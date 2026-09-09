using System.Collections.Immutable;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticReferenceExtractor
{
    public ImmutableArray<string> Diagnostics { get; private set; } = ImmutableArray<string>.Empty;

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

        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
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
        var contributions = new List<ExtractedProjectContribution>();
        foreach (var project in solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
        {
            var edges = new GraphEdgeAccumulator();
            foreach (var document in project.Project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (projectKeys is not null && !projectKeys.Contains(project.Identity.Key))
                {
                    break;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                var semanticModel = project.Compilation.GetSemanticModel(root.SyntaxTree);
                var operationWalker = new SemanticOperationWalker(catalog, semanticModel, locations, edges);
                try
                {
                    new SemanticSyntaxWalker(semanticModel, operationWalker).Visit(root);
                }
                catch (Exception exception) when (IsRecoverableSemanticException(exception))
                {
                    diagnostics.Add(SemanticDiagnostic(solution, project, document, exception));
                }
            }

            if (projectKeys is not null && !projectKeys.Contains(project.Identity.Key))
            {
                continue;
            }

            if (relationshipEdgesByProject.TryGetValue(project.Identity.Key, out var relationshipEdges))
            {
                foreach (var edge in relationshipEdges)
                {
                    edges.Add(edge);
                }
            }

            var projectDiagnostics = diagnostics
                .Where(diagnostic => diagnostic.Contains($"project '{project.Identity.RelativePath}'", StringComparison.Ordinal))
                .ToArray();
            var nodes = catalog.Declarations
                .Where(declaration => string.Equals(declaration.Identity.Project.Key, project.Identity.Key, StringComparison.Ordinal))
                .Select(declaration => declaration.Node);
            contributions.Add(new ExtractedProjectContribution(
                project.Identity,
                GraphSnapshot.Create(nodes, edges),
                projectDiagnostics));
        }

        Diagnostics = diagnostics
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
        return contributions;
    }

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
