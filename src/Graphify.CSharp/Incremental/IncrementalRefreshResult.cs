using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalRefreshResult
{
    public IncrementalRefreshResult(
        GraphSnapshot graph,
        string? outputDigest,
        IncrementalCacheLoadStatus cacheStatus,
        int extractedProjectCount,
        int reusedProjectCount,
        bool outputRepublished,
        RefreshGeneration? generation = null,
        SemanticQueryResponse? semanticRefreshResponse = null)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        OutputDigest = outputDigest;
        CacheStatus = cacheStatus;
        ExtractedProjectCount = extractedProjectCount;
        ReusedProjectCount = reusedProjectCount;
        OutputRepublished = outputRepublished;
        Generation = generation;
        SemanticRefreshResponse = semanticRefreshResponse;
    }

    public GraphSnapshot Graph { get; }

    public string? OutputDigest { get; }

    public IncrementalCacheLoadStatus CacheStatus { get; }

    public int ExtractedProjectCount { get; }

    public int ReusedProjectCount { get; }

    public bool OutputRepublished { get; }

    public RefreshGeneration? Generation { get; }

    public SemanticQueryResponse? SemanticRefreshResponse { get; }

    internal IncrementalRefreshResult WithSemanticRefreshResponse(SemanticQueryResponse response) =>
        new(
            Graph,
            OutputDigest,
            CacheStatus,
            ExtractedProjectCount,
            ReusedProjectCount,
            OutputRepublished,
            Generation,
            response ?? throw new ArgumentNullException(nameof(response)));
}
