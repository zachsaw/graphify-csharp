namespace Graphify.CSharp.Incremental;

internal interface IFileInventoryScanner
{
    Task<FileInventorySnapshot> ScanAsync(
        IReadOnlyList<string> roots,
        string repositoryRoot,
        bool includeContentHashes = false,
        CancellationToken cancellationToken = default);
}

internal sealed class FileInventoryScanner : IFileInventoryScanner
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Task<FileInventorySnapshot> ScanAsync(
        IReadOnlyList<string> roots,
        string repositoryRoot,
        bool includeContentHashes = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRoot = IncrementalPaths.CanonicalAbsolutePath(repositoryRoot);
        var normalizedRoots = NormalizeRoots(roots);
        var entries = new Dictionary<string, FileInventoryEntry>(PathComparer);
        var pendingDirectories = new Stack<string>(normalizedRoots.Reverse());
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            foreach (var entryPath in EnumerateEntries(directory, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = ReadAttributes(entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ShouldSkipDirectory(entryPath, attributes))
                    {
                        pendingDirectories.Push(entryPath);
                    }

                    continue;
                }

                if (!IsRelevantFile(entryPath))
                {
                    continue;
                }

                try
                {
                    var fullPath = IncrementalPaths.CanonicalAbsolutePath(entryPath);
                    var fingerprint = SourceFingerprint.FromFile(
                        fullPath,
                        normalizedRoot,
                        includeContentHashes);
                    if (fingerprint.Exists)
                    {
                        entries[fullPath] = new FileInventoryEntry(fullPath, fingerprint);
                    }
                }
                catch (FileNotFoundException)
                {
                    // A concurrent delete is represented by the difference
                    // from the previous snapshot and will also be delivered by
                    // FileSystemWatcher when possible.
                }
                catch (DirectoryNotFoundException)
                {
                    // See the FileNotFoundException case above.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidDataException(
                        $"The file inventory could not read '{entryPath}': {exception.Message}",
                        exception);
                }
            }
        }

        return Task.FromResult(new FileInventorySnapshot(entries.Values));
    }

    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string> roots)
    {
        var normalized = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IncrementalPaths.CanonicalAbsolutePath)
            .Distinct(PathComparer)
            .OrderBy(root => root, PathComparer)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one file inventory root is required.", nameof(roots));
        }

        foreach (var root in normalized)
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"The file inventory root '{root}' does not exist.");
            }
        }

        return normalized
            .Where((root, index) => !normalized
                .Take(index)
                .Any(parent => IsUnderDirectory(root, parent)))
            .ToArray();
    }

    private static IEnumerable<string> EnumerateEntries(string directory, CancellationToken cancellationToken)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"The file inventory could not enumerate '{directory}': {exception.Message}",
                exception);
        }

        Array.Sort(entries, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    private static FileAttributes ReadAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return FileAttributes.Normal;
        }
        catch (DirectoryNotFoundException)
        {
            return FileAttributes.Normal;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"The file inventory could not inspect '{path}': {exception.Message}",
                exception);
        }
    }

    private static bool ShouldSkipDirectory(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".e2e", StringComparison.OrdinalIgnoreCase)
            || name.Equals("graphify-out", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TestResults", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRelevantFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return IsRelevantFile(path);
    }

    private static bool IsRelevantFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderDirectory(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
