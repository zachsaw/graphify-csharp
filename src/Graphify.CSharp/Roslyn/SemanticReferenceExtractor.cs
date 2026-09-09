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

        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
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
                try
                {
                    new SemanticSyntaxWalker(semanticModel, operationWalker).Visit(root);
                }
                catch (Exception exception) when (IsRecoverableSemanticException(exception))
                {
                    diagnostics.Add(SemanticDiagnostic(solution, project, document, exception));
                }
            }
        }

        Diagnostics = diagnostics
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
        return GraphSnapshot.Create(catalog.Declarations.Select(declaration => declaration.Node), edges);
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
