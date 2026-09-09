using System.Collections.Immutable;
using Microsoft.CodeAnalysis.MSBuild;

namespace Graphify.CSharp.Roslyn;

public sealed class LoadedSolution : IDisposable
{
    public LoadedSolution(
        MSBuildWorkspace workspace,
        IEnumerable<AnalyzedProject> projects,
        string repositoryRoot,
        IEnumerable<WorkspaceLoadDiagnostic>? diagnostics = null,
        IDisposable? resources = null)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Projects = (projects ?? throw new ArgumentNullException(nameof(projects))).ToImmutableArray();
        RepositoryRoot = Path.GetFullPath(repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot)));
        Diagnostics = (diagnostics ?? Array.Empty<WorkspaceLoadDiagnostic>()).ToImmutableArray();
        Resources = resources;
    }

    public MSBuildWorkspace Workspace { get; }

    public ImmutableArray<AnalyzedProject> Projects { get; private set; }

    public string RepositoryRoot { get; }

    public ImmutableArray<WorkspaceLoadDiagnostic> Diagnostics { get; }

    internal void ReplaceProjects(IEnumerable<AnalyzedProject> projects)
    {
        Projects = (projects ?? throw new ArgumentNullException(nameof(projects))).ToImmutableArray();
    }

    private IDisposable? Resources { get; }

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
