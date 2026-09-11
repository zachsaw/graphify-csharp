namespace Graphify.CSharp.Incremental;

internal interface IFileChangeWatcher : IDisposable
{
    event Action<string>? PathChanged;

    event Action<Exception>? Failed;

    string Root { get; }

    void Start();
}

internal interface IFileChangeWatcherFactory
{
    IFileChangeWatcher Create(string root);
}

internal sealed class FileSystemChangeWatcherFactory : IFileChangeWatcherFactory
{
    public IFileChangeWatcher Create(string root) => new FileSystemChangeWatcher(root);
}

internal sealed class FileSystemChangeWatcher : IFileChangeWatcher
{
    private const int MaximumNativeBufferSize = 64 * 1024;
    private readonly FileSystemWatcher _watcher;
    private readonly object _lifecycleGate = new();
    private int _disposed;

    public FileSystemChangeWatcher(string root)
    {
        Root = IncrementalPaths.CanonicalAbsolutePath(root);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"The file watcher root '{Root}' does not exist.");
        }

        _watcher = new FileSystemWatcher(Root, "*")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            InternalBufferSize = MaximumNativeBufferSize,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public event Action<string>? PathChanged;

    public event Action<Exception>? Failed;

    public string Root { get; }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        RaisePathChanged(args.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        // A rename is both a deletion and an addition from the semantic
        // indexer's perspective. Reporting both paths also covers a rename
        // where the destination is outside a project source set.
        RaisePathChanged(args.OldFullPath);
        RaisePathChanged(args.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var exception = args.GetException()
            ?? new IOException($"The file watcher for '{Root}' reported an unspecified error.");
        try
        {
            Failed?.Invoke(exception);
        }
        catch
        {
            // A watcher callback must not escape into the FileSystemWatcher
            // implementation. The host's normal recovery path owns errors.
        }
    }

    private void RaisePathChanged(string path)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !FileInventoryScanner.IsRelevantFilePath(path))
        {
            return;
        }

        try
        {
            PathChanged?.Invoke(IncrementalPaths.CanonicalAbsolutePath(path));
        }
        catch
        {
            // Consumers are required to enqueue only. A misbehaving consumer
            // cannot be allowed to terminate native watcher delivery.
        }
    }
}
