using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Audit;

public sealed class UsageAuditAnalyzer
{
    private readonly DeclarationCatalog _catalog;

    public UsageAuditAnalyzer(DeclarationCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public UsageAuditReport Analyze(GraphSnapshot graph, AuditOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new AuditOptions();

        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var incomingEdges = graph.Edges
            .GroupBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var results = new List<UsageAuditResult>();

        foreach (var target in _catalog.Declarations.Where(IsAuditable).OrderBy(item => item.Identity.CanonicalKey, StringComparer.Ordinal))
        {
            incomingEdges.TryGetValue(target.Node.Id, out var targetEdges);
            targetEdges ??= Array.Empty<GraphEdge>();
            var callers = BuildCallers(targetEdges, nodes, options.TestNamespacePolicy);
            var isConfiguredRoot = options.ProductionRootNodeIds.Contains(target.Node.Id);
            var classification = Classify(callers, isConfiguredRoot);
            var warnings = BuildWarnings(targetEdges, classification, isConfiguredRoot);
            results.Add(new UsageAuditResult(target, classification, callers, isConfiguredRoot, warnings));
        }

        return new UsageAuditReport(results);
    }

    private CallerUsage[] BuildCallers(
        IEnumerable<GraphEdge> edges,
        IReadOnlyDictionary<string, GraphNode> nodes,
        NamespaceTestPolicy policy)
    {
        return edges
            .GroupBy(edge => edge.SourceId, StringComparer.Ordinal)
            .Select(group => BuildCaller(group.Key, group, nodes, policy))
            .OrderBy(caller => caller.CallerNodeId, StringComparer.Ordinal)
            .ToArray();
    }

    private CallerUsage BuildCaller(
        string callerNodeId,
        IEnumerable<GraphEdge> edges,
        IReadOnlyDictionary<string, GraphNode> nodes,
        NamespaceTestPolicy policy)
    {
        var callerDeclaration = _catalog.TryGetByNodeId(callerNodeId, out var declaration) ? declaration : null;
        var hasExternalNode = nodes.TryGetValue(callerNodeId, out var node) && node.Kind == GraphNodeKind.ExternalRoot;
        var callerNamespace = callerDeclaration?.Identity.Namespace;
        var classification = callerDeclaration is not null
            ? policy.Classify(callerNamespace)
            : CallerClassification.External;
        var label = callerDeclaration?.Node.Label ?? node?.Label ?? $"<external:{callerNodeId}>";

        return new CallerUsage(
            callerNodeId,
            label,
            callerNamespace,
            hasExternalNode || callerDeclaration is null ? CallerClassification.External : classification,
            edges.Select(edge => edge.Relation),
            edges.Select(edge => edge.Evidence),
            edges.SelectMany(edge => edge.SourceLocations));
    }

    private static UsageClassification Classify(IReadOnlyList<CallerUsage> callers, bool isConfiguredRoot)
    {
        if (isConfiguredRoot)
        {
            return UsageClassification.ProductionUsed;
        }

        if (callers.Count == 0)
        {
            return UsageClassification.ZeroReferences;
        }

        var hasProduction = callers.Any(caller => caller.Classification is CallerClassification.Production or CallerClassification.External);
        var hasTests = callers.Any(caller => caller.Classification == CallerClassification.Test);
        return (hasProduction, hasTests) switch
        {
            (true, true) => UsageClassification.Mixed,
            (true, false) => UsageClassification.ProductionUsed,
            (false, true) => UsageClassification.TestOnly,
            _ => UsageClassification.ZeroReferences,
        };
    }

    private static AuditWarningKind[] BuildWarnings(
        IReadOnlyCollection<GraphEdge> edges,
        UsageClassification classification,
        bool isConfiguredRoot)
    {
        var warnings = new List<AuditWarningKind>();
        if (classification == UsageClassification.ZeroReferences && !isConfiguredRoot)
        {
            warnings.Add(AuditWarningKind.PotentialDynamicReference);
        }

        if (edges.Any(edge => edge.Evidence == EvidenceKind.Ambiguous))
        {
            warnings.Add(AuditWarningKind.AmbiguousEvidence);
        }

        return warnings.ToArray();
    }

    private static bool IsAuditable(SymbolDeclaration declaration) => declaration.Identity.Kind != SymbolKind.Namespace;
}
