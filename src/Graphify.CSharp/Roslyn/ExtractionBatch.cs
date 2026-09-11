using System.Collections.Immutable;

namespace Graphify.CSharp.Roslyn;

internal sealed class ExtractionDocument
{
    public ExtractionDocument(string key, long estimatedCost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (estimatedCost <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedCost), estimatedCost, "Estimated cost must be positive.");
        }

        Key = key;
        EstimatedCost = estimatedCost;
    }

    public string Key { get; }

    public long EstimatedCost { get; }
}

internal sealed class ExtractionBatch
{
    public ExtractionBatch(string projectKey, int ordinal, IEnumerable<ExtractionDocument> documents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Batch ordinal cannot be negative.");
        }

        ProjectKey = projectKey;
        Ordinal = ordinal;
        Documents = (documents ?? throw new ArgumentNullException(nameof(documents))).ToImmutableArray();
        if (Documents.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An extraction batch must contain at least one document.", nameof(documents));
        }

        EstimatedCost = Documents.Aggregate(0L, (total, document) => checked(total + document.EstimatedCost));
    }

    public string ProjectKey { get; }

    public int Ordinal { get; }

    public ImmutableArray<ExtractionDocument> Documents { get; }

    public long EstimatedCost { get; }
}

internal static class ExtractionBatchPlanner
{
    internal const int DefaultTargetBatchesPerWorker = 2;
    internal const int DefaultMinimumDocumentsPerBatch = 32;

    public static ImmutableArray<ExtractionBatch> Create(
        string projectKey,
        IEnumerable<ExtractionDocument> documents,
        int maxDegreeOfParallelism,
        int targetBatchesPerWorker = DefaultTargetBatchesPerWorker,
        int minimumDocumentsPerBatch = DefaultMinimumDocumentsPerBatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentNullException.ThrowIfNull(documents);
        if (maxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism, "Parallelism must be positive.");
        }

        if (targetBatchesPerWorker <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetBatchesPerWorker), targetBatchesPerWorker, "The target batch count must be positive.");
        }

        if (minimumDocumentsPerBatch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDocumentsPerBatch), minimumDocumentsPerBatch, "The minimum batch size must be positive.");
        }

        var ordered = documents
            .OrderBy(document => document.Key, StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1].Key, ordered[index].Key, StringComparison.Ordinal))
            {
                throw new ArgumentException($"The extraction input contains duplicate document '{ordered[index].Key}'.", nameof(documents));
            }
        }

        if (ordered.Length == 0)
        {
            return ImmutableArray<ExtractionBatch>.Empty;
        }

        var maxBatchCount = Math.Min(
            ordered.Length / minimumDocumentsPerBatch,
            MaxBatchCount(maxDegreeOfParallelism, targetBatchesPerWorker));
        var batchCount = Math.Max(1, maxBatchCount);
        if (maxDegreeOfParallelism == 1)
        {
            batchCount = 1;
        }

        if (batchCount == 1)
        {
            return [new ExtractionBatch(projectKey, 0, ordered)];
        }

        var batches = ImmutableArray.CreateBuilder<ExtractionBatch>(batchCount);
        var totalCost = ordered.Aggregate(0L, (total, document) => checked(total + document.EstimatedCost));
        var remainingCost = totalCost;
        var start = 0;
        for (var ordinal = 0; ordinal < batchCount; ordinal++)
        {
            var remainingBatches = batchCount - ordinal;
            var end = ordinal == batchCount - 1
                ? ordered.Length
                : ChooseEnd(ordered, start, remainingBatches, remainingCost);
            var batchDocuments = ordered[start..end];
            var batch = new ExtractionBatch(projectKey, ordinal, batchDocuments);
            batches.Add(batch);
            remainingCost -= batch.EstimatedCost;
            start = end;
        }

        return batches.MoveToImmutable();
    }

    private static int MaxBatchCount(int maxDegreeOfParallelism, int targetBatchesPerWorker)
    {
        var desired = (long)maxDegreeOfParallelism * targetBatchesPerWorker;
        return desired >= int.MaxValue ? int.MaxValue : (int)desired;
    }

    private static int ChooseEnd(
        IReadOnlyList<ExtractionDocument> ordered,
        int start,
        int remainingBatches,
        long remainingCost)
    {
        var targetCost = CeilDivide(remainingCost, remainingBatches);
        var maximumEnd = ordered.Count - (remainingBatches - 1);
        var end = start;
        var currentCost = 0L;
        while (end < maximumEnd)
        {
            currentCost = checked(currentCost + ordered[end].EstimatedCost);
            end++;
            if (currentCost >= targetCost)
            {
                break;
            }
        }

        return end;
    }

    private static long CeilDivide(long value, int divisor) =>
        value / divisor + (value % divisor == 0 ? 0 : 1);
}
