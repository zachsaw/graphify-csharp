using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalWatcherHost : IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly object _inventoryGate = new();
    private readonly object _recoveryGate = new();
    private readonly object _healthGate = new();
    private readonly ProjectLoadRequest _request;
    private readonly string _outputPath;
    private readonly RefreshRequestIdentity _requestIdentity;
    private readonly IFileInventoryScanner _inventoryScanner;
    private readonly IFileChangeWatcherFactory _watcherFactory;
    private readonly IncrementalWatcherOptions _options;
    private readonly IReadOnlyList<string> _watchRoots;
    private readonly IncrementalIndexSession _session;
    private readonly SemaphoreSlim _recoverySignal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _healthy = NewHealthSource();
    private List<IFileChangeWatcher> _watchers = [];
    private FileInventorySnapshot? _inventory;
    private WatcherLease? _lease;
    private IncrementalRefreshControlServer? _controlServer;
    private Task? _startTask;
    private Task? _backupTask;
    private Task? _recoveryTask;
    private bool _recoveryPending;
    private bool _recoverySignalQueued;
    private string _recoveryReason = "The watcher requires recovery.";
    private bool _disposed;

    public IncrementalWatcherHost(
        ProjectLoadRequest request,
        string outputPath,
        IncrementalWatcherOptions? options = null,
        IProjectLoader? projectLoader = null,
        IncrementalCacheStore? cacheStore = null,
        IncrementalOutputPublisher? outputPublisher = null,
        IFileInventoryScanner? inventoryScanner = null,
        IFileChangeWatcherFactory? watcherFactory = null,
        Func<Guid>? sessionIdFactory = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        _outputPath = Path.GetFullPath(outputPath);
        _requestIdentity = new RefreshRequestIdentity(
            request.InputPath,
            request.RepositoryRoot,
            request.Configuration,
            request.TargetFramework);
        _options = options ?? new IncrementalWatcherOptions();
        _inventoryScanner = inventoryScanner ?? new FileInventoryScanner();
        _watcherFactory = watcherFactory ?? new FileSystemChangeWatcherFactory();
        _watchRoots = GetWatchRoots(request);
        _session = new IncrementalIndexSession(
            request,
            _outputPath,
            projectLoader,
            cacheStore,
            outputPublisher,
            sessionIdFactory,
            OnTrustLost);
    }

    public IncrementalIndexSession Session => _session;

    public string PipeName => IncrementalRefreshControlChannel.ForRequest(_requestIdentity);

    public string LeasePath => WatcherLease.ForOutput(_outputPath, _requestIdentity);

    public bool IsReady => _healthy.Task.IsCompletedSuccessfully;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task start;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _startTask ??= StartCoreAsync();
            start = _startTask;
        }

        return cancellationToken.CanBeCanceled
            ? start.WaitAsync(cancellationToken)
            : start;
    }

    public async Task<IncrementalRefreshResult> RefreshAsync(
        bool rebuild = false,
        CancellationToken cancellationToken = default)
    {
        await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
        return rebuild
            ? await _session.RebuildAsync(cancellationToken).ConfigureAwait(false)
            : await _session.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled
            ? _shutdown.Task.WaitAsync(cancellationToken)
            : _shutdown.Task;

    public async ValueTask DisposeAsync()
    {
        Task? start;
        Task? backup;
        Task? recovery;
        IncrementalRefreshControlServer? controlServer;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            start = _startTask;
            backup = _backupTask;
            recovery = _recoveryTask;
            controlServer = _controlServer;
            _stop.Cancel();
            _healthy.TrySetCanceled(_stop.Token);
        }

        DisposeWatchers();
        TryReleaseRecoverySignal();
        if (controlServer is not null)
        {
            await controlServer.DisposeAsync().ConfigureAwait(false);
        }

        await AwaitIgnoringCancellation(start).ConfigureAwait(false);
        await AwaitIgnoringCancellation(backup).ConfigureAwait(false);
        await AwaitIgnoringCancellation(recovery).ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);

        _lease?.Dispose();
        _lease = null;
        _recoverySignal.Dispose();
        _stop.Dispose();
        _shutdown.TrySetResult(true);
    }

    private async Task StartCoreAsync()
    {
        try
        {
            _lease = WatcherLease.Acquire(_outputPath, _requestIdentity);
            CreateAndStartWatchers();
            SetInventory(await ScanInventoryAsync(includeContentHashes: false, _stop.Token).ConfigureAwait(false));

            // Start recovery and backup loops before Roslyn initialization so
            // failures during the cold start are not lost.
            _recoveryTask = Task.Run(() => RecoveryLoopAsync(_stop.Token));
            _backupTask = Task.Run(() => BackupScanLoopAsync(_stop.Token));
            await _session.StartAsync(_stop.Token).ConfigureAwait(false);

            _controlServer = new IncrementalRefreshControlServer(
                PipeName,
                (rebuild, cancellationToken) => RefreshAsync(rebuild, cancellationToken));
            _controlServer.Start();
            MarkHealthy();
        }
        catch (Exception exception)
        {
            _healthy.TrySetException(exception);
            DisposeWatchers();
            _stop.Cancel();
            TryReleaseRecoverySignal();
            throw;
        }
    }

    private async Task BackupScanLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.BackupScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (!WatchRootsExist())
                    {
                        throw new DirectoryNotFoundException("A configured watcher root is no longer available.");
                    }

                    var current = await ScanInventoryAsync(includeContentHashes: false, cancellationToken)
                        .ConfigureAwait(false);
                    FileInventorySnapshot? previous;
                    lock (_inventoryGate)
                    {
                        previous = _inventory;
                        _inventory = current;
                    }

                    foreach (var path in current.CompareTo(previous))
                    {
                        _session.ReportFileChanged(path);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SignalWatcherFailure(
                        $"The backup file inventory failed: {exception.Message}",
                        reportToSession: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RecoveryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _recoverySignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    string reason;
                    lock (_recoveryGate)
                    {
                        if (!_recoveryPending)
                        {
                            _recoverySignalQueued = false;
                            break;
                        }

                        _recoveryPending = false;
                        reason = _recoveryReason;
                    }

                    MarkUnhealthy();
                    await RecoverWatcherAsync(reason, cancellationToken).ConfigureAwait(false);

                    lock (_recoveryGate)
                    {
                        if (_recoveryPending)
                        {
                            continue;
                        }

                        _recoverySignalQueued = false;
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverWatcherAsync(string reason, CancellationToken cancellationToken)
    {
        DisposeWatchers();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                CreateAndStartWatchers();
                SetInventory(await ScanInventoryAsync(includeContentHashes: false, cancellationToken).ConfigureAwait(false));
                await _session.RebuildAsync(cancellationToken).ConfigureAwait(false);
                MarkHealthy();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DisposeWatchers();
                SignalWatcherFailure(
                    $"Watcher recovery is retrying after '{reason}': {exception.Message}",
                    reportToSession: false);
                await Task.Delay(_options.RecoveryRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void CreateAndStartWatchers()
    {
        DisposeWatchers();
        var created = new List<IFileChangeWatcher>(_watchRoots.Count);
        try
        {
            foreach (var root in _watchRoots)
            {
                var watcher = _watcherFactory.Create(root);
                watcher.PathChanged += OnWatcherPathChanged;
                watcher.Failed += OnWatcherFailed;
                created.Add(watcher);
            }

            foreach (var watcher in created)
            {
                watcher.Start();
            }

            lock (_lifecycleGate)
            {
                _watchers = created;
            }
        }
        catch
        {
            foreach (var watcher in created)
            {
                watcher.PathChanged -= OnWatcherPathChanged;
                watcher.Failed -= OnWatcherFailed;
                watcher.Dispose();
            }

            throw;
        }
    }

    private void DisposeWatchers()
    {
        List<IFileChangeWatcher> watchers;
        lock (_lifecycleGate)
        {
            watchers = _watchers;
            _watchers = [];
        }

        foreach (var watcher in watchers)
        {
            watcher.PathChanged -= OnWatcherPathChanged;
            watcher.Failed -= OnWatcherFailed;
            watcher.Dispose();
        }
    }

    private void OnWatcherPathChanged(string path)
    {
        try
        {
            _session.ReportFileChanged(path);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException exception)
        {
            SignalWatcherFailure($"The watcher could not enqueue '{path}': {exception.Message}", reportToSession: false);
        }
    }

    private void OnWatcherFailed(Exception exception)
    {
        SignalWatcherFailure(
            $"The file watcher reported an error: {exception.Message}",
            reportToSession: true);
    }

    private void OnTrustLost(string reason)
    {
        SignalWatcherFailure(reason, reportToSession: false);
    }

    private void SignalWatcherFailure(string reason, bool reportToSession)
    {
        var releaseSignal = false;
        lock (_recoveryGate)
        {
            if (_disposed)
            {
                return;
            }

            _recoveryPending = true;
            _recoveryReason = reason;
            if (!_recoverySignalQueued)
            {
                _recoverySignalQueued = true;
                releaseSignal = true;
            }
        }

        MarkUnhealthy();
        if (reportToSession)
        {
            try
            {
                _session.ReportWatcherFailure(reason);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        if (releaseSignal)
        {
            TryReleaseRecoverySignal();
        }
    }

    private void MarkUnhealthy()
    {
        lock (_healthGate)
        {
            if (_healthy.Task.IsCompletedSuccessfully)
            {
                _healthy = NewHealthSource();
            }
        }
    }

    private void MarkHealthy()
    {
        lock (_healthGate)
        {
            _healthy.TrySetResult(true);
        }
    }

    private async Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task healthy;
            lock (_healthGate)
            {
                healthy = _healthy.Task;
            }

            await healthy.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (IsReady)
            {
                return;
            }
        }
    }

    private async Task<FileInventorySnapshot> ScanInventoryAsync(
        bool includeContentHashes,
        CancellationToken cancellationToken) =>
        await _inventoryScanner
            .ScanAsync(_watchRoots, _request.RepositoryRoot, includeContentHashes, cancellationToken)
            .ConfigureAwait(false);

    private void SetInventory(FileInventorySnapshot snapshot)
    {
        lock (_inventoryGate)
        {
            _inventory = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }
    }

    private bool WatchRootsExist() => _watchRoots.All(Directory.Exists);

    private static IReadOnlyList<string> GetWatchRoots(ProjectLoadRequest request)
    {
        var repositoryRoot = IncrementalPaths.CanonicalAbsolutePath(request.RepositoryRoot);
        var inputPath = IncrementalPaths.CanonicalAbsolutePath(request.InputPath);
        var inputRoot = Directory.Exists(inputPath)
            ? inputPath
            : Path.GetDirectoryName(inputPath) ?? repositoryRoot;
        var roots = new List<string> { repositoryRoot };
        if (!IsUnderDirectory(inputRoot, repositoryRoot))
        {
            roots.Add(inputRoot);
        }

        return roots
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(root => root, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsUnderDirectory(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private void TryReleaseRecoverySignal()
    {
        try
        {
            _recoverySignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static TaskCompletionSource<bool> NewHealthSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AwaitIgnoringCancellation(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
