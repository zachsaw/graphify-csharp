using System.Collections.Immutable;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Audit;

public sealed class CallerUsage
{
    public CallerUsage(
        string callerNodeId,
        string callerLabel,
        string? callerNamespace,
        CallerClassification classification,
        IEnumerable<GraphRelation> relations,
        IEnumerable<EvidenceKind> evidence,
        IEnumerable<SourceLocation>? sourceLocations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerLabel);

        CallerNodeId = callerNodeId;
        CallerLabel = callerLabel;
        CallerNamespace = callerNamespace;
        Classification = classification;
        Relations = (relations ?? throw new ArgumentNullException(nameof(relations)))
            .Distinct()
            .OrderBy(relation => relation)
            .ToImmutableArray();
        Evidence = (evidence ?? throw new ArgumentNullException(nameof(evidence)))
            .Distinct()
            .OrderBy(kind => kind)
            .ToImmutableArray();
        SourceLocations = (sourceLocations ?? Array.Empty<SourceLocation>())
            .Distinct()
            .OrderBy(location => location.FilePath, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToImmutableArray();
    }

    public string CallerNodeId { get; }

    public string CallerLabel { get; }

    public string? CallerNamespace { get; }

    public CallerClassification Classification { get; }

    public ImmutableArray<GraphRelation> Relations { get; }

    public ImmutableArray<EvidenceKind> Evidence { get; }

    public ImmutableArray<SourceLocation> SourceLocations { get; }
}
