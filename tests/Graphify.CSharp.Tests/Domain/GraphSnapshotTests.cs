using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Tests.Domain;

public sealed class GraphSnapshotTests
{
    [Fact]
    public void Snapshot_merges_duplicate_nodes_and_edges_deterministically()
    {
        var project = new ProjectIdentity("src/App/App.csproj", "net10.0");
        var caller = new SymbolIdentity(project, "App", [new ContainingTypeIdentity("Caller")], SymbolKind.Method, "Run");
        var callee = new SymbolIdentity(project, "App", [new ContainingTypeIdentity("Callee")], SymbolKind.Method, "Run");
        var callerNode = GraphNode.ForSymbol(caller, [new SourceLocation("src/App/Caller.cs", 20, 9)]);
        var calleeNode = GraphNode.ForSymbol(callee);
        var firstLocation = new SourceLocation("src/App/Caller.cs", 22, 13);
        var secondLocation = new SourceLocation("src/App/Caller.cs", 23, 13);

        var snapshot = GraphSnapshot.Create(
            [calleeNode, callerNode, callerNode],
            [
                new GraphEdge(callerNode.Id, calleeNode.Id, GraphRelation.Calls, EvidenceKind.Extracted, 0.9, [firstLocation]),
                new GraphEdge(callerNode.Id, calleeNode.Id, GraphRelation.Calls, EvidenceKind.Extracted, 1.0, [secondLocation]),
            ]);

        Assert.Equal(2, snapshot.Nodes.Length);
        Assert.Single(snapshot.Edges);
        Assert.Equal(1.0, snapshot.Edges[0].Confidence);
        Assert.True(new[] { firstLocation, secondLocation }.SequenceEqual(snapshot.Edges[0].SourceLocations));
        Assert.Equal(
            snapshot.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).Select(node => node.Id),
            snapshot.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void Graph_edge_rejects_invalid_confidence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphEdge("a", "b", GraphRelation.Calls, EvidenceKind.Extracted, 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphEdge("a", "b", GraphRelation.Calls, EvidenceKind.Extracted, double.NaN));
    }
}
