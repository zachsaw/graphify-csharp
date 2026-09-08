using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class SymbolDeclaration
{
    public SymbolDeclaration(
        ISymbol symbol,
        SymbolIdentity identity,
        GraphNode node,
        string? referenceKey = null)
    {
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ReferenceKey = referenceKey;
    }

    public ISymbol Symbol { get; }

    public SymbolIdentity Identity { get; }

    public GraphNode Node { get; }

    public string? ReferenceKey { get; }
}
