using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class GraphEdgeAccumulatorTests
{
    [Fact]
    public void Accumulator_merges_duplicate_observations_without_materializing_each_observation()
    {
        var accumulator = new GraphEdgeAccumulator();
        var firstLocation = new SourceLocation("src/Caller.cs", 10, 5);
        var secondLocation = new SourceLocation("src/Caller.cs", 20, 5);

        accumulator.Add("caller", "callee", GraphRelation.Calls, EvidenceKind.Extracted, 0.7, firstLocation);
        accumulator.Add("caller", "callee", GraphRelation.Calls, EvidenceKind.Extracted, 1.0, firstLocation);
        accumulator.Add("caller", "callee", GraphRelation.Calls, EvidenceKind.Extracted, 0.8, secondLocation);

        Assert.Equal(1, accumulator.Count);
        var edge = Assert.Single(accumulator.ToImmutableArray());
        Assert.Equal(1.0, edge.Confidence);
        Assert.True(new[] { firstLocation, secondLocation }.SequenceEqual(edge.SourceLocations));
    }

    [Fact]
    public void Accumulator_keeps_edge_identity_and_confidence_when_observation_has_no_location()
    {
        var accumulator = new GraphEdgeAccumulator();
        var observed = new GraphEdge(
            "caller",
            "callee",
            GraphRelation.References,
            EvidenceKind.Inferred,
            0.9);

        accumulator.Add(observed);
        accumulator.Add("caller", "callee", GraphRelation.References, EvidenceKind.Inferred, 1.0, null);

        var edge = Assert.Single(accumulator.ToImmutableArray());
        Assert.Equal(observed.SourceId, edge.SourceId);
        Assert.Equal(observed.TargetId, edge.TargetId);
        Assert.Equal(observed.Relation, edge.Relation);
        Assert.Equal(observed.Evidence, edge.Evidence);
        Assert.Equal(1.0, edge.Confidence);
        Assert.Empty(edge.SourceLocations);
    }
}
