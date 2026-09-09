using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Graphify.CSharp.Roslyn;

public sealed class RoslynWorkspaceLoader : IProjectLoader
{
    public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MsBuildEnvironment.EnsureRegistered();

        var diagnostics = new List<WorkspaceLoadDiagnostic>();
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
        workspace.RegisterWorkspaceFailedHandler(args => diagnostics.Add(new WorkspaceLoadDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message)));
        WorkspaceOpenResult? opened = null;

        try
        {
            opened = await OpenProjectsAsync(workspace, request, cancellationToken).ConfigureAwait(false);
            var projects = opened.Projects;
            var analyzedProjects = new List<AnalyzedProject>(projects.Count);
            var seenProjectKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in projects.OrderBy(project => project.FilePath ?? project.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
                {
                    continue;
                }

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    diagnostics.Add(new WorkspaceLoadDiagnostic("Compilation", $"Roslyn did not produce a compilation for '{project.Name}'."));
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
                opened.Resources);
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
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(request.InputPath);
        return extension.ToLowerInvariant() switch
        {
            ".sln" or ".slnx" => new WorkspaceOpenResult(
                (await workspace.OpenSolutionAsync(request.InputPath, cancellationToken: cancellationToken).ConfigureAwait(false)).Projects.ToArray(),
                request.InputPath,
                request.InputPath,
                Resources: null),
            ".csproj" => new WorkspaceOpenResult(
                await OpenProjectAndReferencesAsync(workspace, request.InputPath, cancellationToken).ConfigureAwait(false),
                request.InputPath,
                request.InputPath,
                Resources: null),
            ".cs" => await OpenFileBasedAppAsync(workspace, request, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Input must be a .sln, .slnx, .csproj, or file-based .cs app.", nameof(request)),
        };
    }

    private static async Task<WorkspaceOpenResult> OpenFileBasedAppAsync(
        MSBuildWorkspace workspace,
        ProjectLoadRequest request,
        CancellationToken cancellationToken)
    {
        var generated = await FileBasedAppProject.CreateAsync(
            request.InputPath,
            request.TargetFramework,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await workspace.OpenProjectAsync(generated.ProjectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var remappedSolution = await generated.RemapDocumentsAsync(
                workspace.CurrentSolution,
                cancellationToken).ConfigureAwait(false);
            return new WorkspaceOpenResult(
                remappedSolution.Projects.ToArray(),
                generated.ProjectPath,
                request.InputPath,
                generated);
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
        CancellationToken cancellationToken)
    {
        await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        return workspace.CurrentSolution.Projects.ToArray();
    }

    private sealed record WorkspaceOpenResult(
        IReadOnlyList<Project> Projects,
        string PrimaryProjectPath,
        string? LogicalProjectPath,
        IDisposable? Resources);

}
