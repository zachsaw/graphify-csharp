using System.Collections.Concurrent;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

public sealed class SourceLocationFactory
{
    private readonly string _repositoryRoot;
    private readonly ConcurrentDictionary<(string Path, string? SourcePath), string> _relativePathCache = new();

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

        var lineSpan = location.GetMappedLineSpan();
        if (string.IsNullOrWhiteSpace(lineSpan.Path))
        {
            return null;
        }

        var relativePath = RelativePath(lineSpan.Path, lineSpan.HasMappedPath ? location.SourceTree?.FilePath : null);
        return new SourceLocation(
            relativePath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }

    internal string RelativePath(string path, string? sourcePath = null)
    {
        // #line paths are relative to the containing source, not the export's
        // working directory. Cache the resolved path so equal mappings in
        // different source directories remain distinct.
        return _relativePathCache.GetOrAdd(
            (path, Path.IsPathFullyQualified(path) ? null : sourcePath),
            static (key, repositoryRoot) =>
            {
                var baseDirectory = string.IsNullOrWhiteSpace(key.SourcePath)
                    ? repositoryRoot
                    : Path.GetDirectoryName(Path.GetFullPath(key.SourcePath.Replace('\\', '/'), repositoryRoot))!;
                var fullPath = Path.GetFullPath(key.Path.Replace('\\', '/'), baseDirectory);
                return ProjectIdentity.FromPath(fullPath, repositoryRoot).RelativePath;
            },
            _repositoryRoot);
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
