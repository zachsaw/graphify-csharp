using System.Collections.Immutable;
using Microsoft.CodeAnalysis.MSBuild;

namespace Graphify.CSharp.Roslyn;

public sealed class LoadedSolution : IDisposable
{
    public LoadedSolution(
        MSBuildWorkspace workspace,
        IEnumerable<AnalyzedProject> projects,
        string repositoryRoot,
        IEnumerable<WorkspaceLoadDiagnostic>? diagnostics = null)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Projects = (projects ?? throw new ArgumentNullException(nameof(projects))).ToImmutableArray();
        RepositoryRoot = Path.GetFullPath(repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot)));
        Diagnostics = (diagnostics ?? Array.Empty<WorkspaceLoadDiagnostic>()).ToImmutableArray();
    }

    public MSBuildWorkspace Workspace { get; }

    public ImmutableArray<AnalyzedProject> Projects { get; }

    public string RepositoryRoot { get; }

    public ImmutableArray<WorkspaceLoadDiagnostic> Diagnostics { get; }

    public void Dispose() => Workspace.Dispose();
}
