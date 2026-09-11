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
