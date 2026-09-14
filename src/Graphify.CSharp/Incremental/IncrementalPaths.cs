namespace Graphify.CSharp.Incremental;

internal static class IncrementalPaths
{
    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string CanonicalAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path.Trim());
        fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        return fullPath.Replace('\\', '/');
    }

    /// <summary>
    /// Resolves the existing portion of a path through filesystem aliases.
    ///
    /// The normal canonical path is intentionally lexical because it is used
    /// on the watcher hot path. Export validation is rare and may instead need
    /// to answer what an OS will actually open when a parent directory is a
    /// symlink or junction. The final file may be absent; in that case all
    /// existing parents are still resolved and the missing suffix is appended
    /// lexically.
    /// </summary>
    public static bool TryResolvePhysicalPath(string path, out string resolvedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var lexicalPath = CanonicalAbsolutePath(path);
        resolvedPath = lexicalPath;
        try
        {
            resolvedPath = ResolvePhysicalPath(
                Path.GetFullPath(path.Trim()),
                new HashSet<string>(PathComparer));
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            // Export callers treat an unresolved path conservatively. A
            // failure here must never turn an alias into an unvalidated write.
            resolvedPath = lexicalPath;
            return false;
        }
    }

    private static string ResolvePhysicalPath(
        string fullPath,
        HashSet<string> visitedLinks)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("The path has no filesystem root.", nameof(fullPath));
        }

        var current = root;
        var components = fullPath[root.Length..]
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            var entry = FindExistingEntry(current, components[index]);
            if (entry is null)
            {
                // A missing component cannot hide another existing symlink.
                // Preserve support for destinations whose parent directories
                // have not been created yet.
                for (; index < components.Length; index++)
                {
                    current = Path.Combine(current, components[index]);
                }

                return CanonicalAbsolutePath(current);
            }

            var target = entry.ResolveLinkTarget(returnFinalTarget: false);
            if (target is null)
            {
                current = entry.FullName;
                continue;
            }

            var linkPath = CanonicalAbsolutePath(entry.FullName);
            if (!visitedLinks.Add(linkPath))
            {
                throw new IOException($"The filesystem alias '{linkPath}' contains a cycle.");
            }

            try
            {
                // Resolve the target from its own root as well. macOS, for
                // example, may expose /var as a link to /private/var even when
                // that alias is embedded in another link's target path.
                current = ResolvePhysicalPath(
                    Path.GetFullPath(target.FullName),
                    visitedLinks);
            }
            finally
            {
                visitedLinks.Remove(linkPath);
            }
        }

        return CanonicalAbsolutePath(current);
    }

    private static FileSystemInfo? FindExistingEntry(string parent, string name)
    {
        var candidatePath = Path.Combine(parent, name);
        var candidate = GetExistingEntry(candidatePath);
        if (candidate is null)
        {
            var parentDirectory = new DirectoryInfo(parent);
            if (!parentDirectory.Exists)
            {
                return null;
            }

            // A dangling link is not reported as an existing FileInfo or
            // DirectoryInfo. Enumerating the parent still exposes the link so
            // a subsequent component cannot bypass validation.
            foreach (var entry in parentDirectory.EnumerateFileSystemInfos())
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        var containingDirectory = new DirectoryInfo(parent);
        if (!containingDirectory.Exists)
        {
            return candidate;
        }

        // When the filesystem is case-insensitive, enumeration gives us the
        // directory entry's spelling. On a case-sensitive filesystem the exact
        // match wins before the ignore-case fallback, so distinct names remain
        // distinct.
        FileSystemInfo? caseInsensitiveMatch = null;
        foreach (var entry in containingDirectory.EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }

            if (caseInsensitiveMatch is null
                && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitiveMatch = entry;
            }
        }

        return caseInsensitiveMatch ?? candidate;
    }

    private static FileSystemInfo? GetExistingEntry(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (file.Exists)
        {
            return file;
        }

        var directory = new DirectoryInfo(path);
        directory.Refresh();
        return directory.Exists ? directory : null;
    }

    public static string CanonicalRelativePath(string path, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var fullPath = Path.GetFullPath(path.Trim());
        var fullRoot = Path.GetFullPath(repositoryRoot.Trim());
        return Domain.CanonicalText.NormalizePath(Path.GetRelativePath(fullRoot, fullPath));
    }

    public static bool IsUnderDirectory(string path, string parent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);

        var normalizedPath = CanonicalAbsolutePath(path);
        var normalizedParent = CanonicalAbsolutePath(parent);
        var relative = Path.GetRelativePath(
            normalizedParent.Replace('/', Path.DirectorySeparatorChar),
            normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    public static bool IsPathOrUnder(string path, string parent) =>
        string.Equals(CanonicalAbsolutePath(path), CanonicalAbsolutePath(parent), PathComparison)
        || IsUnderDirectory(path, parent);
}
