using System.Collections.Immutable;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Roslyn;

internal sealed class ExtractedProjectContribution
{
    public ExtractedProjectContribution(
        ProjectIdentity project,
        GraphSnapshot graph,
        IEnumerable<string>? diagnostics = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Diagnostics = (diagnostics ?? Array.Empty<string>())
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(diagnostic => diagnostic.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public ProjectIdentity Project { get; }

    public GraphSnapshot Graph { get; }

    public ImmutableArray<string> Diagnostics { get; }
}
