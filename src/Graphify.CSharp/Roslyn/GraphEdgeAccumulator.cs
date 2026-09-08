using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Roslyn;

internal sealed class GraphEdgeAccumulator : ICollection<GraphEdge>
{
    private readonly Dictionary<string, GraphEdge> _edges = new(StringComparer.Ordinal);

    public int Count => _edges.Count;

    public bool IsReadOnly => false;

    public void Add(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        _edges[edge.DeduplicationKey] = _edges.TryGetValue(edge.DeduplicationKey, out var existing)
            ? existing.Merge(edge)
            : edge;
    }

    public void Clear() => _edges.Clear();

    public bool Contains(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return _edges.ContainsKey(edge.DeduplicationKey);
    }

    public void CopyTo(GraphEdge[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        _edges.Values.CopyTo(array, arrayIndex);
    }

    public bool Remove(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return _edges.Remove(edge.DeduplicationKey);
    }

    public IEnumerator<GraphEdge> GetEnumerator() => _edges.Values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
