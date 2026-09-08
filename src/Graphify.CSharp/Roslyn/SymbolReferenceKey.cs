using Microsoft.CodeAnalysis;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Roslyn;

internal static class SymbolReferenceKey
{
    private static readonly RoslynSymbolIdentityFactory IdentityFactory = new();
    private static readonly ProjectIdentity ReferenceProject = new("__reference__.csproj", "__reference__");

    public static bool TryCreate(ISymbol symbol, out string referenceKey)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (string.IsNullOrWhiteSpace(symbol.Name)
            || symbol.IsImplicitlyDeclared
            || symbol is not INamespaceSymbol
            and not INamedTypeSymbol
            and not IMethodSymbol
            and not IPropertySymbol
            and not IFieldSymbol
            and not IEventSymbol)
        {
            referenceKey = string.Empty;
            return false;
        }

        try
        {
            referenceKey = IdentityFactory.Create(symbol, ReferenceProject).ReferenceKey;
            return true;
        }
        catch (ArgumentException)
        {
            referenceKey = string.Empty;
            return false;
        }
    }
}
