using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalRefreshResult
{
    public IncrementalRefreshResult(
        GraphSnapshot graph,
        string outputDigest,
        IncrementalCacheLoadStatus cacheStatus,
        int extractedProjectCount,
        int reusedProjectCount,
        bool outputRepublished)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDigest);
        OutputDigest = outputDigest;
        CacheStatus = cacheStatus;
        ExtractedProjectCount = extractedProjectCount;
        ReusedProjectCount = reusedProjectCount;
        OutputRepublished = outputRepublished;
    }

    public GraphSnapshot Graph { get; }

    public string OutputDigest { get; }

    public IncrementalCacheLoadStatus CacheStatus { get; }

    public int ExtractedProjectCount { get; }

    public int ReusedProjectCount { get; }

    public bool OutputRepublished { get; }
}
