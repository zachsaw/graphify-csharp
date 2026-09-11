using System.Collections.Concurrent;
using System.Collections.Immutable;
using Graphify.CSharp.Incremental;
using Microsoft.CodeAnalysis.MSBuild;

namespace Graphify.CSharp.Roslyn;

public sealed class LoadedSolution : IDisposable
{
    public LoadedSolution(
        MSBuildWorkspace workspace,
        IEnumerable<AnalyzedProject> projects,
        string repositoryRoot,
        IEnumerable<WorkspaceLoadDiagnostic>? diagnostics = null,
        IDisposable? resources = null,
        ProjectLoadRequest? request = null,
        IEnumerable<string>? transientRoots = null)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Projects = (projects ?? throw new ArgumentNullException(nameof(projects))).ToImmutableArray();
        RepositoryRoot = Path.GetFullPath(repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot)));
        Diagnostics = (diagnostics ?? Array.Empty<WorkspaceLoadDiagnostic>()).ToImmutableArray();
        Resources = resources;
        Request = request;
        TransientRoots = (transientRoots ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(IncrementalPaths.CanonicalAbsolutePath)
            .Distinct(IncrementalPaths.PathComparer)
            .OrderBy(path => path, IncrementalPaths.PathComparer)
            .ToImmutableArray();
    }

    public MSBuildWorkspace Workspace { get; }

    public ImmutableArray<AnalyzedProject> Projects { get; private set; }

    public string RepositoryRoot { get; }

    public ImmutableArray<WorkspaceLoadDiagnostic> Diagnostics { get; }

    internal ProjectLoadRequest? Request { get; }

    internal ImmutableArray<string> TransientRoots { get; }

    internal bool IsTransientPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = IncrementalPaths.CanonicalAbsolutePath(path);
        return TransientRoots.Any(root => IncrementalPaths.IsPathOrUnder(canonicalPath, root));
    }

    internal void ReplaceProjects(IEnumerable<AnalyzedProject> projects)
    {
        Projects = (projects ?? throw new ArgumentNullException(nameof(projects))).ToImmutableArray();
    }

    private IDisposable? Resources { get; }

    private readonly ConcurrentDictionary<string, ProjectInputDiscoveryResult> _inputDiscoveries = new(StringComparer.Ordinal);

    internal ProjectInputDiscoveryResult GetInputDiscovery(AnalyzedProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var discovery = _inputDiscoveries.GetOrAdd(
            project.Identity.Key,
            _ => RoslynProjectInputDiscovery.Discover(
                project.Project,
                GetProjectDirectory(project),
                Request));
        if (TransientRoots.Length == 0)
        {
            return discovery;
        }

        return discovery with
        {
            Paths = discovery.Paths
                .Where(path => !IsTransientPath(path))
                .ToImmutableArray(),
            Globs = discovery.Globs
                .Where(glob => !IsTransientPath(glob.CanonicalBaseDirectory))
                .ToImmutableArray(),
            InfrastructurePaths = discovery.InfrastructurePaths
                .Where(path => !IsTransientPath(path))
                .ToImmutableArray(),
            ExplicitSemanticPaths = discovery.ExplicitSemanticPaths
                .Where(path => !IsTransientPath(path))
                .ToImmutableArray(),
        };
    }

    private string GetProjectDirectory(AnalyzedProject project)
    {
        var projectPath = project.Project.FilePath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            projectPath = Path.Combine(
                RepositoryRoot,
                project.Identity.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.GetDirectoryName(IncrementalPaths.CanonicalAbsolutePath(projectPath))
            ?? IncrementalPaths.CanonicalAbsolutePath(RepositoryRoot);
    }

    public void Dispose()
    {
        try
        {
            Workspace.Dispose();
        }
        finally
        {
            Resources?.Dispose();
        }
    }
}
