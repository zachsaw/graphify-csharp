using System.Collections.Immutable;
using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Incremental;

/// <summary>
/// Immutable lookup data for one installed semantic evidence revision.
/// It is built by the session worker and read only by commands running on that
/// worker; no pipe thread ever observes the mutable session dictionaries.
/// </summary>
internal sealed class SemanticEvidenceIndex
{
    private SemanticEvidenceIndex(
        ImmutableArray<SymbolDeclaration> declarations,
        ImmutableArray<GraphEdge> edges,
        ImmutableDictionary<string, SymbolDeclaration> declarationsById,
        ImmutableDictionary<string, ImmutableArray<GraphEdge>> incoming,
        ImmutableDictionary<string, ImmutableArray<GraphEdge>> outgoing)
    {
        Declarations = declarations;
        Edges = edges;
        DeclarationsById = declarationsById;
        Incoming = incoming;
        Outgoing = outgoing;
    }

    public ImmutableArray<SymbolDeclaration> Declarations { get; }

    public ImmutableArray<GraphEdge> Edges { get; }

    public ImmutableDictionary<string, SymbolDeclaration> DeclarationsById { get; }

    public ImmutableDictionary<string, ImmutableArray<GraphEdge>> Incoming { get; }

    public ImmutableDictionary<string, ImmutableArray<GraphEdge>> Outgoing { get; }

    // Summary maps are deliberately built on demand. Most agent queries are
    // symbol, signature, usage, or hierarchy lookups and never need a summary
    // map. The session worker is the sole owner of this index, so lazy fields
    // do not need locks and a cancelled build is never published.
    private ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>>? _ungroupedSummaries;

    private ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>>? _projectSummaries;

    private ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>>? _namespaceSummaries;

    private ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>>? _projectNamespaceSummaries;

    private ImmutableArray<SymbolDeclaration>? _declarationsByDisplayName;

    public static SemanticEvidenceIndex Create(
        DeclarationCatalog catalog,
        IEnumerable<ProjectContributionEnvelope> contributions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(contributions);

        var declarations = catalog.Declarations
            .OrderBy(declaration => declaration.Node.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var declarationsById = declarations.ToImmutableDictionary(
            declaration => declaration.Node.Id,
            StringComparer.Ordinal);
        var edgesByKey = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        foreach (var contribution in contributions.OrderBy(item => item.Project.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var edge in contribution.Graph.Edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edgesByKey.TryGetValue(edge.DeduplicationKey, out var existing))
                {
                    edgesByKey[edge.DeduplicationKey] = existing.Merge(edge);
                }
                else
                {
                    edgesByKey.Add(edge.DeduplicationKey, edge);
                }
            }
        }

        var edges = edgesByKey.Values
            .OrderBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Relation)
            .ThenBy(edge => edge.Evidence)
            .ToImmutableArray();
        var incoming = BuildEdgeIndex(edges, edge => edge.TargetId);
        var outgoing = BuildEdgeIndex(edges, edge => edge.SourceId);
        return new SemanticEvidenceIndex(
            declarations,
            edges,
            declarationsById,
            incoming,
            outgoing);
    }

    public ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>> GetSummaries(
        SummaryGrouping grouping,
        CancellationToken cancellationToken = default) =>
        grouping switch
        {
            SummaryGrouping.None => _ungroupedSummaries ??= BuildSummaries(
                Declarations,
                DeclarationsById,
                Incoming,
                SummaryGrouping.None,
                cancellationToken),
            SummaryGrouping.Project => _projectSummaries ??= BuildSummaries(
                Declarations,
                DeclarationsById,
                Incoming,
                SummaryGrouping.Project,
                cancellationToken),
            SummaryGrouping.Namespace => _namespaceSummaries ??= BuildSummaries(
                Declarations,
                DeclarationsById,
                Incoming,
                SummaryGrouping.Namespace,
                cancellationToken),
            SummaryGrouping.Project | SummaryGrouping.Namespace => _projectNamespaceSummaries ??= BuildSummaries(
                Declarations,
                DeclarationsById,
                Incoming,
                SummaryGrouping.Project | SummaryGrouping.Namespace,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "Unsupported summary grouping."),
        };

    public ImmutableArray<SymbolDeclaration> GetDeclarationsByDisplayName(
        CancellationToken cancellationToken = default)
    {
        if (_declarationsByDisplayName is { } cached)
        {
            return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ordered = Declarations
            .OrderBy(declaration => declaration.Identity.DisplayName, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Node.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        return _declarationsByDisplayName ??= ordered;
    }

    private static ImmutableDictionary<string, ImmutableArray<GraphEdge>> BuildEdgeIndex(
        IEnumerable<GraphEdge> edges,
        Func<GraphEdge, string> keySelector)
    {
        var grouped = edges
            .GroupBy(keySelector, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Relation)
                    .ThenBy(edge => edge.Evidence)
                    .ToImmutableArray(),
                StringComparer.Ordinal);
        return grouped.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>> BuildSummaries(
        ImmutableArray<SymbolDeclaration> declarations,
        ImmutableDictionary<string, SymbolDeclaration> declarationsById,
        ImmutableDictionary<string, ImmutableArray<GraphEdge>> incoming,
        SummaryGrouping grouping,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ImmutableArray<SemanticSummaryAggregate>>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edges = incoming.GetValueOrDefault(declaration.Node.Id, []);
            if (grouping == SummaryGrouping.None)
            {
                result.Add(
                    declaration.Node.Id,
                    [SemanticSummaryAggregate.Create(edges, declarationsById, group: null)]);
                continue;
            }

            var grouped = new Dictionary<SemanticSummaryGroupKey, List<GraphEdge>>(
                SemanticSummaryGroupKeyComparer.Instance);
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SemanticSummaryAggregate.IsSupportedRelation(edge.Relation))
                {
                    continue;
                }

                var key = SemanticSummaryGroupKey.Create(edge, declarationsById, grouping);
                if (!grouped.TryGetValue(key, out var groupEdges))
                {
                    groupEdges = [];
                    grouped.Add(key, groupEdges);
                }

                groupEdges.Add(edge);
            }

            result.Add(
                declaration.Node.Id,
                grouped
                    .OrderBy(pair => pair.Key, SemanticSummaryGroupKeyComparer.Instance)
                    .Select(pair => SemanticSummaryAggregate.Create(pair.Value, declarationsById, pair.Key))
                    .ToImmutableArray());
        }

        return result.ToImmutableDictionary(StringComparer.Ordinal);
    }
}

