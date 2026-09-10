namespace Graphify.CSharp.Incremental;

internal static class IncrementalPaths
{
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
}
