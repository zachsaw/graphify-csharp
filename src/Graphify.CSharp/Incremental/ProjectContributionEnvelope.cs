using System.Collections.Immutable;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal sealed class ProjectContributionEnvelope
{
    public ProjectContributionEnvelope(
        ProjectFingerprint fingerprint,
        GraphSnapshot graph,
        IEnumerable<string>? diagnostics = null,
        bool isComplete = true)
    {
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Diagnostics = (diagnostics ?? Array.Empty<string>())
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(diagnostic => diagnostic.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
        IsComplete = isComplete;
    }

    public ProjectIdentity Project => Fingerprint.Project;

    public ProjectFingerprint Fingerprint { get; }

    public GraphSnapshot Graph { get; }

    public ImmutableArray<string> Diagnostics { get; }

    public bool IsComplete { get; }

    public string ContributionKey => IncrementalHashing.Sha256(Project.Key);
}