[Flags]
internal enum SummaryGrouping
{
    None = 0,
    Project = 1,
    Namespace = 2,
}

internal sealed record SemanticSummaryGroupKey(
    string? Project,
    string? Namespace,
    string? TargetFramework,
    bool IsExternalRoot)
{
    public static SemanticSummaryGroupKey Create(
        GraphEdge edge,
        ImmutableDictionary<string, SymbolDeclaration> declarationsById,
        SummaryGrouping grouping)
    {
        if (!declarationsById.TryGetValue(edge.SourceId, out var origin))
        {
            return new SemanticSummaryGroupKey(null, null, null, IsExternalRoot: true);
        }

        return new SemanticSummaryGroupKey(
            grouping.HasFlag(SummaryGrouping.Project) ? origin.Identity.Project.RelativePath : null,
            grouping.HasFlag(SummaryGrouping.Namespace) ? origin.Identity.Namespace : null,
            origin.Identity.Project.TargetFramework,
            IsExternalRoot: false);
    }
}

internal sealed class SemanticSummaryGroupKeyComparer : IComparer<SemanticSummaryGroupKey>, IEqualityComparer<SemanticSummaryGroupKey>
{
    public static SemanticSummaryGroupKeyComparer Instance { get; } = new();

    public int Compare(SemanticSummaryGroupKey? x, SemanticSummaryGroupKey? y)
    {
        var result = string.CompareOrdinal(x?.Project, y?.Project);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(x?.Namespace, y?.Namespace);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(x?.TargetFramework, y?.TargetFramework);
        return result != 0
            ? result
            : Nullable.Compare(x?.IsExternalRoot, y?.IsExternalRoot);
    }

    public bool Equals(SemanticSummaryGroupKey? x, SemanticSummaryGroupKey? y) =>
        x is not null
        && y is not null
        && Compare(x, y) == 0;

    public int GetHashCode(SemanticSummaryGroupKey obj) =>
        HashCode.Combine(obj.Project, obj.Namespace, obj.TargetFramework, obj.IsExternalRoot);
}

internal sealed record SemanticSummaryAggregate(
    SemanticRelationCounts Calls,
    SemanticRelationCounts References,
    SemanticRelationCounts Inherits,
    SemanticRelationCounts Implements,
    SemanticRelationCounts Overrides,
    int DistinctOriginCount,
    string? OriginProject,
    string? OriginNamespace,
    string? OriginTargetFramework,
    bool IsExternalRoot)
{
    private static readonly GraphRelation[] SupportedRelations =
    [
        GraphRelation.Calls,
        GraphRelation.References,
        GraphRelation.Inherits,
        GraphRelation.Implements,
        GraphRelation.Overrides,
    ];

    public static SemanticSummaryAggregate Create(
        IEnumerable<GraphEdge> edges,
        ImmutableDictionary<string, SymbolDeclaration> declarationsById,
        SemanticSummaryGroupKey? group)
    {
        var selected = edges
            .Where(edge => IsSupportedRelation(edge.Relation))
            .ToArray();

        SemanticRelationCounts Counts(GraphRelation relation)
        {
            var relationEdges = selected.Where(edge => edge.Relation == relation).ToArray();
            return new SemanticRelationCounts(
                relationEdges.Length,
                relationEdges.Sum(edge => edge.SourceLocations.Length));
        }

        return new SemanticSummaryAggregate(
            Counts(GraphRelation.Calls),
            Counts(GraphRelation.References),
            Counts(GraphRelation.Inherits),
            Counts(GraphRelation.Implements),
            Counts(GraphRelation.Overrides),
            selected.Select(edge => edge.SourceId).Distinct(StringComparer.Ordinal).Count(),
            group?.Project,
            group?.Namespace,
            group?.TargetFramework,
            group?.IsExternalRoot ?? false);
    }

    public static bool IsSupportedRelation(GraphRelation relation) => SupportedRelations.Contains(relation);
}

internal sealed record SemanticEvidenceView(
    Guid SessionId,
    string Mode,
    string AnalysisKey,
    LoadedSolution Solution,
    DeclarationCatalog Catalog,
    SemanticEvidenceIndex Index,
    WatcherInputSnapshot InputSnapshot,
    RefreshGeneration Generation,
    long TargetEventGeneration,
    long EvidenceRevision,
    byte[] CursorSecret,
    IReadOnlyList<string>? GlobalDiagnostics = null,
    IReadOnlyList<string>? ContributionDiagnostics = null)
{
    public string SnapshotId => $"{SessionId:D}:{EvidenceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public ImmutableArray<string> Diagnostics => Solution.Diagnostics
        .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
        .Concat(InputSnapshot.InputDiscoveryDiagnostics)
        .Concat(Catalog.Diagnostics)
        .Concat(GlobalDiagnostics ?? Array.Empty<string>())
        .Concat(ContributionDiagnostics ?? Array.Empty<string>())
        .Distinct(StringComparer.Ordinal)
        .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
        .ToImmutableArray();
}
