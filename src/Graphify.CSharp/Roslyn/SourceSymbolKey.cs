using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

internal static class SourceSymbolKey
{
    public static bool TryCreate(ISymbol symbol, out string key)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var location = symbol.Locations
            .Where(item => item.IsInSource && item.SourceTree is not null)
            .OrderBy(item => item.SourceTree!.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.SourceSpan.Start)
            .ThenBy(item => item.SourceSpan.Length)
            .FirstOrDefault();
        if (location is null || string.IsNullOrWhiteSpace(location.SourceTree!.FilePath))
        {
            key = string.Empty;
            return false;
        }

        key = string.Join(
            '\u001F',
            symbol.Kind,
            symbol.Name,
            location.SourceTree.FilePath.Replace('\\', '/'),
            location.SourceSpan.Start,
            location.SourceSpan.Length);
        return true;
    }
}
