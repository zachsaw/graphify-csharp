namespace Graphify.CSharp.Roslyn;

internal sealed class ExtractionParallelismOptions
{
    public ExtractionParallelismOptions(
        int maxDegreeOfParallelism,
        int targetBatchesPerWorker = ExtractionBatchPlanner.DefaultTargetBatchesPerWorker,
        int minimumDocumentsPerBatch = ExtractionBatchPlanner.DefaultMinimumDocumentsPerBatch)
    {
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

        MaxDegreeOfParallelism = maxDegreeOfParallelism;
        TargetBatchesPerWorker = targetBatchesPerWorker;
        MinimumDocumentsPerBatch = minimumDocumentsPerBatch;
    }

    public int MaxDegreeOfParallelism { get; }

    public int TargetBatchesPerWorker { get; }

    public int MinimumDocumentsPerBatch { get; }

    public static ExtractionParallelismOptions Default { get; } = new(
        Math.Max(1, Math.Min(Environment.ProcessorCount, 8)));
}
