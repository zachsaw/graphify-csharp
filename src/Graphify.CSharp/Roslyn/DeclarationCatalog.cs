using System.Collections.Concurrent;
using System.Collections.Immutable;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class DeclarationCatalog
{
    private readonly ImmutableDictionary<string, ImmutableArray<SymbolDeclaration>> _byReferenceKey;
    private readonly ImmutableDictionary<string, ImmutableArray<SymbolDeclaration>> _bySourceLocation;
    private readonly Dictionary<ISymbol, SymbolDeclaration> _bySymbol;
    private readonly ConcurrentDictionary<ISymbol, ReferenceLookupResult> _referenceLookupCache =
        new(SymbolEqualityComparer.Default);

    public DeclarationCatalog(
        IEnumerable<SymbolDeclaration> declarations,
        IEnumerable<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        Diagnostics = (diagnostics ?? Array.Empty<string>())
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(diagnostic => diagnostic.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();

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

    public ImmutableArray<string> Diagnostics { get; }

    public bool TryGet(ISymbol symbol, out SymbolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var canonical = PartialSymbolHelper.Canonical(symbol);
        if (_bySymbol.TryGetValue(canonical, out declaration!))
        {
            return true;
        }

        return TryGetBySourceLocation(symbol, out declaration);
    }

    public bool TryGetReference(ISymbol symbol, out SymbolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (_referenceLookupCache.TryGetValue(symbol, out var cached))
        {
            declaration = cached.Declaration!;
            return cached.Declaration is not null;
        }

        SymbolDeclaration? resolved = null;
        try
        {
            if (!symbol.Locations.Any(location => location.IsInSource)
                || !SymbolReferenceKey.TryCreate(symbol, out var referenceKey))
            {
                _referenceLookupCache.TryAdd(symbol, new ReferenceLookupResult(null));
                declaration = null!;
                return false;
            }

            if (_byReferenceKey.TryGetValue(referenceKey, out var matches) && matches.Length == 1)
            {
                resolved = matches[0];
            }
        }
        catch (NotSupportedException)
        {
            // Some Roslyn implementation symbols intentionally do not expose
            // locations or containing symbols. They cannot be reference keys.
        }

        _referenceLookupCache.TryAdd(symbol, new ReferenceLookupResult(resolved));
        declaration = resolved!;
        return resolved is not null;
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

    private readonly record struct ReferenceLookupResult(SymbolDeclaration? Declaration);

}
