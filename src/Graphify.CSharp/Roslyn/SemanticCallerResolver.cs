using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticCallerResolver
{
    private readonly DeclarationCatalog _catalog;

    public SemanticCallerResolver(DeclarationCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public SymbolDeclaration? Resolve(SemanticModel semanticModel, int position)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);

        var enclosingSymbol = semanticModel.GetEnclosingSymbol(position);
        if (!IsInsideExtensionBlock(enclosingSymbol))
        {
            return ResolveSymbolChain(enclosingSymbol);
        }

        var syntaxDeclaration = ResolveSyntaxDeclaration(semanticModel, position);
        return syntaxDeclaration ?? ResolveSymbolChain(enclosingSymbol);
    }

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
        var root = semanticModel.SyntaxTree.GetRoot();
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
}
