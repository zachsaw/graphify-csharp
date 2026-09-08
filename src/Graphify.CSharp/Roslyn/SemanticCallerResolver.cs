using Microsoft.CodeAnalysis;

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

        for (var symbol = semanticModel.GetEnclosingSymbol(position); symbol is not null; symbol = symbol.ContainingSymbol)
        {
            if (_catalog.TryGet(symbol, out var declaration))
            {
                return declaration;
            }
        }

        return null;
    }
}
