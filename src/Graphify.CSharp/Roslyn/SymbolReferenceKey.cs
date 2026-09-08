using Microsoft.CodeAnalysis;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Roslyn;

internal static class SymbolReferenceKey
{
    private static readonly RoslynSymbolIdentityFactory IdentityFactory = new();
    private static readonly ProjectIdentity ReferenceProject = new("__reference__.csproj", "__reference__");

    public static string Create(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!TryCreate(symbol, out var referenceKey))
        {
            throw new ArgumentException($"Unsupported reference symbol '{symbol.Kind}'.", nameof(symbol));
        }

        return referenceKey;
    }

    public static bool TryCreate(ISymbol symbol, out string referenceKey)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol is not INamespaceSymbol
            and not INamedTypeSymbol
            and not IMethodSymbol
            and not IPropertySymbol
            and not IFieldSymbol
            and not IEventSymbol)
        {
            referenceKey = string.Empty;
            return false;
        }

        referenceKey = IdentityFactory.Create(symbol, ReferenceProject).ReferenceKey;
        return true;
    }
}
