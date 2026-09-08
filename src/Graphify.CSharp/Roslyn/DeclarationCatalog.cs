using System.Collections.Immutable;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class DeclarationCatalog
{
    private readonly ImmutableDictionary<string, SymbolDeclaration> _byKey;
    private readonly ImmutableDictionary<string, SymbolDeclaration> _byNodeId;
    private readonly Dictionary<ISymbol, SymbolDeclaration> _bySymbol;

    public DeclarationCatalog(IEnumerable<SymbolDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        var supplied = declarations.ToArray();
        var collision = supplied
            .GroupBy(declaration => declaration.Identity.CanonicalKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
        {
            var details = string.Join(
                " | ",
                collision.Select(declaration =>
                    $"{declaration.Symbol.ToDisplayString()} [{string.Join(", ", declaration.Symbol.Locations.Where(location => location.IsInSource).Select(location => location.SourceTree?.FilePath ?? "<unknown>"))}]"));
            throw new InvalidOperationException($"Canonical symbol identity collision for '{collision.Key}': {details}");
        }

        var ordered = supplied
            .OrderBy(declaration => declaration.Identity.CanonicalKey, StringComparer.Ordinal)
            .ToImmutableArray();
        Declarations = ordered;
        _byKey = ordered.ToImmutableDictionary(declaration => declaration.Identity.CanonicalKey, StringComparer.Ordinal);
        _byNodeId = ordered.ToImmutableDictionary(declaration => declaration.Node.Id, StringComparer.Ordinal);
        _bySymbol = new Dictionary<ISymbol, SymbolDeclaration>(SymbolEqualityComparer.Default);
        foreach (var declaration in ordered)
        {
            _bySymbol[declaration.Symbol] = declaration;
        }
    }

    public ImmutableArray<SymbolDeclaration> Declarations { get; }

    public bool TryGet(ISymbol symbol, out SymbolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return _bySymbol.TryGetValue(symbol, out declaration!);
    }

    public bool TryGet(string canonicalKey, out SymbolDeclaration declaration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        return _byKey.TryGetValue(canonicalKey, out declaration!);
    }

    public bool TryGetByNodeId(string nodeId, out SymbolDeclaration declaration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return _byNodeId.TryGetValue(nodeId, out declaration!);
    }
}
