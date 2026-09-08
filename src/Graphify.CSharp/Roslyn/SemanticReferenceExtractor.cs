using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticReferenceExtractor
{
    public async Task<GraphSnapshot> ExtractAsync(
        LoadedSolution solution,
        DeclarationCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(catalog);

        var edges = new GraphEdgeAccumulator();
        var locations = new SourceLocationFactory(solution.RepositoryRoot);
        foreach (var edge in new SemanticDeclarationRelationshipExtractor().Extract(solution, catalog, cancellationToken))
        {
            edges.Add(edge);
        }
        foreach (var project in solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
        {
            foreach (var document in project.Project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null)
                {
                    continue;
                }

                var semanticModel = project.Compilation.GetSemanticModel(root.SyntaxTree);
                var operationWalker = new SemanticOperationWalker(catalog, semanticModel, locations, edges);
                new SemanticSyntaxWalker(semanticModel, operationWalker).Visit(root);
            }
        }

        return GraphSnapshot.Create(catalog.Declarations.Select(declaration => declaration.Node), edges);
    }
}
