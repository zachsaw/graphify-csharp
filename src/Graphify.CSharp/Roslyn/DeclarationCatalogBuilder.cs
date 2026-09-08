using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Roslyn;

public sealed class DeclarationCatalogBuilder
{
    private readonly RoslynSymbolIdentityFactory _identityFactory;

    public DeclarationCatalogBuilder(RoslynSymbolIdentityFactory? identityFactory = null)
    {
        _identityFactory = identityFactory ?? new RoslynSymbolIdentityFactory();
    }

    public async Task<DeclarationCatalog> BuildAsync(LoadedSolution solution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var declarations = new List<SymbolDeclaration>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var locations = new SourceLocationFactory(solution.RepositoryRoot);
        foreach (var project in solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePaths = project.Project.Documents
                .Select(document => document.FilePath)
                .OfType<string>()
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.Ordinal);
            var entryPoint = project.Compilation.GetEntryPoint(cancellationToken);
            var entryPointIdentity = entryPoint is null ? null : _identityFactory.Create(entryPoint, project.Identity);

            VisitNamespace(
                project.Compilation.GlobalNamespace,
                project.Identity,
                locations,
                sourcePaths,
                entryPointIdentity,
                declarations,
                seenSymbols,
                cancellationToken);

            await AddLocalFunctionsAsync(
                project,
                locations,
                sourcePaths,
                entryPointIdentity,
                declarations,
                seenSymbols,
                cancellationToken).ConfigureAwait(false);
        }

        return new DeclarationCatalog(declarations);
    }

    private void VisitNamespace(
        INamespaceSymbol @namespace,
        ProjectIdentity project,
        SourceLocationFactory locations,
        IReadOnlySet<string> sourcePaths,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        ISet<ISymbol> seenSymbols,
        CancellationToken cancellationToken)
    {
        if (!@namespace.IsGlobalNamespace && IsSourceDeclaration(@namespace, sourcePaths))
        {
            Add(@namespace, project, locations, entryPoint, declarations, seenSymbols);
        }

        foreach (var member in @namespace.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    VisitNamespace(childNamespace, project, locations, sourcePaths, entryPoint, declarations, seenSymbols, cancellationToken);
                    break;
                case INamedTypeSymbol type:
                    VisitType(type, project, locations, sourcePaths, entryPoint, declarations, seenSymbols, cancellationToken);
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
        ISet<ISymbol> seenSymbols,
        CancellationToken cancellationToken)
    {
        if (!IsSourceDeclaration(type, sourcePaths))
        {
            return;
        }

        Add(type, project, locations, entryPoint, declarations, seenSymbols);
        foreach (var member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamedTypeSymbol nestedType)
            {
                VisitType(nestedType, project, locations, sourcePaths, entryPoint, declarations, seenSymbols, cancellationToken);
            }
            else if (IsSourceDeclaration(member, sourcePaths) && IsSupportedMember(member))
            {
                Add(member, project, locations, entryPoint, declarations, seenSymbols);
            }
        }
    }

    private async Task AddLocalFunctionsAsync(
        AnalyzedProject project,
        SourceLocationFactory locations,
        IReadOnlySet<string> sourcePaths,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        ISet<ISymbol> seenSymbols,
        CancellationToken cancellationToken)
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
            foreach (var localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var symbol = semanticModel.GetDeclaredSymbol(localFunction, cancellationToken);
                if (symbol is not null && IsSourceDeclaration(symbol, sourcePaths))
                {
                    Add(symbol, project.Identity, locations, entryPoint, declarations, seenSymbols);
                }
            }
        }
    }

    private static bool IsSupportedMember(ISymbol symbol) => symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;

    private void Add(
        ISymbol symbol,
        ProjectIdentity project,
        SourceLocationFactory locations,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        ISet<ISymbol> seenSymbols)
    {
        if (!seenSymbols.Add(symbol))
        {
            return;
        }

        var identity = _identityFactory.Create(symbol, project);
        var properties = new List<KeyValuePair<string, string>>
        {
            new("declaration_kind", DeclarationKind(symbol)),
        };
        if (entryPoint is not null && identity.Equals(entryPoint))
        {
            properties.Add(new KeyValuePair<string, string>("is_entry_point", "true"));
        }

        var node = GraphNode.ForSymbol(identity, locations.CreateMany(symbol.Locations), properties);
        declarations.Add(new SymbolDeclaration(symbol, identity, node, SymbolReferenceKey.Create(symbol)));
    }

    private static string DeclarationKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol method => method.MethodKind.ToString().ToLowerInvariant(),
        IPropertySymbol property => property.IsIndexer ? "indexer" : "property",
        IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum => "enum_member",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    private static bool IsSourceDeclaration(ISymbol symbol, IReadOnlySet<string> sourcePaths) =>
        !symbol.IsImplicitlyDeclared
        && symbol.Locations.Any(location =>
            location.IsInSource
            && location.SourceTree?.FilePath is string filePath
            && sourcePaths.Contains(Path.GetFullPath(filePath)));
}
