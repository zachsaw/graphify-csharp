using System.Collections.Concurrent;
using Graphify.CSharp.Incremental;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Graphify.CSharp.Roslyn;

public sealed class RoslynWorkspaceLoader : IProjectLoader
{
    public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        => await LoadCoreAsync(request, operation: null, cancellationToken).ConfigureAwait(false);

    internal async Task<LoadedSolution> LoadAsync(
        ProjectLoadRequest request,
        IndexingObservationOperation? operation,
        CancellationToken cancellationToken = default)
        => await LoadCoreAsync(request, operation, cancellationToken).ConfigureAwait(false);

    private async Task<LoadedSolution> LoadCoreAsync(
        ProjectLoadRequest request,
        IndexingObservationOperation? operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        MsBuildEnvironment.EnsureRegistered();

        var diagnostics = new ConcurrentQueue<WorkspaceLoadDiagnostic>();
        var workspaceProperties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Configuration"] = request.Configuration,
            ["DesignTimeBuild"] = "true",
            ["BuildingProject"] = "false",
        };
        if (request.TargetFramework is not null)
        {
            workspaceProperties["TargetFramework"] = request.TargetFramework;
        }

        var workspace = MSBuildWorkspace.Create(workspaceProperties);
        workspace.RegisterWorkspaceFailedHandler(args => diagnostics.Enqueue(new WorkspaceLoadDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message)));
        WorkspaceOpenResult? opened = null;

        try
        {
            using var loadPhase = operation?.BeginPhase(IndexingStages.LoadingProjects);
            opened = await OpenProjectsAsync(workspace, request, loadPhase, cancellationToken).ConfigureAwait(false);
            var projects = opened.Projects;
            var analyzedProjects = new List<AnalyzedProject>(projects.Count);
            var seenProjectKeys = new HashSet<string>(StringComparer.Ordinal);
            var completedProjects = 0;
            var csharpProjectCount = projects.Count(project =>
                string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal));
            foreach (var project in projects.OrderBy(project => project.FilePath ?? project.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
                {
                    continue;
                }

                loadPhase?.SetStage(IndexingStages.Compiling, project.Name);
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    diagnostics.Enqueue(new WorkspaceLoadDiagnostic("Compilation", $"Roslyn did not produce a compilation for '{project.Name}'."));
                    continue;
                }

                var projectPath = project.FilePath ?? throw new InvalidOperationException($"Project '{project.Name}' has no file path.");
                var targetFramework = new TargetFrameworkResolver().Resolve(
                    projectPath,
                    request.Configuration,
                    request.TargetFramework);
                var identityPath = opened.LogicalProjectPath is not null
                    && string.Equals(
                        Path.GetFullPath(projectPath),
                        Path.GetFullPath(opened.PrimaryProjectPath),
                        StringComparison.Ordinal)
                    ? opened.LogicalProjectPath
                    : projectPath;
                var identity = Domain.ProjectIdentity.FromPath(identityPath, request.RepositoryRoot, targetFramework);
                if (!seenProjectKeys.Add(identity.Key))
                {
                    continue;
                }

                analyzedProjects.Add(new AnalyzedProject(project, identity, compilation));
                loadPhase?.ReportWork(++completedProjects, csharpProjectCount, "projects", project.Name);
            }

            if (analyzedProjects.Count == 0)
            {
                throw new InvalidOperationException($"No C# projects could be loaded from '{request.InputPath}'.");
            }

            return new LoadedSolution(
                workspace,
                analyzedProjects,
                request.RepositoryRoot,
                diagnostics,
                opened.Resources,
                request,
                opened.TransientRoots);
        }
        catch
        {
            workspace.Dispose();
            opened?.Resources?.Dispose();
            throw;
        }
    }

    private static async Task<WorkspaceOpenResult> OpenProjectsAsync(
        MSBuildWorkspace workspace,
        ProjectLoadRequest request,
        IndexingObservationPhase? phase,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(request.InputPath);
        return extension.ToLowerInvariant() switch
        {
            ".sln" or ".slnx" => new WorkspaceOpenResult(
                (await OpenSolutionAsync(workspace, request.InputPath, phase, cancellationToken).ConfigureAwait(false))
                    .Projects
                    .ToArray(),
                request.InputPath,
                request.InputPath,
                Resources: null),
            ".csproj" => new WorkspaceOpenResult(
                await OpenProjectAndReferencesAsync(workspace, request.InputPath, phase, cancellationToken).ConfigureAwait(false),
                request.InputPath,
                request.InputPath,
                Resources: null),
            ".cs" => await OpenFileBasedAppAsync(workspace, request, phase, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Input must be a .sln, .slnx, .csproj, or file-based .cs app.", nameof(request)),
        };
    }

    private static async Task<Solution> OpenSolutionAsync(
        MSBuildWorkspace workspace,
        string solutionPath,
        IndexingObservationPhase? phase,
        CancellationToken cancellationToken)
    {
        try
        {
            return await workspace.OpenSolutionAsync(
                    solutionPath,
                    new InlineProgress<ProjectLoadProgress>(progress => ReportLoadProgress(phase, progress)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SolutionLoadException(solutionPath, exception);
        }
    }

    private static async Task<WorkspaceOpenResult> OpenFileBasedAppAsync(
        MSBuildWorkspace workspace,
        ProjectLoadRequest request,
        IndexingObservationPhase? phase,
        CancellationToken cancellationToken)
    {
        var generated = await FileBasedAppProject.CreateAsync(
            request.InputPath,
            request.TargetFramework,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await workspace.OpenProjectAsync(
                    generated.ProjectPath,
                    new InlineProgress<ProjectLoadProgress>(progress => ReportLoadProgress(phase, progress)),
                    cancellationToken)
                .ConfigureAwait(false);
            var remappedSolution = await generated.RemapDocumentsAsync(
                workspace.CurrentSolution,
                cancellationToken).ConfigureAwait(false);
            return new WorkspaceOpenResult(
                remappedSolution.Projects.ToArray(),
                generated.ProjectPath,
                request.InputPath,
                generated,
                [generated.TemporaryDirectory]);
        }
        catch
        {
            generated.Dispose();
            throw;
        }
    }

    private static async Task<IReadOnlyList<Project>> OpenProjectAndReferencesAsync(
        MSBuildWorkspace workspace,
        string projectPath,
        IndexingObservationPhase? phase,
        CancellationToken cancellationToken)
    {
        await workspace.OpenProjectAsync(
                projectPath,
                new InlineProgress<ProjectLoadProgress>(progress => ReportLoadProgress(phase, progress)),
                cancellationToken)
            .ConfigureAwait(false);
        return workspace.CurrentSolution.Projects.ToArray();
    }

    private static void ReportLoadProgress(
        IndexingObservationPhase? phase,
        ProjectLoadProgress progress)
    {
        if (phase is null)
        {
            return;
        }

        var stage = progress.Operation == ProjectLoadOperation.Build
            ? IndexingStages.Compiling
            : IndexingStages.LoadingProjects;
        var name = string.IsNullOrWhiteSpace(progress.FilePath)
            ? null
            : Path.GetFileName(progress.FilePath);
        phase.SetStage(stage, name);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record WorkspaceOpenResult(
        IReadOnlyList<Project> Projects,
        string PrimaryProjectPath,
        string? LogicalProjectPath,
        IDisposable? Resources,
        IReadOnlyList<string>? TransientRoots = null);

}
