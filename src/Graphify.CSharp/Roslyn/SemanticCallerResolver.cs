using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticCallerResolver
{
    private readonly DeclarationCatalog _catalog;
    private SemanticModel? _cachedSemanticModel;
    private SyntaxNode? _cachedRoot;
    private readonly Dictionary<int, SymbolDeclaration?> _callerByPosition = new();

    public SemanticCallerResolver(DeclarationCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public SymbolDeclaration? Resolve(SemanticModel semanticModel, int position)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);

        if (!ReferenceEquals(_cachedSemanticModel, semanticModel))
        {
            _cachedSemanticModel = semanticModel;
            _cachedRoot = null;
            _callerByPosition.Clear();
        }

        if (_callerByPosition.TryGetValue(position, out var cached))
        {
            return cached;
        }

        var resolved = ResolveUncached(semanticModel, position);
        _callerByPosition[position] = resolved;
        return resolved;
    }

    private SymbolDeclaration? ResolveUncached(SemanticModel semanticModel, int position)
    {

#if NET11_0_OR_GREATER
        if (IsInsideUnionDeclaration(semanticModel, position))
        {
            return ResolveSyntaxDeclaration(semanticModel, position)
                ?? ResolveSymbolChain(semanticModel.GetEnclosingSymbol(position));
        }
#endif

        var enclosingSymbol = semanticModel.GetEnclosingSymbol(position);
        if (!IsInsideExtensionBlock(enclosingSymbol))
        {
            return ResolveSymbolChain(enclosingSymbol);
        }

        var syntaxDeclaration = ResolveSyntaxDeclaration(semanticModel, position);
        return syntaxDeclaration ?? ResolveSymbolChain(enclosingSymbol);
    }

#if NET11_0_OR_GREATER
    private bool IsInsideUnionDeclaration(SemanticModel semanticModel, int position)
    {
        var root = GetRoot(semanticModel);
        var tokenPosition = Math.Clamp(position, root.FullSpan.Start, Math.Max(root.FullSpan.Start, root.FullSpan.End - 1));
        return root.FindToken(tokenPosition).Parent?.AncestorsAndSelf()
            .OfType<UnionDeclarationSyntax>()
            .Any() == true;
    }
#endif

    private SymbolDeclaration? ResolveSymbolChain(ISymbol? symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (_catalog.TryGet(current, out var declaration))
            {
                return declaration;
            }
        }

        return null;
    }

    private static bool IsInsideExtensionBlock(ISymbol? symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamedTypeSymbol { TypeKind: TypeKind.Extension })
            {
                return true;
            }
        }

        return false;
    }

    private SymbolDeclaration? ResolveSyntaxDeclaration(SemanticModel semanticModel, int position)
    {
        var root = GetRoot(semanticModel);
        var tokenPosition = Math.Clamp(position, root.FullSpan.Start, Math.Max(root.FullSpan.Start, root.FullSpan.End - 1));
        for (var node = root.FindToken(tokenPosition).Parent; node is not null; node = node.Parent)
        {
            if (node is not MemberDeclarationSyntax
                and not LocalFunctionStatementSyntax
                and not AccessorDeclarationSyntax)
            {
                continue;
            }

            var symbol = semanticModel.GetDeclaredSymbol(node);
            if (symbol is not null && _catalog.TryGet(symbol, out var declaration))
            {
                return declaration;
            }
        }

        return null;
    }

    private SyntaxNode GetRoot(SemanticModel semanticModel) =>
        _cachedRoot ??= semanticModel.SyntaxTree.GetRoot();
}
