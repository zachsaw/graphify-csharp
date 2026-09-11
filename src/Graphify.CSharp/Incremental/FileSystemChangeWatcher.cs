using System.Threading.Channels;

namespace Graphify.CSharp.Incremental;

internal interface IFileChangeWatcher : IDisposable
{
    event Action<FileChangeEvent>? PathChanged;

    event Action<Exception>? Failed;

    string Root { get; }

    void Start();
}

internal interface IFileChangeWatcherFactory
{
    IFileChangeWatcher Create(WatcherRoot root, Func<FileChangeEvent, bool> shouldCapture);
}

internal sealed class FileSystemChangeWatcherFactory : IFileChangeWatcherFactory
{
    public IFileChangeWatcher Create(WatcherRoot root, Func<FileChangeEvent, bool> shouldCapture) =>
        new FileSystemChangeWatcher(root, shouldCapture);
}

internal sealed class FileSystemChangeWatcher : IFileChangeWatcher
{
    private const int MaximumNativeBufferSize = 64 * 1024;
    private const int EventQueueCapacity = 4096;
    private readonly FileSystemWatcher _watcher;
    private readonly Func<FileChangeEvent, bool> _shouldCapture;
    private readonly Channel<FileChangeEvent> _pendingChanges = Channel.CreateBounded<FileChangeEvent>(
        new BoundedChannelOptions(EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _dispatchStop = new();
    private readonly object _lifecycleGate = new();
    private Task? _dispatchTask;
    private int _dispatchCallbackThreadId = -1;
    private int _disposed;

    public FileSystemChangeWatcher(WatcherRoot root, Func<FileChangeEvent, bool> shouldCapture)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(shouldCapture);
        Root = root.CanonicalPath;
        _shouldCapture = shouldCapture;
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"The file watcher root '{Root}' does not exist.");
        }

        _watcher = new FileSystemWatcher(Root, "*")
        {
            IncludeSubdirectories = root.IncludeSubdirectories,
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

    public event Action<FileChangeEvent>? PathChanged;

    public event Action<Exception>? Failed;

    public string Root { get; }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _dispatchTask ??= Task.Run(() => DispatchLoopAsync(_dispatchStop.Token));
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Dispose()
    {
        Task? dispatchTask;
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
            _pendingChanges.Writer.TryComplete();
            _dispatchStop.Cancel();
            dispatchTask = _dispatchTask;
        }

        // Disposal normally happens from the host's lifecycle/recovery task,
        // not from this dispatch loop. Wait for the worker so a test or a
        // recovery can safely remove the watched directory immediately. A
        // consumer is allowed to dispose the watcher from its own callback;
        // in that case the worker will observe cancellation after returning.
        if (dispatchTask is not null
            && Environment.CurrentManagedThreadId != Volatile.Read(ref _dispatchCallbackThreadId))
        {
            try
            {
                dispatchTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_dispatchStop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_dispatchStop.IsCancellationRequested)
            {
            }

            _dispatchStop.Dispose();
        }
        else if (dispatchTask is not null)
        {
            // A PathChanged consumer may dispose the watcher from the
            // dispatch callback itself. Joining here would wait for the
            // callback that is currently executing. Dispose the cancellation
            // source after the loop has unwound instead.
            _ = dispatchTask.ContinueWith(
                _ => _dispatchStop.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _dispatchStop.Dispose();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        EnqueueChange(new FileChangeEvent(MapChangeKind(args.ChangeType), args.FullPath));
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        EnqueueChange(new FileChangeEvent(FileChangeKind.Renamed, args.FullPath, args.OldFullPath));
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

    private void RaisePathChanged(FileChangeEvent change)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (!_shouldCapture(change))
            {
                return;
            }

            PathChanged?.Invoke(change);
        }
        catch
        {
            // Consumers are required to enqueue only. A misbehaving consumer
            // cannot be allowed to terminate native watcher delivery.
        }
    }

    private void EnqueueChange(FileChangeEvent change)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_pendingChanges.Writer.TryWrite(change))
        {
            ReportFailure(new IOException(
                $"The file watcher event queue for '{Root}' overflowed before an event could be classified."));
        }
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in _pendingChanges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    continue;
                }

                Volatile.Write(ref _dispatchCallbackThreadId, Environment.CurrentManagedThreadId);
                try
                {
                    RaisePathChanged(ResolveEntryType(change));
                }
                finally
                {
                    Volatile.Write(ref _dispatchCallbackThreadId, -1);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void ReportFailure(Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

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

    private static FileChangeEvent ResolveEntryType(FileChangeEvent change)
    {
        if (change.IsDirectory is not null)
        {
            return change;
        }

        return change with
        {
            IsDirectory = TryGetEntryType(change.Path)
                ?? TryGetEntryType(change.OldPath),
        };
    }

    private static bool? TryGetEntryType(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (Directory.Exists(path))
            {
                return true;
            }

            if (File.Exists(path))
            {
                return false;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static FileChangeKind MapChangeKind(WatcherChangeTypes changeType) =>
        changeType switch
        {
            WatcherChangeTypes.Created => FileChangeKind.Created,
            WatcherChangeTypes.Deleted => FileChangeKind.Deleted,
            _ => FileChangeKind.Changed,
        };
}
