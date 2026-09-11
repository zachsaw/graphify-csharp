namespace Graphify.CSharp.Incremental;

internal sealed class WatcherLease : IDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private WatcherLease(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static WatcherLease Acquire(string outputPath, RefreshRequestIdentity request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(request);

        var path = ForOutput(outputPath, request);
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Watcher lease path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        try
        {
            return new WatcherLease(
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
                "A matching Graphify C# watcher is already running for this input identity.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"The watcher could not acquire its local lease at '{path}'.",
                exception);
        }
    }

    public static string ForOutput(string outputPath, RefreshRequestIdentity request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(request);
        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(IncrementalCachePath.ForOutput(outputPath))
                ?? throw new InvalidOperationException("The output path has no cache directory."),
            $"watch-{request.Digest}-{IncrementalRefreshControlChannel.OutputPathIdentity(outputPath)}.lock");
    }

    public static bool IsHeld(string path)
    {
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

        _stream.Dispose();
        try
        {
            File.Delete(Path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // A stale lock file is harmless: IsHeld probes the OS lease.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }
}
