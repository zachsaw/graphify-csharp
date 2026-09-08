using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class DeclarationCatalogBuilder
{
    private readonly RoslynSymbolIdentityFactory _identityFactory;

    public DeclarationCatalogBuilder(RoslynSymbolIdentityFactory? identityFactory = null)
    {
        _identityFactory = identityFactory ?? new RoslynSymbolIdentityFactory();
    }

    public Task<DeclarationCatalog> BuildAsync(LoadedSolution solution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var declarations = new List<SymbolDeclaration>();
        foreach (var project in solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locations = new SourceLocationFactory(solution.RepositoryRoot);
            VisitNamespace(project.Compilation.GlobalNamespace, project.Identity, locations, declarations, cancellationToken);
        }

        return Task.FromResult(new DeclarationCatalog(declarations));
    }

    private void VisitNamespace(
        INamespaceSymbol @namespace,
        ProjectIdentity project,
        SourceLocationFactory locations,
        ICollection<SymbolDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        foreach (var member in @namespace.GetMembers().OrderBy(member => member.ToDisplayString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    VisitNamespace(childNamespace, project, locations, declarations, cancellationToken);
                    break;
                case INamedTypeSymbol type:
                    VisitType(type, project, locations, declarations, cancellationToken);
                    break;
            }
        }
    }

    private void VisitType(
        INamedTypeSymbol type,
        ProjectIdentity project,
        SourceLocationFactory locations,
        ICollection<SymbolDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        if (!IsSourceDeclaration(type))
        {
            return;
        }

        Add(type, project, locations, declarations);
        foreach (var member in type.GetMembers().OrderBy(member => member.ToDisplayString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamedTypeSymbol nestedType)
            {
                VisitType(nestedType, project, locations, declarations, cancellationToken);
            }
            else if (IsSourceDeclaration(member) && IsSupportedMember(member))
            {
                Add(member, project, locations, declarations);
            }
        }
    }

    private void Add(
        ISymbol symbol,
        ProjectIdentity project,
        SourceLocationFactory locations,
        ICollection<SymbolDeclaration> declarations)
    {
        var identity = _identityFactory.Create(symbol, project);
        var node = GraphNode.ForSymbol(identity, locations.CreateMany(symbol.Locations));
        declarations.Add(new SymbolDeclaration(symbol, identity, node));
    }

    private static bool IsSupportedMember(ISymbol symbol) => symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;

    private static bool IsSourceDeclaration(ISymbol symbol) => !symbol.IsImplicitlyDeclared && symbol.Locations.Any(location => location.IsInSource);
}
