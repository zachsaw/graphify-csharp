using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class DeclarationCatalogBuilder
{
    private readonly Func<ISymbol, ProjectIdentity, string?, SymbolIdentity> _createIdentity;

    public DeclarationCatalogBuilder(RoslynSymbolIdentityFactory? identityFactory = null)
    {
        var factory = identityFactory ?? new RoslynSymbolIdentityFactory();
        _createIdentity = factory.Create;
    }

    internal static DeclarationCatalogBuilder ForTesting(
        Func<ISymbol, ProjectIdentity, string?, SymbolIdentity> createIdentity) =>
        new(createIdentity);

    private DeclarationCatalogBuilder(Func<ISymbol, ProjectIdentity, string?, SymbolIdentity> createIdentity)
    {
        _createIdentity = createIdentity ?? throw new ArgumentNullException(nameof(createIdentity));
    }

    public async Task<DeclarationCatalog> BuildAsync(LoadedSolution solution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var declarations = new List<SymbolDeclaration>();
        var diagnostics = new List<string>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
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
            SymbolIdentity? entryPointIdentity = null;
            if (entryPoint is not null)
            {
                try
                {
                    entryPointIdentity = _createIdentity(entryPoint, project.Identity, solution.RepositoryRoot);
                }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(IdentityDiagnostic(entryPoint, exception, locations));
                }
            }

            if (entryPoint is { IsImplicitlyDeclared: true }
                && IsSourceDeclaration(entryPoint, sourcePaths, allowImplicit: true))
            {
                Add(
                    entryPoint,
                    project.Identity,
                    solution.RepositoryRoot,
                    locations,
                    entryPointIdentity,
                    declarations,
                    seenSymbols,
                    seenIdentities,
                    diagnostics);
            }

            VisitNamespace(
                project.Compilation.GlobalNamespace,
                project.Identity,
                solution.RepositoryRoot,
                locations,
                sourcePaths,
                entryPointIdentity,
                declarations,
                seenSymbols,
                seenIdentities,
                diagnostics,
                cancellationToken);

            await AddSyntaxDeclarationsAsync(
                project,
                solution.RepositoryRoot,
                locations,
                sourcePaths,
                entryPoint,
                entryPointIdentity,
                declarations,
                seenSymbols,
                seenIdentities,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
        }

        return new DeclarationCatalog(declarations, diagnostics);
    }

    private void VisitNamespace(
        INamespaceSymbol @namespace,
        ProjectIdentity project,
        string repositoryRoot,
        SourceLocationFactory locations,
        IReadOnlySet<string> sourcePaths,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        ISet<ISymbol> seenSymbols,
        ISet<string> seenIdentities,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!@namespace.IsGlobalNamespace && IsSourceDeclaration(@namespace, sourcePaths))
        {
            Add(@namespace, project, repositoryRoot, locations, entryPoint, declarations, seenSymbols, seenIdentities, diagnostics);
        }

        foreach (var member in @namespace.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    VisitNamespace(childNamespace, project, repositoryRoot, locations, sourcePaths, entryPoint, declarations, seenSymbols, seenIdentities, diagnostics, cancellationToken);
                    break;
                case INamedTypeSymbol type:
                    VisitType(type, project, repositoryRoot, locations, sourcePaths, entryPoint, declarations, seenSymbols, seenIdentities, diagnostics, cancellationToken);
                    break;
            }
        }
    }

    private void VisitType(
        INamedTypeSymbol type,
        ProjectIdentity project,
        string repositoryRoot,
        SourceLocationFactory locations,
        IReadOnlySet<string> sourcePaths,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        ISet<ISymbol> seenSymbols,
        ISet<string> seenIdentities,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!IsSourceDeclaration(type, sourcePaths))
        {
            return;
        }

        Add(type, project, repositoryRoot, locations, entryPoint, declarations, seenSymbols, seenIdentities, diagnostics);
        foreach (var member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamedTypeSymbol nestedType)
            {
                VisitType(nestedType, project, repositoryRoot, locations, sourcePaths, entryPoint, declarations, seenSymbols, seenIdentities, diagnostics, cancellationToken);
            }
            else if (IsSourceDeclaration(member, sourcePaths) && IsSupportedMember(member))
            {
                Add(member, project, repositoryRoot, locations, entryPoint, declarations, seenSymbols, seenIdentities, diagnostics);
            }
        }
    }

    private async Task AddSyntaxDeclarationsAsync(
        AnalyzedProject project,
        string repositoryRoot,
        SourceLocationFactory locations,
        IReadOnlySet<string> sourcePaths,
        IMethodSymbol? entryPointSymbol,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        ISet<ISymbol> seenSymbols,
        ISet<string> seenIdentities,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var syntaxDeclarations = await new SourceDeclarationCollector()
            .CollectAsync(project, cancellationToken)
            .ConfigureAwait(false);
        foreach (var symbol in syntaxDeclarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isEntryPoint = entryPoint is not null
                && entryPointSymbol is not null
                && SymbolEqualityComparer.Default.Equals(symbol, entryPointSymbol)
                && IsSourceDeclaration(symbol, sourcePaths, allowImplicit: true);
            if (isEntryPoint || IsSourceDeclaration(symbol, sourcePaths))
            {
                Add(symbol, project.Identity, repositoryRoot, locations, entryPoint, declarations, seenSymbols, seenIdentities, diagnostics);
            }
        }
    }

    private static bool IsSupportedMember(ISymbol symbol) => symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;

    private void Add(
        ISymbol symbol,
        ProjectIdentity project,
        string repositoryRoot,
        SourceLocationFactory locations,
        SymbolIdentity? entryPoint,
        ICollection<SymbolDeclaration> declarations,
        ISet<ISymbol> seenSymbols,
        ISet<string> seenIdentities,
        ICollection<string> diagnostics)
    {
        var declarationSymbol = PartialSymbolHelper.Canonical(symbol);
        if (!seenSymbols.Add(declarationSymbol))
        {
            return;
        }

        SymbolIdentity identity;
        try
        {
            identity = _createIdentity(declarationSymbol, project, repositoryRoot);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(IdentityDiagnostic(declarationSymbol, exception, locations));
            return;
        }
        if (!seenIdentities.Add(identity.CanonicalKey))
        {
            diagnostics.Add($"Identity: skipped duplicate {declarationSymbol.Kind} '{declarationSymbol.ToDisplayString()}' at {LocationText(declarationSymbol, locations)}.");
            return;
        }
        var properties = new List<KeyValuePair<string, string>>
        {
            new("declaration_kind", DeclarationKind(declarationSymbol)),
        };
        if (entryPoint is not null && identity.Equals(entryPoint))
        {
            properties.Add(new KeyValuePair<string, string>("is_entry_point", "true"));
        }

        var node = GraphNode.ForSymbol(identity, locations.CreateMany(PartialSymbolHelper.Parts(declarationSymbol).SelectMany(part => part.Locations)), properties);
        var referenceKey = SymbolReferenceKey.TryCreate(declarationSymbol, out var projectIndependentKey)
            ? projectIndependentKey
            : null;
        declarations.Add(new SymbolDeclaration(declarationSymbol, identity, node, referenceKey));
    }


    private static string IdentityDiagnostic(
        ISymbol symbol,
        ArgumentException exception,
        SourceLocationFactory locations)
    {
        return $"Identity: skipped {symbol.Kind} '{symbol.ToDisplayString()}' at {LocationText(symbol, locations)}: {exception.Message}";
    }

    private static string LocationText(ISymbol symbol, SourceLocationFactory locations)
    {
        var location = locations.CreateMany(symbol.Locations).FirstOrDefault();
        return location is null
            ? "<unknown>"
            : $"{location.FilePath}:{location.Line}:{location.Column}";
    }

    private static string DeclarationKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol type when IsUnionType(type) => "union",
        INamedTypeSymbol type => type.IsRecord
            ? type.IsValueType ? "record_struct" : "record"
            : type.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol method => method.MethodKind.ToString().ToLowerInvariant(),
        IPropertySymbol property => property.IsIndexer ? "indexer" : "property",
        IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum => "enum_member",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        IParameterSymbol => "parameter",
        ILocalSymbol local when local.IsConst => "local_constant",
        ILocalSymbol => "local",
        IRangeVariableSymbol => "range_variable",
        ITypeParameterSymbol => "type_parameter",
        ILabelSymbol => "label",
        IAliasSymbol => "alias",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    private static bool IsUnionType(INamedTypeSymbol type)
    {
#if NET11_0_OR_GREATER
        return type.IsUnion;
#else
        return false;
#endif
    }

    private static bool IsSourceDeclaration(
        ISymbol symbol,
        IReadOnlySet<string> sourcePaths,
        bool allowImplicit = false) =>
        !string.IsNullOrWhiteSpace(symbol.Name)
        && !IsCompilerGeneratedImplementationSymbol(symbol)
        && (allowImplicit || !symbol.IsImplicitlyDeclared)
        && symbol.Locations.Any(location =>
            location.IsInSource
            && location.SourceTree?.FilePath is string filePath
            && sourcePaths.Contains(Path.GetFullPath(filePath)));

    private static bool IsCompilerGeneratedImplementationSymbol(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type when type.IsTupleType || type.IsAnonymousType => true,
        IFieldSymbol field when field.ContainingType?.IsTupleType == true => true,
        IPropertySymbol property when property.ContainingType?.IsAnonymousType == true => true,
        _ => false,
    };
}
