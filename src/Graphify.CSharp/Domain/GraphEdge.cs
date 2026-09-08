using System.Collections.Immutable;

namespace Graphify.CSharp.Domain;

public enum GraphRelation
{
    Calls,
    References,
    Inherits,
    Implements,
    Overrides,
    DispatchesTo,
}

public enum EvidenceKind
{
    Extracted,
    Inferred,
    Ambiguous,
}

public sealed class GraphEdge
{
    public GraphEdge(
        string sourceId,
        string targetId,
        GraphRelation relation,
        EvidenceKind evidence,
        double confidence,
        IEnumerable<SourceLocation>? sourceLocations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be finite and between 0 and 1.");
        }

        SourceId = sourceId;
        TargetId = targetId;
        Relation = relation;
        Evidence = evidence;
        Confidence = confidence;
        SourceLocations = (sourceLocations ?? Array.Empty<SourceLocation>())
            .Distinct()
            .OrderBy(location => location.FilePath, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToImmutableArray();
    }

    public string SourceId { get; }

    public string TargetId { get; }

    public GraphRelation Relation { get; }

    public EvidenceKind Evidence { get; }

    public double Confidence { get; }

    public ImmutableArray<SourceLocation> SourceLocations { get; }

    public string DeduplicationKey => string.Join('\u001F', SourceId, TargetId, Relation, Evidence);

    public GraphEdge Merge(GraphEdge other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(DeduplicationKey, other.DeduplicationKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cannot merge graph edges with different identities.");
        }

        return new GraphEdge(
            SourceId,
            TargetId,
            Relation,
            Evidence,
            Math.Max(Confidence, other.Confidence),
            SourceLocations.Concat(other.SourceLocations));
    }
}
