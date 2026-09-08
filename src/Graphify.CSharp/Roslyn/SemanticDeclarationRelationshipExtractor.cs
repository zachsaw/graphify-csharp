using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticDeclarationRelationshipExtractor
{
    public IReadOnlyList<GraphEdge> Extract(
        LoadedSolution solution,
        DeclarationCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(catalog);

        var locations = new SourceLocationFactory(solution.RepositoryRoot);
        var edges = new List<GraphEdge>();
        foreach (var declaration in catalog.Declarations.OrderBy(item => item.Identity.CanonicalKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (declaration.Symbol)
            {
                case INamedTypeSymbol type:
                    AddTypeRelationships(declaration, type, catalog, locations, edges);
                    break;
                case IMethodSymbol method:
                    AddMethodRelationships(declaration, method, catalog, locations, edges);
                    break;
                case IPropertySymbol property:
                    AddPropertyRelationships(declaration, property, catalog, locations, edges);
                    break;
                case IEventSymbol @event:
                    AddEventRelationships(declaration, @event, catalog, locations, edges);
                    break;
            }
        }

        return edges;
    }

    private static void AddTypeRelationships(
        SymbolDeclaration source,
        INamedTypeSymbol type,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        foreach (var interfaceType in type.AllInterfaces.OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
        {
            AddEdge(source, interfaceType, GraphRelation.Implements, catalog, locations, edges);
        }
    }

    private static void AddMethodRelationships(
        SymbolDeclaration source,
        IMethodSymbol method,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        AddEdge(source, method.OverriddenMethod, GraphRelation.Overrides, catalog, locations, edges);
        AddExplicitInterfaceRelationships(source, method.ExplicitInterfaceImplementations, catalog, locations, edges);
        AddImplicitInterfaceRelationships(source, method, catalog, locations, edges);
    }

    private static void AddPropertyRelationships(
        SymbolDeclaration source,
        IPropertySymbol property,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        AddEdge(source, property.OverriddenProperty, GraphRelation.Overrides, catalog, locations, edges);
        AddExplicitInterfaceRelationships(source, property.ExplicitInterfaceImplementations, catalog, locations, edges);
        AddImplicitInterfaceRelationships(source, property, catalog, locations, edges);
    }

    private static void AddEventRelationships(
        SymbolDeclaration source,
        IEventSymbol @event,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        AddEdge(source, @event.OverriddenEvent, GraphRelation.Overrides, catalog, locations, edges);
        AddExplicitInterfaceRelationships(source, @event.ExplicitInterfaceImplementations, catalog, locations, edges);
        AddImplicitInterfaceRelationships(source, @event, catalog, locations, edges);
    }

    private static void AddExplicitInterfaceRelationships(
        SymbolDeclaration source,
        IEnumerable<ISymbol> interfaceMembers,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        foreach (var interfaceMember in interfaceMembers.OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
        {
            AddEdge(source, interfaceMember, GraphRelation.Implements, catalog, locations, edges);
        }
    }

    private static void AddImplicitInterfaceRelationships(
        SymbolDeclaration source,
        ISymbol member,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        if (member.ContainingType is not INamedTypeSymbol containingType)
        {
            return;
        }

        foreach (var interfaceType in containingType.AllInterfaces.OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
        {
            foreach (var interfaceMember in interfaceType.GetMembers().OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
            {
                if (!SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(interfaceMember), member))
                {
                    continue;
                }

                AddEdge(source, interfaceMember, GraphRelation.Implements, catalog, locations, edges);
            }
        }
    }

    private static void AddEdge(
        SymbolDeclaration source,
        ISymbol? targetSymbol,
        GraphRelation relation,
        DeclarationCatalog catalog,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        if (targetSymbol is null)
        {
            return;
        }

        var target = FindDeclaration(targetSymbol, catalog);
        if (target is null)
        {
            return;
        }

        edges.Add(new GraphEdge(
            source.Node.Id,
            target.Node.Id,
            relation,
            EvidenceKind.Extracted,
            confidence: 1.0,
            locations.CreateMany(source.Symbol.Locations)));
    }

    private static SymbolDeclaration? FindDeclaration(ISymbol symbol, DeclarationCatalog catalog)
    {
        if (catalog.TryGet(symbol, out var declaration))
        {
            return declaration;
        }

        return catalog.TryGet(symbol.OriginalDefinition, out declaration) ? declaration : null;
    }
}
