using System.Collections.Concurrent;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class SourceLocationFactory
{
    private readonly string _repositoryRoot;
    private readonly ConcurrentDictionary<string, string> _relativePathCache = new(StringComparer.Ordinal);

    public SourceLocationFactory(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    public SourceLocation? Create(Location location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsInSource)
        {
            return null;
        }

        var lineSpan = location.GetLineSpan();
        if (string.IsNullOrWhiteSpace(lineSpan.Path))
        {
            return null;
        }

        var relativePath = _relativePathCache.GetOrAdd(
            lineSpan.Path,
            static (path, repositoryRoot) => ProjectIdentity.FromPath(path, repositoryRoot).RelativePath,
            _repositoryRoot);
        return new SourceLocation(
            relativePath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }

    public IReadOnlyList<SourceLocation> CreateMany(IEnumerable<Location> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return locations
            .Select(Create)
            .OfType<SourceLocation>()
            .Distinct()
            .OrderBy(location => location.FilePath, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToArray();
    }
}
