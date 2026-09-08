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
            var sourcePaths = project.Project.Documents
                .Select(document => document.FilePath)
                .OfType<string>()
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.Ordinal);
            var entryPoint = project.Compilation.GetEntryPoint(cancellationToken);
            var entryPointIdentity = entryPoint is null ? null : _identityFactory.Create(entryPoint, project.Identity);
            VisitNamespace(project.Compilation.GlobalNamespace, project.Identity, locations, sourcePaths, entryPointIdentity, declarations, cancellationToken);
        }

        return Task.FromResult(new DeclarationCatalog(declarations));
    }

    private void VisitNamespace(
        INamespaceSymbol @namespace,
        ProjectIdentity project,
        SourceLocationFactory locations,
        IReadOnlySet<string> sourcePaths,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        foreach (var member in @namespace.GetMembers().OrderBy(member => member.ToDisplayString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    VisitNamespace(childNamespace, project, locations, sourcePaths, entryPoint, declarations, cancellationToken);
                    break;
                case INamedTypeSymbol type:
                    VisitType(type, project, locations, sourcePaths, entryPoint, declarations, cancellationToken);
                    break;
            }
        }
    }

    private void VisitType(
        INamedTypeSymbol type,
        ProjectIdentity project,
        SourceLocationFactory locations,
        IReadOnlySet<string> sourcePaths,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        if (!IsSourceDeclaration(type, sourcePaths))
        {
            return;
        }

        Add(type, project, locations, entryPoint, declarations);
        foreach (var member in type.GetMembers().OrderBy(member => member.ToDisplayString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamedTypeSymbol nestedType)
            {
                VisitType(nestedType, project, locations, sourcePaths, entryPoint, declarations, cancellationToken);
            }
            else if (IsSourceDeclaration(member, sourcePaths) && IsSupportedMember(member))
            {
                Add(member, project, locations, entryPoint, declarations);
            }
        }
    }

    private void Add(
        ISymbol symbol,
        ProjectIdentity project,
        SourceLocationFactory locations,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations)
    {
        var identity = _identityFactory.Create(symbol, project);
        IEnumerable<KeyValuePair<string, string>>? properties = entryPoint is not null
            && identity.Equals(entryPoint)
            ? new[] { new KeyValuePair<string, string>("is_entry_point", "true") }
            : null;
        var node = GraphNode.ForSymbol(identity, locations.CreateMany(symbol.Locations), properties);
        declarations.Add(new SymbolDeclaration(symbol, identity, node));
    }

    private static bool IsSupportedMember(ISymbol symbol) => symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;

    private static bool IsSourceDeclaration(ISymbol symbol, IReadOnlySet<string> sourcePaths) =>
        !symbol.IsImplicitlyDeclared
        && symbol.Locations.Any(location =>
            location.IsInSource
            && location.SourceTree?.FilePath is string filePath
            && sourcePaths.Contains(Path.GetFullPath(filePath)));
}
