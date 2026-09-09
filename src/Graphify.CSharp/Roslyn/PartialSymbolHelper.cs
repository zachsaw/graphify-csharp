using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

internal static class PartialSymbolHelper
{
    public static ISymbol Canonical(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.PartialDefinitionPart ?? method,
        IPropertySymbol property => property.PartialDefinitionPart ?? property,
        IEventSymbol @event => @event.PartialDefinitionPart ?? @event,
        _ => symbol,
    };

    public static IEnumerable<ISymbol> Parts(ISymbol symbol)
    {
        var canonical = Canonical(symbol);
        yield return canonical;

        switch (canonical)
        {
            case IMethodSymbol { PartialImplementationPart: { } implementation }:
                yield return implementation;
                break;
            case IPropertySymbol { PartialImplementationPart: { } implementation }:
                yield return implementation;
                break;
            case IEventSymbol { PartialImplementationPart: { } implementation }:
                yield return implementation;
                break;
        }
    }
}
