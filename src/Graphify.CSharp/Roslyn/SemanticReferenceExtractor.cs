using System.Collections.Immutable;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticReferenceExtractor
{
    private readonly ExtractionParallelismOptions _parallelism;
    private readonly object _diagnosticsGate = new();
    private ImmutableArray<string> _diagnostics = ImmutableArray<string>.Empty;

    public SemanticReferenceExtractor()
        : this(ExtractionParallelismOptions.Default)
    {
    }

    internal SemanticReferenceExtractor(ExtractionParallelismOptions parallelism)
    {
        _parallelism = parallelism ?? throw new ArgumentNullException(nameof(parallelism));
    }

    public ImmutableArray<string> Diagnostics
    {
        get
        {
            lock (_diagnosticsGate)
            {
                return _diagnostics;
            }
        }
    }

    public async Task<GraphSnapshot> ExtractAsync(
        LoadedSolution solution,
        DeclarationCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(catalog);

        var contributions = await ExtractContributionsAsync(solution, catalog, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return GraphSnapshot.Create(
            contributions.SelectMany(contribution => contribution.Graph.Nodes),
            contributions.SelectMany(contribution => contribution.Graph.Edges));
    }

    internal async Task<IReadOnlyList<ExtractedProjectContribution>> ExtractContributionsAsync(
        LoadedSolution solution,
        DeclarationCatalog catalog,
        IReadOnlySet<string>? projectKeys = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(catalog);

        var locations = new SourceLocationFactory(solution.RepositoryRoot);
        var projectByNodeId = catalog.Declarations.ToDictionary(
            declaration => declaration.Node.Id,
            declaration => declaration.Identity.Project.Key,
            StringComparer.Ordinal);
        var relationshipEdgesByProject = new SemanticDeclarationRelationshipExtractor()
            .Extract(solution, catalog, cancellationToken, projectKeys)
            .Where(edge => projectByNodeId.ContainsKey(edge.SourceId))
            .GroupBy(edge => projectByNodeId[edge.SourceId], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var projects = solution.Projects
            .OrderBy(project => project.Identity.Key, StringComparer.Ordinal)
            .Where(project => projectKeys is null || projectKeys.Contains(project.Identity.Key))
            .ToArray();
        var work = projects
            .SelectMany(project => CreateProjectBatchWork(project))
            .Select((batch, index) => batch with { Index = index })
            .ToArray();
        var batchResults = await ExtractBatchesAsync(
                solution,
                catalog,
                locations,
                work,
                cancellationToken)
            .ConfigureAwait(false);
        var resultsByProject = new Dictionary<string, List<BatchExtractionResult>>(StringComparer.Ordinal);
        for (var index = 0; index < work.Length; index++)
        {
            var projectKey = work[index].Project.Identity.Key;
            if (!resultsByProject.TryGetValue(projectKey, out var projectResults))
            {
                projectResults = [];
                resultsByProject.Add(projectKey, projectResults);
            }

            projectResults.Add(batchResults[index]);
        }

        var contributions = new List<ExtractedProjectContribution>(projects.Length);
        foreach (var project in projects)
        {
            var edges = new GraphEdgeAccumulator();
            var projectResults = resultsByProject.GetValueOrDefault(project.Identity.Key) ?? [];
            foreach (var result in projectResults.OrderBy(result => result.Ordinal))
            {
                foreach (var edge in result.Edges)
                {
                    edges.Add(edge);
                }
            }

            if (relationshipEdgesByProject.TryGetValue(project.Identity.Key, out var relationshipEdges))
            {
                foreach (var edge in relationshipEdges)
                {
                    edges.Add(edge);
                }
            }

            var projectDiagnostics = projectResults
                .SelectMany(result => result.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
                .ToArray();
            var nodes = catalog.Declarations
                .Where(declaration => string.Equals(declaration.Identity.Project.Key, project.Identity.Key, StringComparison.Ordinal))
                .Select(declaration => declaration.Node);
            contributions.Add(new ExtractedProjectContribution(
                project.Identity,
                GraphSnapshot.Create(nodes, edges.ToImmutableArray()),
                projectDiagnostics));
        }

        var diagnostics = contributions
            .SelectMany(contribution => contribution.Diagnostics)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
        lock (_diagnosticsGate)
        {
            _diagnostics = diagnostics;
        }

        return contributions;
    }

    private async Task<ImmutableArray<BatchExtractionResult>> ExtractBatchesAsync(
        LoadedSolution solution,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        IReadOnlyList<BatchWork> work,
        CancellationToken cancellationToken)
    {
        var results = new BatchExtractionResult[work.Count];
        if (work.Count == 0)
        {
            return ImmutableArray<BatchExtractionResult>.Empty;
        }

        if (work.Count == 1)
        {
            results[0] = await ExtractBatchAsync(solution, catalog, locations, work[0], cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await Parallel.ForEachAsync(
                    work,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = _parallelism.MaxDegreeOfParallelism,
                    },
                    async (batch, token) =>
                    {
                        results[batch.Index] = await ExtractBatchAsync(
                                solution,
                                catalog,
                                locations,
                                batch,
                                token)
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }

        return results.ToImmutableArray();
    }

    private BatchWork[] CreateProjectBatchWork(AnalyzedProject project)
    {
        var documents = RoslynDocumentKeyPolicy.Create(project.Project.Documents)
            .Select(document => new DocumentWork(
                document.Document,
                document.Key,
                EstimateCost(document.Document)))
            .ToArray();
        var documentByKey = documents.ToDictionary(document => document.Key, StringComparer.Ordinal);
        return ExtractionBatchPlanner
            .Create(
                project.Identity.Key,
                documents.Select(document => new ExtractionDocument(document.Key, document.EstimatedCost)),
                _parallelism.MaxDegreeOfParallelism,
                _parallelism.TargetBatchesPerWorker,
                _parallelism.MinimumDocumentsPerBatch)
            .Select(batch => new BatchWork(
                project,
                batch,
                batch.Documents.Select(document => documentByKey[document.Key].Document).ToImmutableArray(),
                Index: -1))
            .ToArray();
    }

    private static async Task<BatchExtractionResult> ExtractBatchAsync(
        LoadedSolution solution,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        BatchWork batch,
        CancellationToken cancellationToken)
    {
        var edges = new GraphEdgeAccumulator();
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in batch.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            var semanticModel = batch.Project.Compilation.GetSemanticModel(root.SyntaxTree);
            var operationWalker = new SemanticOperationWalker(catalog, semanticModel, locations, edges);
            try
            {
                new SemanticSyntaxWalker(semanticModel, operationWalker).Visit(root);
            }
            catch (Exception exception) when (IsRecoverableSemanticException(exception))
            {
                diagnostics.Add(SemanticDiagnostic(solution, batch.Project, document, exception));
            }
        }

        return new BatchExtractionResult(
            batch.Plan.Ordinal,
            edges.ToImmutableArray(),
            diagnostics.OrderBy(diagnostic => diagnostic, StringComparer.Ordinal).ToImmutableArray());
    }

    private static long EstimateCost(Microsoft.CodeAnalysis.Document document)
    {
        if (document.FilePath is string path)
        {
            try
            {
                return Math.Max(1, new FileInfo(path).Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return Math.Max(1, document.Name.Length);
    }

    private sealed record BatchWork(
        AnalyzedProject Project,
        ExtractionBatch Plan,
        ImmutableArray<Microsoft.CodeAnalysis.Document> Documents,
        int Index);

    private sealed record DocumentWork(
        Microsoft.CodeAnalysis.Document Document,
        string Key,
        long EstimatedCost);

    private sealed record BatchExtractionResult(
        int Ordinal,
        ImmutableArray<GraphEdge> Edges,
        ImmutableArray<string> Diagnostics);

    private static bool IsRecoverableSemanticException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or NotImplementedException;

    private static string SemanticDiagnostic(
        LoadedSolution solution,
        AnalyzedProject project,
        Document document,
        Exception exception)
    {
        var path = document.FilePath is null
            ? document.Name
            : Path.GetRelativePath(solution.RepositoryRoot, Path.GetFullPath(document.FilePath))
                .Replace('\\', '/');
        return $"Semantic: skipped unsupported Roslyn operations in '{path}' for project '{project.Identity.RelativePath}': {exception.Message}";
    }
}
