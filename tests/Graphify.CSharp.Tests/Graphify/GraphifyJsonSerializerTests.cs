using System.Text.Json;
using global::Graphify.CSharp.Domain;
using global::Graphify.CSharp.Graphify;

namespace Graphify.CSharp.Tests.Graphify;

public sealed class GraphifyJsonSerializerTests
{
    [Fact]
    public void Emits_the_current_graphify_base_shape_with_stable_ordering()
    {
        var project = new ProjectIdentity("src/App/App.csproj", "net10.0");
        var first = GraphNode.ForSymbol(
            new SymbolIdentity(project, "App", [new ContainingTypeIdentity("Caller")], SymbolKind.Method, "Run"),
            [new SourceLocation("src/App/Caller.cs", 10, 5)]);
        var second = GraphNode.ForSymbol(
            new SymbolIdentity(project, "App", [new ContainingTypeIdentity("Target")], SymbolKind.Method, "Run"),
            [new SourceLocation("src/App/Target.cs", 20, 5)]);
        var edge = new GraphEdge(first.Id, second.Id, GraphRelation.Calls, EvidenceKind.Extracted, 1.0, [new SourceLocation("src/App/Caller.cs", 12, 9)]);
        var graph = GraphSnapshot.Create([second, first], [edge]);

        var json = new GraphifyJsonSerializer().Serialize(graph);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("directed").GetBoolean());
        Assert.True(document.RootElement.GetProperty("multigraph").GetBoolean());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("nodes").ValueKind);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("edges").ValueKind);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("hyperedges").ValueKind);
        Assert.Equal("code", document.RootElement.GetProperty("nodes")[0].GetProperty("file_type").GetString());
        Assert.Equal("calls", document.RootElement.GetProperty("edges")[0].GetProperty("relation").GetString());
        Assert.Equal("EXTRACTED", document.RootElement.GetProperty("edges")[0].GetProperty("confidence").GetString());
        Assert.Equal("L12:C9", document.RootElement.GetProperty("edges")[0].GetProperty("source_location").GetString());
        Assert.True(document.RootElement.GetProperty("edges")[0].GetProperty("source").GetString() is not null);
        Assert.Equal("csharp/v1", document.RootElement.GetProperty("graphify_csharp").GetProperty("schema_version").GetString());
        Assert.False(document.RootElement.GetProperty("graphify_csharp").TryGetProperty("audit", out _));
        Assert.Equal("App", document.RootElement.GetProperty("nodes")[0].GetProperty("properties").GetProperty("namespace").GetString());
    }

    [Fact]
    public void Serialization_is_independent_of_input_order()
    {
        var project = new ProjectIdentity("src/App/App.csproj", "net10.0");
        var first = GraphNode.ForSymbol(new SymbolIdentity(project, "App", [new ContainingTypeIdentity("A")], SymbolKind.Type, "A"));
        var second = GraphNode.ForSymbol(new SymbolIdentity(project, "App", [new ContainingTypeIdentity("B")], SymbolKind.Type, "B"));
        var edge = new GraphEdge(second.Id, first.Id, GraphRelation.References, EvidenceKind.Extracted, 1.0);
        var serializer = new GraphifyJsonSerializer();

        var forward = serializer.Serialize(GraphSnapshot.Create([first, second], [edge]));
        var reversed = serializer.Serialize(GraphSnapshot.Create([second, first], [edge]));

        Assert.Equal(forward, reversed);
    }
}
