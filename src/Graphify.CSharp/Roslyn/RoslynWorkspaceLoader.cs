using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Graphify.CSharp.Roslyn;

public sealed class RoslynWorkspaceLoader : IProjectLoader
{
    private static readonly object RegistrationGate = new();

    public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureMsBuildRegistered();

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

        try
        {
            var projects = await OpenProjectsAsync(workspace, request, cancellationToken).ConfigureAwait(false);
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
                var identity = Domain.ProjectIdentity.FromPath(projectPath, request.RepositoryRoot, request.TargetFramework);
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

            return new LoadedSolution(workspace, analyzedProjects, request.RepositoryRoot, diagnostics);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static async Task<IReadOnlyList<Project>> OpenProjectsAsync(
        MSBuildWorkspace workspace,
        ProjectLoadRequest request,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(request.InputPath);
        return extension.ToLowerInvariant() switch
        {
            ".sln" or ".slnx" => (await workspace.OpenSolutionAsync(request.InputPath, cancellationToken: cancellationToken).ConfigureAwait(false)).Projects.ToArray(),
            ".csproj" => await OpenProjectAndReferencesAsync(workspace, request.InputPath, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Input must be a .sln, .slnx, or .csproj file.", nameof(request)),
        };
    }

    private static async Task<IReadOnlyList<Project>> OpenProjectAndReferencesAsync(
        MSBuildWorkspace workspace,
        string projectPath,
        CancellationToken cancellationToken)
    {
        await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        return workspace.CurrentSolution.Projects.ToArray();
    }

    private static void EnsureMsBuildRegistered()
    {
        lock (RegistrationGate)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }
}
