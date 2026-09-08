using System.Collections.Immutable;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class DeclarationCatalog
{
    private readonly ImmutableDictionary<string, ImmutableArray<SymbolDeclaration>> _byReferenceKey;
    private readonly ImmutableDictionary<string, ImmutableArray<SymbolDeclaration>> _bySourceLocation;
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
        _byReferenceKey = ordered
            .Where(declaration => !string.IsNullOrWhiteSpace(declaration.ReferenceKey))
            .GroupBy(declaration => declaration.ReferenceKey!, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
        var sourceLocationMatches = new Dictionary<string, List<SymbolDeclaration>>(StringComparer.Ordinal);
        foreach (var declaration in ordered)
        {
            if (!SourceSymbolKey.TryCreate(declaration.Symbol, out var sourceKey))
            {
                continue;
            }

            if (!sourceLocationMatches.TryGetValue(sourceKey, out var matches))
            {
                matches = [];
                sourceLocationMatches.Add(sourceKey, matches);
            }

            matches.Add(declaration);
        }

        _bySourceLocation = sourceLocationMatches.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableArray(),
            StringComparer.Ordinal);
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
        if (_bySymbol.TryGetValue(symbol, out declaration!))
        {
            return true;
        }

        return TryGetBySourceLocation(symbol, out declaration);
    }

    public bool TryGetReference(ISymbol symbol, out SymbolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (!symbol.Locations.Any(location => location.IsInSource)
            || !SymbolReferenceKey.TryCreate(symbol, out var referenceKey))
        {
            declaration = null!;
            return false;
        }

        if (_byReferenceKey.TryGetValue(referenceKey, out var matches) && matches.Length == 1)
        {
            declaration = matches[0];
            return true;
        }

        declaration = null!;
        return false;
    }

    private bool TryGetBySourceLocation(ISymbol symbol, out SymbolDeclaration declaration)
    {
        if (SourceSymbolKey.TryCreate(symbol, out var sourceKey)
            && _bySourceLocation.TryGetValue(sourceKey, out var matches)
            && matches.Length == 1)
        {
            declaration = matches[0];
            return true;
        }

        declaration = null!;
        return false;
    }

}
