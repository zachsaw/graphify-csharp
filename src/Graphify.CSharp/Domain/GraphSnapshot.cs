using System.Collections.Immutable;

namespace Graphify.CSharp.Domain;

public sealed class GraphSnapshot
{
    private GraphSnapshot(ImmutableArray<GraphNode> nodes, ImmutableArray<GraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public ImmutableArray<GraphNode> Nodes { get; }

    public ImmutableArray<GraphEdge> Edges { get; }

    public static GraphSnapshot Create(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var nodeMap = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            nodeMap[node.Id] = nodeMap.TryGetValue(node.Id, out var existing) ? existing.Merge(node) : node;
        }

        var edgeMap = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            edgeMap[edge.DeduplicationKey] = edgeMap.TryGetValue(edge.DeduplicationKey, out var existing)
                ? existing.Merge(edge)
                : edge;
        }

        return new GraphSnapshot(
            nodeMap.Values
                .OrderBy(node => node.Id, StringComparer.Ordinal)
                .ToImmutableArray(),
            edgeMap.Values
                .OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Relation)
                .ThenBy(edge => edge.Evidence)
                .ToImmutableArray());
    }
}
