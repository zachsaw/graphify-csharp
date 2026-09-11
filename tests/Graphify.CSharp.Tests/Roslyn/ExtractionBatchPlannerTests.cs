using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class ExtractionBatchPlannerTests
{
    [Fact]
    public void Keeps_small_projects_in_one_coarse_batch()
    {
        var documents = Enumerable.Range(0, ExtractionBatchPlanner.DefaultMinimumDocumentsPerBatch)
            .Select(index => new ExtractionDocument($"file-{index:D3}.cs", estimatedCost: 1));

        var batches = ExtractionBatchPlanner.Create("project|net10.0", documents, maxDegreeOfParallelism: 8);

        var batch = Assert.Single(batches);
        Assert.Equal(0, batch.Ordinal);
        Assert.Equal(ExtractionBatchPlanner.DefaultMinimumDocumentsPerBatch, batch.Documents.Length);
    }

    [Fact]
    public void Limits_batch_count_to_a_small_multiple_of_workers()
    {
        var documents = Enumerable.Range(0, 1_000)
            .Select(index => new ExtractionDocument($"file-{index:D4}.cs", estimatedCost: 1));

        var batches = ExtractionBatchPlanner.Create("project|net10.0", documents, maxDegreeOfParallelism: 4);

        Assert.Equal(8, batches.Length);
        Assert.All(batches, batch => Assert.True(batch.Documents.Length >= 32));
    }

    [Fact]
    public void Uses_cost_to_create_stable_balanced_file_batches()
    {
        var documents = Enumerable.Range(0, 128)
            .Select(index => new ExtractionDocument($"file-{index:D4}.cs", index % 7 == 0 ? 100 : 1))
            .OrderByDescending(document => document.Key, StringComparer.Ordinal)
            .ToArray();

        var first = ExtractionBatchPlanner.Create("project|net10.0", documents, maxDegreeOfParallelism: 4);
        var second = ExtractionBatchPlanner.Create("project|net10.0", documents, maxDegreeOfParallelism: 4);

        Assert.Equal(first.Select(batch => batch.Documents.Select(document => document.Key)), second.Select(batch => batch.Documents.Select(document => document.Key)));
        Assert.Equal(first.Sum(batch => batch.Documents.Length), documents.Length);
        Assert.Equal(documents.Select(document => document.Key).OrderBy(key => key, StringComparer.Ordinal), first.SelectMany(batch => batch.Documents).Select(document => document.Key));
        Assert.True(first.Max(batch => batch.EstimatedCost) - first.Min(batch => batch.EstimatedCost) <= 200);
    }

    [Fact]
    public void Serial_mode_keeps_the_entire_project_in_one_batch()
    {
        var documents = Enumerable.Range(0, 1_000)
            .Select(index => new ExtractionDocument($"file-{index:D4}.cs", estimatedCost: index + 1));

        var batches = ExtractionBatchPlanner.Create("project|net10.0", documents, maxDegreeOfParallelism: 1);

        Assert.Single(batches);
        Assert.Equal(documents.Count(), batches[0].Documents.Length);
    }

    [Fact]
    public void Rejects_duplicate_document_keys()
    {
        var documents = new[]
        {
            new ExtractionDocument("same.cs", estimatedCost: 1),
            new ExtractionDocument("same.cs", estimatedCost: 2),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            ExtractionBatchPlanner.Create("project|net10.0", documents, maxDegreeOfParallelism: 2));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
