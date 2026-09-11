namespace Graphify.CSharp.Incremental;

/// <summary>
/// Owns one publication destination across all Graphify C# processes.
/// </summary>
internal sealed class OutputDestinationLease : IDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private OutputDestinationLease(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static OutputDestinationLease Acquire(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var path = ForOutput(outputPath);
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Output lease path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        try
        {
            return new OutputDestinationLease(
                path,
                new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.SequentialScan));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"The output destination '{IncrementalPaths.CanonicalAbsolutePath(outputPath)}' is already owned by another Graphify C# process.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"The output destination '{IncrementalPaths.CanonicalAbsolutePath(outputPath)}' could not be leased.",
                exception);
        }
    }

    public static string ForOutput(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = System.IO.Path.GetFullPath(outputPath);
        var outputDirectory = System.IO.Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("The output path has no parent directory.");
        var outputFileName = System.IO.Path.GetFileName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            throw new InvalidOperationException($"Output path '{outputPath}' has no file name.");
        }

        // Keep the destination spelling in the filesystem path rather than
        // hashing it. The filesystem then performs the correct equivalence
        // check: csharp.json and CSHARP.json share this sidecar on a
        // case-insensitive volume, while remaining independent on a
        // case-sensitive volume. The same applies to parent-directory aliases
        // resolved by the OS (including supported symlinked parent paths).
        return System.IO.Path.Combine(
            outputDirectory,
            ".graphify-csharp",
            $"output-{outputFileName}.lock");
    }

    public static bool IsHeld(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                options: FileOptions.SequentialScan);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Deliberately leave the inode/path in place. Deleting a stable lock
        // path after releasing the OS handle allows a successor to acquire a
        // newly-created file and then have this late cleanup delete its lock.
        // An unheld lock file is harmless: IsHeld probes the OS lease and the
        // next owner reuses the same path.
        _stream.Dispose();
    }
}
