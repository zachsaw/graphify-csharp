using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalWatcherHost : IAsyncDisposable, IWatcherManagementHost
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
    private readonly WatcherManagementOptions? _managementOptions;
    private readonly WatcherSessionRegistry? _managementRegistry;
    private readonly IReadOnlyList<WatcherRoot> _baseWatchRoots;
    private readonly IncrementalIndexSession _session;
    private readonly SemaphoreSlim _recoverySignal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _workStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _healthy = NewHealthSource();
    private List<WatcherRoot> _watchRoots;
    private List<IFileChangeWatcher> _watchers = [];
    private FileInventorySnapshot? _inventory;
    private WatcherInputSnapshot _inputSnapshot;
    private OutputDestinationLease? _outputLease;
    private WatcherLease? _lease;
    private IncrementalRefreshControlServer? _controlServer;
    private WatcherManagementServer? _managementServer;
    private WatcherSessionDescriptor? _managementDescriptor;
    private bool _managementRegistered;
    private Task? _startTask;
    private Task? _backupTask;
    private Task? _recoveryTask;
    private Task? _disposeTask;
    private bool _recoveryPending;
    private bool _recoverySignalQueued;
    private string _recoveryReason = "The watcher requires recovery.";
    private int _bootstrapUncertaintyReported;
    private int _disposed;

    public IncrementalWatcherHost(
        ProjectLoadRequest request,
        string outputPath,
        IncrementalWatcherOptions? options = null,
        IProjectLoader? projectLoader = null,
        IncrementalCacheStore? cacheStore = null,
        IncrementalOutputPublisher? outputPublisher = null,
        IFileInventoryScanner? inventoryScanner = null,
        IFileChangeWatcherFactory? watcherFactory = null,
        Func<Guid>? sessionIdFactory = null,
        WatcherManagementOptions? managementOptions = null)
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
        _managementOptions = managementOptions;
        _managementRegistry = managementOptions is null
            ? null
            : new WatcherSessionRegistry(managementOptions.StateDirectory);
        _inventoryScanner = inventoryScanner ?? new FileInventoryScanner();
        _watcherFactory = watcherFactory ?? new FileSystemChangeWatcherFactory();
        _baseWatchRoots = GetWatchRoots(request);
        _watchRoots = _baseWatchRoots.ToList();
        _inputSnapshot = WatcherInputSnapshot.CreateBootstrap(
            _watchRoots.Select(root => root.CanonicalPath),
            _outputPath,
            IncrementalCachePath.ForOutput(_outputPath),
            _watchRoots);
        _session = new IncrementalIndexSession(
            request,
            _outputPath,
            projectLoader,
            cacheStore,
            outputPublisher,
            sessionIdFactory,
            OnTrustLost,
            OnInputSnapshotChanged);
    }

    public IncrementalIndexSession Session => _session;

    public string PipeName => IncrementalRefreshControlChannel.ForRequest(_requestIdentity, _outputPath);

    public string LeasePath => WatcherLease.ForOutput(_outputPath, _requestIdentity);

    public string OutputLeasePath => OutputDestinationLease.ForOutput(_outputPath);

    internal string ManagementPipeName => _managementOptions is null
        ? throw new InvalidOperationException("Management is not enabled for this watcher host.")
        : WatcherManagementProtocol.ForSession(_session.SessionId, _managementOptions.StateDirectory);

    internal string ManagementDescriptorPath => _managementRegistry is null
        ? throw new InvalidOperationException("Management is not enabled for this watcher host.")
        : _managementRegistry.DescriptorPath(_session.SessionId);

    public bool IsReady
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            lock (_healthGate)
            {
                return Volatile.Read(ref _disposed) == 0
                    && _healthy.Task.IsCompletedSuccessfully;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task start;
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
            // StartCoreAsync performs synchronous lease, watcher, and inventory
            // work before its first incomplete await. Run that coarse startup
            // operation outside the lifecycle lock so management stop can
            // acquire the lock and cancel it while the initial scan is running.
            _startTask ??= Task.Run(StartCoreAsync);
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
        while (true)
        {
            await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            var trustVersion = _session.EventTrustVersion;
            var result = rebuild
                ? await _session.RebuildAsync(cancellationToken).ConfigureAwait(false)
                : await _session.RefreshAsync(cancellationToken).ConfigureAwait(false);

            // A delivery loss can race with extraction. The session correctly
            // refuses to acknowledge that epoch, but its foreground command
            // can still finish with the graph it extracted before recovery was
            // queued. Do not return that result: wait for recovery and issue
            // the same foreground request again so the caller receives the
            // graph belonging to a trusted boundary.
            await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            if (_session.IsEventTrustValid(trustVersion))
            {
                return result;
            }
        }
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled
            ? _shutdown.Task.WaitAsync(cancellationToken)
            : _shutdown.Task;

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposeTask = DisposeCoreAsync();
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    internal Task WaitForStopRequestedAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled
            ? _stopRequested.Task.WaitAsync(cancellationToken)
            : _stopRequested.Task;

    internal Task WaitForWorkStoppedAsync(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? _workStopped.Task.WaitAsync(cancellationToken)
            : _workStopped.Task;

    WatcherInspectionSnapshot IWatcherManagementHost.GetInspectionSnapshot() => GetInspectionSnapshot();

    void IWatcherManagementHost.RequestStop() => RequestStop();

    Task IWatcherManagementHost.WaitForWorkStoppedAsync(CancellationToken cancellationToken) =>
        WaitForWorkStoppedAsync(cancellationToken);

    internal void RequestStop()
    {
        if (_stopRequested.TrySetResult(true))
        {
            // The host is the sole owner of workload and management-server
            // cleanup. Starting the idempotent cleanup task here closes the
            // race where startup failure handling and the CLI both try to
            // dispose the server that is serving this stop request.
            _ = ObserveDetachedDisposeAsync();
        }
    }

    private async Task ObserveDetachedDisposeAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The stop handler observes the same failure through the work
            // completion barrier. This detached lifetime owner must still
            // observe its task so a cleanup failure is never unobserved.
        }
    }

    internal WatcherInspectionSnapshot GetInspectionSnapshot()
    {
        var generation = _session.Generation;
        var ready = IsReady;
        var sessionState = _session.Status switch
        {
            IncrementalSessionStatus.Created => "starting",
            var status => status.ToString().ToLowerInvariant(),
        };
        var lifecycleState = _workStopped.Task.IsCompletedSuccessfully
            ? "stopped"
            : _stopRequested.Task.IsCompleted
                ? "stopping"
                : !ready && sessionState is "ready" or "refreshing"
                    ? "recovering"
                : sessionState;
        var endpoint = _managementOptions is null
            ? string.Empty
            : WatcherManagementProtocol.ForSession(_session.SessionId, _managementOptions.StateDirectory);
        return new WatcherInspectionSnapshot(
            _session.SessionId,
            Environment.ProcessId,
            WatcherProcessIdentity.CurrentStartTimeUtcTicks(),
            _request.InputPath,
            _request.RepositoryRoot,
            _request.Configuration,
            _request.TargetFramework,
            _outputPath,
            endpoint,
            _managementOptions?.ToolVersion ?? "unknown",
            WatcherManagementProtocol.CurrentVersion,
            lifecycleState,
            ready,
            generation.EventGeneration,
            generation.IndexedGeneration,
            generation.PublishedGeneration);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            Exception? cleanupFailure = null;
            void RecordCleanupFailure(Exception? exception)
            {
                cleanupFailure ??= exception;
            }

            Task? start;
            IncrementalRefreshControlServer? controlServer;
            WatcherManagementServer? managementServer;
            WatcherSessionDescriptor? managementDescriptor;
            var managementRegistered = false;
            lock (_lifecycleGate)
            {
                start = _startTask;
                controlServer = TakeControlServerLocked();
                // Take management ownership in the same critical section as the
                // stop signal. Startup failure cleanup cannot race this handoff
                // and dispose the server that may be serving the stop request.
                managementServer = _managementServer;
                _managementServer = null;
                managementDescriptor = _managementDescriptor;
                _managementDescriptor = null;
                managementRegistered = _managementRegistered;
                _managementRegistered = false;
                _stopRequested.TrySetResult(true);
                _stop.Cancel();
            }

            lock (_healthGate)
            {
                _healthy.TrySetCanceled(_stop.Token);
            }

            RecordCleanupFailure(TryDisposeWatchers());
            TryReleaseRecoverySignal();
            if (controlServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false));
            }

            // A faulted start task is the startup error already observed by
            // StartAsync, not a cleanup failure. Observe it so it cannot become
            // unhandled, while allowing a host that never acquired ownership
            // to be disposed normally by an await-using scope.
            await AwaitIgnoringCancellation(start).ConfigureAwait(false);

            Task? backup;
            Task? recovery;
            lock (_lifecycleGate)
            {
                backup = _backupTask;
                recovery = _recoveryTask;
                controlServer = TakeControlServerLocked();
            }

            if (controlServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false));
            }

            RecordCleanupFailure(await CaptureCleanupFailureAsync(backup).ConfigureAwait(false));
            RecordCleanupFailure(await CaptureCleanupFailureAsync(recovery).ConfigureAwait(false));

            // Recovery can be between creating and publishing a watcher set when
            // shutdown starts. All producer tasks are stopped now, so this final
            // pass closes anything that was published after the first pass.
            RecordCleanupFailure(TryDisposeWatchers());

            WatcherLease? lease;
            OutputDestinationLease? outputLease;
            lock (_lifecycleGate)
            {
                controlServer = TakeControlServerLocked();
                lease = _lease;
                _lease = null;
                outputLease = _outputLease;
                _outputLease = null;
            }

            if (controlServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false));
            }

            RecordCleanupFailure(TryDispose(lease));
            RecordCleanupFailure(await CaptureCleanupFailureAsync(_session.DisposeAsync().AsTask()).ConfigureAwait(false));
            RecordCleanupFailure(TryDispose(outputLease));

            if (managementRegistered && managementDescriptor is not null)
            {
                try
                {
                    _managementRegistry!.Remove(managementDescriptor.SessionId);
                }
                catch (Exception exception)
                {
                    RecordCleanupFailure(exception);
                }
            }

            // The stop handler awaits this barrier and must not be made to await
            // disposal of the server that is executing that request. The endpoint
            // stays alive long enough to send the final response below.
            if (cleanupFailure is null)
            {
                _workStopped.TrySetResult(true);
            }
            else
            {
                _workStopped.TrySetException(cleanupFailure);
            }

            if (managementServer is not null)
            {
                try
                {
                    // Keep the endpoint accepting inspection (and idempotent
                    // stop requests) until all workload cleanup has completed.
                    // The stop handler relies on this barrier to return a
                    // truthful final state to the client.
                    managementServer.BeginShutdown();
                }
                catch (Exception exception)
                {
                    RecordCleanupFailure(exception);
                }

                RecordCleanupFailure(await CaptureCleanupFailureAsync(managementServer.DisposeAsync().AsTask()).ConfigureAwait(false));
            }

            try
            {
                _recoverySignal.Dispose();
                _stop.Dispose();
            }
            catch (Exception exception)
            {
                RecordCleanupFailure(exception);
            }

            _shutdown.TrySetResult(true);

            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }
        catch (Exception exception)
        {
            // Never strand a management stop handler if an unexpected cleanup
            // failure occurs before the normal completion point.
            _workStopped.TrySetException(exception);
            _shutdown.TrySetResult(true);
            throw;
        }
    }

    private async Task StartCoreAsync()
    {
        WatcherLease? lease = null;
        OutputDestinationLease? outputLease = null;
        try
        {
            // Publish the request-specific lease first so a matching client
            // waits for this startup, then acquire the destination-only lease
            // atomically before any cache or output operation can begin.
            lease = WatcherLease.Acquire(_outputPath, _requestIdentity);
            outputLease = OutputDestinationLease.Acquire(_outputPath);
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    lease.Dispose();
                    outputLease.Dispose();
                    lease = null;
                    outputLease = null;
                    return;
                }

                _lease = lease;
                _outputLease = outputLease;
                lease = null;
                outputLease = null;
            }

            // The management endpoint is published only after ownership has
            // been acquired, but before any Roslyn/MSBuild work begins. This
            // makes startup observable without exposing an unmanaged owner.
            if (_managementOptions is not null)
            {
                await StartManagementAsync(_stop.Token).ConfigureAwait(false);
            }

            CreateAndStartWatchers();
            await RefreshInventoryBaselineAsync(
                    _stop.Token,
                    allowBootstrap: true)
                .ConfigureAwait(false);

            // Start recovery and backup loops before Roslyn initialization so
            // failures during the cold start are not lost.
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _recoveryTask = Task.Run(() => RecoveryLoopAsync(_stop.Token));
            }

            await _session.StartAsync(_stop.Token).ConfigureAwait(false);
            await RefreshInventoryBaselineAsync(
                    _stop.Token,
                    reconcileDifferences: true)
                .ConfigureAwait(false);

            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _backupTask = Task.Run(() => BackupScanLoopAsync(_stop.Token));
                }
            }

            var controlServer = new IncrementalRefreshControlServer(
                PipeName,
                _requestIdentity,
                _outputPath,
                (rebuild, cancellationToken) => RefreshAsync(rebuild, cancellationToken));
            var installed = false;
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _controlServer = controlServer;
                    controlServer.Start();
                    installed = true;
                }
            }

            if (!installed)
            {
                await AwaitIgnoringCancellation(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false);
                return;
            }

            MarkHealthy();
            // Startup is a foreground readiness barrier too. A watcher error
            // or uncertain bootstrap event may have queued recovery while the
            // initial Roslyn load was running; do not let StartAsync return
            // until that recovery has either completed or the host has been
            // stopped.
            await WaitUntilHealthyAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_healthGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _healthy.TrySetException(exception);
                }
            }

            IncrementalRefreshControlServer? controlServer;
            lock (_lifecycleGate)
            {
                controlServer = TakeControlServerLocked();
            }

            if (controlServer is not null)
            {
                await AwaitIgnoringCancellation(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false);
            }

            WatcherLease? activeLease;
            OutputDestinationLease? activeOutputLease;
            WatcherManagementServer? managementServer;
            WatcherSessionDescriptor? managementDescriptor;
            var managementRegistered = false;
            lock (_lifecycleGate)
            {
                activeLease = _lease;
                _lease = null;
                activeOutputLease = _outputLease;
                _outputLease = null;
                managementServer = _managementServer;
                _managementServer = null;
                managementDescriptor = _managementDescriptor;
                _managementDescriptor = null;
                managementRegistered = _managementRegistered;
                _managementRegistered = false;
            }

            DisposeWatchers();
            _stop.Cancel();
            TryReleaseRecoverySignal();
            // StartAsync can be cancelled while the session is inside a
            // synchronous output or cache commit. Join the session before
            // releasing destination ownership so an old startup cannot
            // publish after a successor acquires the same output.
            await AwaitIgnoringCancellation(_session.DisposeAsync().AsTask()).ConfigureAwait(false);
            activeLease?.Dispose();
            activeOutputLease?.Dispose();
            lease?.Dispose();
            outputLease?.Dispose();
            if (managementRegistered && managementDescriptor is not null)
            {
                _managementRegistry!.Remove(managementDescriptor.SessionId);
            }

            try
            {
                // Keep startup failure cleanup observable until the workload
                // has released its resources, matching the normal disposal
                // path. This also lets a concurrent stop handler inspect the
                // real host while startup unwinds.
                managementServer?.BeginShutdown();
            }
            catch
            {
                // Preserve the original startup failure; the best-effort
                // server disposal below still observes the transport task.
            }

            await AwaitIgnoringCancellation(managementServer?.DisposeAsync().AsTask()).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartManagementAsync(CancellationToken cancellationToken)
    {
        var options = _managementOptions
            ?? throw new InvalidOperationException("Management options were not configured.");
        var registry = _managementRegistry
            ?? throw new InvalidOperationException("Management registry was not configured.");
        var server = new WatcherManagementServer(_session.SessionId, this, options);
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            var descriptor = new WatcherSessionDescriptor(
                WatcherSessionRegistry.DescriptorSchemaVersion,
                _session.SessionId,
                Environment.ProcessId,
                WatcherProcessIdentity.CurrentStartTimeUtcTicks(),
                server.PipeName,
                _request.InputPath,
                _request.RepositoryRoot,
                _request.Configuration,
                _request.TargetFramework,
                _outputPath,
                options.ToolVersion,
                WatcherManagementProtocol.CurrentVersion);

            var disposeLocalServer = false;
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    disposeLocalServer = true;
                }
                else
                {
                    _managementServer = server;
                    _managementDescriptor = descriptor;
                    registry.Register(descriptor);
                    _managementRegistered = true;
                }
            }

            if (disposeLocalServer)
            {
                await AwaitIgnoringCancellation(server.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
        }
        catch
        {
            await AwaitIgnoringCancellation(server.DisposeAsync().AsTask()).ConfigureAwait(false);
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

                    var scan = await ScanInventoryAsync(includeContentHashes: false, cancellationToken)
                        .ConfigureAwait(false);
                    if (!TrySetInventory(scan, out var previous))
                    {
                        // A workspace reload or coverage replacement completed
                        // while this scan was running. Its result describes an
                        // obsolete scope and must not become the new baseline.
                        continue;
                    }

                    foreach (var change in scan.Snapshot.CompareToEvents(previous))
                    {
                        _session.ReportFileChanged(change);
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

                    var recovered = false;
                    lock (_recoveryGate)
                    {
                        if (_recoveryPending)
                        {
                            continue;
                        }

                        _recoverySignalQueued = false;
                        recovered = true;
                    }

                    if (recovered)
                    {
                        // The recovery signal is no longer queued, so this
                        // transition can safely publish healthy state. Doing
                        // it inside RecoverWatcherAsync would observe its own
                        // still-queued signal and leave the host unhealthy.
                        MarkHealthy();
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
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        DisposeWatchers();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The previously evaluated snapshot may contain linked roots
                // that have since been deleted. Re-establish only the stable
                // request coverage first; the reload below will publish the
                // new evaluated roots after MSBuild has had a chance to remove
                // obsolete links.
                CreateAndStartWatchers(_baseWatchRoots);
                await RefreshInventoryBaselineAsync(
                        cancellationToken,
                        allowBootstrap: true)
                    .ConfigureAwait(false);
                // Recovery restores the in-memory Roslyn/catalog state and
                // trust boundary, but it must not publish a new public graph.
                // JSON publication remains the explicit refresh barrier.
                await _session.RecoverAsync(cancellationToken).ConfigureAwait(false);
                await RefreshInventoryBaselineAsync(
                        cancellationToken,
                        reconcileDifferences: true,
                        publishOutput: false)
                    .ConfigureAwait(false);
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

    private void CreateAndStartWatchers(IEnumerable<WatcherRoot>? roots = null)
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
            DisposeWatchersLocked();
            var rootList = NormalizeWatchRoots(roots ?? _watchRoots).ToArray();
            var created = CreateWatchersLocked(rootList);
            _watchers = created;
            _watchRoots = rootList.ToList();
        }
    }

    private List<IFileChangeWatcher> CreateWatchersLocked(IEnumerable<WatcherRoot> roots)
    {
        var rootList = roots.ToArray();
        var created = new List<IFileChangeWatcher>(rootList.Length);
        try
        {
            foreach (var root in rootList)
            {
                var watcher = _watcherFactory.Create(root, ShouldCaptureWatcherEvent);
                watcher.PathChanged += OnWatcherPathChanged;
                watcher.Failed += OnWatcherFailed;
                created.Add(watcher);
            }

            foreach (var watcher in created)
            {
                watcher.Start();
            }

            return created;
        }
        catch
        {
            DisposeWatcherList(created);
            throw;
        }
    }

    private bool ReplaceWatchersForSnapshot(WatcherInputSnapshot snapshot)
    {
        var desiredRoots = NormalizeWatchRoots(_baseWatchRoots.Concat(snapshot.WatchRoots));
        lock (_lifecycleGate)
        {
            ThrowIfDisposedLocked();
            if (WatchRootsEqual(_watchRoots, desiredRoots))
            {
                return false;
            }

            // Establish new coverage before tearing down old subscriptions so
            // a newly discovered linked source has no intentional observation
            // gap. If creation fails, the old set remains authoritative and
            // recovery will retry the transition.
            var created = CreateWatchersLocked(desiredRoots);
            var previous = _watchers;
            _watchers = created;
            _watchRoots = desiredRoots.ToList();
            DisposeWatcherList(previous);
            return true;
        }
    }

    private static IReadOnlyList<WatcherRoot> NormalizeWatchRoots(IEnumerable<WatcherRoot> roots)
    {
        var candidates = roots
            .Distinct(WatcherRootComparer.Instance)
            .OrderBy(root => root.CanonicalPath.Length)
            .ThenBy(root => root.CanonicalPath, IncrementalPaths.PathComparer)
            .ThenByDescending(root => root.IncludeSubdirectories)
            .ToArray();
        var normalized = new List<WatcherRoot>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (normalized.Any(existing =>
                existing.IncludeSubdirectories
                && IncrementalPaths.IsUnderDirectory(candidate.CanonicalPath, existing.CanonicalPath)))
            {
                continue;
            }

            normalized.RemoveAll(existing =>
                string.Equals(existing.CanonicalPath, candidate.CanonicalPath, IncrementalPaths.PathComparison));
            normalized.Add(candidate);
        }

        return normalized
            .OrderBy(root => root.CanonicalPath, IncrementalPaths.PathComparer)
            .ThenBy(root => root.IncludeSubdirectories)
            .ToArray();
    }

    private static bool WatchRootsEqual(
        IReadOnlyList<WatcherRoot> first,
        IReadOnlyList<WatcherRoot> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair =>
            string.Equals(pair.First.CanonicalPath, pair.Second.CanonicalPath, IncrementalPaths.PathComparison)
            && pair.First.IncludeSubdirectories == pair.Second.IncludeSubdirectories);

    private sealed class WatcherRootComparer : IEqualityComparer<WatcherRoot>
    {
        public static WatcherRootComparer Instance { get; } = new();

        public bool Equals(WatcherRoot? first, WatcherRoot? second) =>
            first is not null
            && second is not null
            && first.IncludeSubdirectories == second.IncludeSubdirectories
            && string.Equals(first.CanonicalPath, second.CanonicalPath, IncrementalPaths.PathComparison);

        public int GetHashCode(WatcherRoot root) =>
            HashCode.Combine(
                IncrementalPaths.PathComparer.GetHashCode(root.CanonicalPath),
                root.IncludeSubdirectories);
    }

    private void DisposeWatchers()
    {
        lock (_lifecycleGate)
        {
            DisposeWatchersLocked();
        }
    }

    private void DisposeWatchersLocked()
    {
        var watchers = _watchers;
        _watchers = [];
        DisposeWatcherList(watchers);
    }

    private void DisposeWatcherList(IEnumerable<IFileChangeWatcher> watchers)
    {
        Exception? firstFailure = null;
        foreach (var watcher in watchers)
        {
            try
            {
                watcher.PathChanged -= OnWatcherPathChanged;
                watcher.Failed -= OnWatcherFailed;
                watcher.Dispose();
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private Exception? TryDisposeWatchers()
    {
        try
        {
            DisposeWatchers();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? TryDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return null;
        }

        try
        {
            disposable.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private void OnWatcherPathChanged(FileChangeEvent change)
    {
        try
        {
            var snapshot = Volatile.Read(ref _inputSnapshot);
            var classification = snapshot.Classify(change);
            if (!classification.Accepted)
            {
                return;
            }

            if (snapshot.IsBootstrap && classification.RequiresColdReconciliation)
            {
                ReportBootstrapUncertainty(change);
                return;
            }

            _session.ReportFileChanged(change with
            {
                Path = IncrementalPaths.CanonicalAbsolutePath(change.Path),
                OldPath = string.IsNullOrWhiteSpace(change.OldPath)
                    ? null
                    : IncrementalPaths.CanonicalAbsolutePath(change.OldPath),
                RequiresColdReconciliation = change.RequiresColdReconciliation
                    || classification.RequiresColdReconciliation,
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException exception)
        {
            SignalWatcherFailure($"The watcher could not enqueue '{change.Path}': {exception.Message}", reportToSession: false);
        }
    }

    private bool ShouldCaptureWatcherEvent(FileChangeEvent change)
    {
        try
        {
            var snapshot = Volatile.Read(ref _inputSnapshot);
            var classification = snapshot.Classify(change);
            if (!classification.Accepted)
            {
                return false;
            }

            if (snapshot.IsBootstrap && classification.RequiresColdReconciliation)
            {
                ReportBootstrapUncertainty(change);
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            SignalWatcherFailure(
                $"The watcher could not classify '{change.Path}': {exception.Message}",
                reportToSession: true);
            return false;
        }
    }

    private void ReportBootstrapUncertainty(FileChangeEvent change)
    {
        if (Interlocked.Exchange(ref _bootstrapUncertaintyReported, 1) == 0)
        {
            SignalWatcherFailure(
                $"An in-scope file changed before project input membership was established: '{change.Path}'.",
                reportToSession: true);
        }
    }

    private bool OnInputSnapshotChanged(WatcherInputSnapshot snapshot)
    {
        Volatile.Write(ref _inputSnapshot, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));
        // Each load gets a fresh conservative transition window.
        Interlocked.Exchange(ref _bootstrapUncertaintyReported, 0);
        if (snapshot.IsBootstrap)
        {
            // A load transition deliberately reuses the previous logical
            // coverage while Roslyn evaluates the next project. The host's
            // current watcher set is already the authoritative coverage for
            // that interval. In particular, recovery may have replaced an
            // obsolete external root with only the stable base roots; do not
            // resurrect the old evaluated roots from the transition snapshot.
            return false;
        }

        try
        {
            return ReplaceWatchersForSnapshot(snapshot);
        }
        catch (Exception exception)
        {
            SignalWatcherFailure(
                $"The watcher could not establish coverage for evaluated inputs: {exception.Message}",
                reportToSession: true);
            return false;
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
            if (Volatile.Read(ref _disposed) != 0)
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
            catch (InvalidOperationException)
            {
                // A session that has already failed is already untrusted. The
                // host must still continue with watcher recovery below.
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
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_healthy.Task.IsCompletedSuccessfully)
            {
                _healthy = NewHealthSource();
            }
        }
    }

    private void MarkHealthy()
    {
        lock (_recoveryGate)
        {
            if (_recoveryPending || _recoverySignalQueued)
            {
                return;
            }

            lock (_healthGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _healthy.TrySetResult(true);
                }
            }
        }
    }

    private async Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(IncrementalWatcherHost));
            }

            Task healthy;
            lock (_healthGate)
            {
                healthy = _healthy.Task;
            }

            await healthy.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_healthGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(IncrementalWatcherHost));
                }

                if (_healthy.Task.IsCompletedSuccessfully)
                {
                    return;
                }
            }
        }
    }

    private async Task<InventoryScanResult> ScanInventoryAsync(
        bool includeContentHashes,
        CancellationToken cancellationToken)
    {
        WatcherRoot[] roots;
        WatcherInputSnapshot inputSnapshot;
        lock (_lifecycleGate)
        {
            roots = _watchRoots.ToArray();
            inputSnapshot = Volatile.Read(ref _inputSnapshot);
        }

        var inventory = await _inventoryScanner
            .ScanAsync(
                roots
                    .Where(root => root.IncludeSubdirectories)
                    .Select(root => root.CanonicalPath)
                    .ToArray(),
                _request.RepositoryRoot,
                includeContentHashes,
                cancellationToken,
                inputSnapshot)
            .ConfigureAwait(false);
        return new InventoryScanResult(inventory, inputSnapshot, roots);
    }

    private async Task RefreshInventoryBaselineAsync(
        CancellationToken cancellationToken,
        bool reconcileDifferences = false,
        bool allowBootstrap = false,
        bool publishOutput = true)
    {
        while (true)
        {
            var scan = await ScanInventoryAsync(includeContentHashes: false, cancellationToken).ConfigureAwait(false);
            if (!TrySetInventory(scan, out var previous, allowBootstrap))
            {
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            if (!reconcileDifferences)
            {
                return;
            }

            // A newly discovered exact input has no previous inventory
            // fingerprint. It may have changed after Roslyn read it but before
            // this post-load scan, so accepting it as a clean baseline would
            // permanently hide that observation gap. Feed Created entries
            // through the same cold path as changed/deleted entries. The next
            // scan will see the now-established baseline and settle without a
            // further reload.
            var changes = scan.Snapshot
                .CompareToEvents(previous)
                .ToArray();
            if (changes.Length == 0)
            {
                return;
            }

            // A scan that straddles a load is evidence, not a new baseline.
            // Feed the differences through the same serialized session path
            // and only accept a later scan after the graph catches up.
            foreach (var change in changes)
            {
                _session.ReportFileChanged(change);
            }

            if (publishOutput)
            {
                await _session.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _session.RecoverAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool TrySetInventory(
        InventoryScanResult scan,
        out FileInventorySnapshot? previous,
        bool allowBootstrap = false)
    {
        ArgumentNullException.ThrowIfNull(scan);
        previous = null;
        lock (_lifecycleGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref _inputSnapshot), scan.InputSnapshot)
                || !WatchRootsEqual(_watchRoots, scan.WatchRoots))
            {
                return false;
            }

            lock (_inventoryGate)
            {
                // A transition snapshot is intentionally conservative and is
                // published before Roslyn has produced the next evaluated
                // membership. A scan captured in that interval must never be
                // accepted as the baseline after a prior baseline exists: it
                // can otherwise publish a partial/stale inventory and enqueue
                // a burst of false changes. The initial bootstrap scan is the
                // one exception because there is no baseline to overwrite.
                if (!allowBootstrap
                    && scan.InputSnapshot.IsBootstrap
                    && _inventory is not null)
                {
                    return false;
                }

                previous = _inventory;
                _inventory = scan.Snapshot ?? throw new ArgumentNullException(nameof(scan));
            }
        }

        return true;
    }

    private sealed record InventoryScanResult(
        FileInventorySnapshot Snapshot,
        WatcherInputSnapshot InputSnapshot,
        IReadOnlyList<WatcherRoot> WatchRoots);

    private bool WatchRootsExist()
    {
        lock (_lifecycleGate)
        {
            return _watchRoots.All(root => Directory.Exists(root.CanonicalPath));
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(IncrementalWatcherHost));
        }
    }

    private IncrementalRefreshControlServer? TakeControlServerLocked()
    {
        var controlServer = _controlServer;
        _controlServer = null;
        return controlServer;
    }

    private static IReadOnlyList<WatcherRoot> GetWatchRoots(ProjectLoadRequest request)
    {
        var repositoryRoot = IncrementalPaths.CanonicalAbsolutePath(request.RepositoryRoot);
        var inputPath = IncrementalPaths.CanonicalAbsolutePath(request.InputPath);
        var inputRoot = Directory.Exists(inputPath)
            ? inputPath
            : Path.GetDirectoryName(inputPath) ?? repositoryRoot;
        var roots = new List<WatcherRoot> { new(repositoryRoot, IncludeSubdirectories: true) };
        if (!IsUnderDirectory(inputRoot, repositoryRoot))
        {
            roots.Add(new WatcherRoot(inputRoot, IncludeSubdirectories: true));
        }

        return roots
            .GroupBy(root => root.CanonicalPath, IncrementalPaths.PathComparer)
            .Select(group => group.First())
            .OrderBy(root => root.CanonicalPath, IncrementalPaths.PathComparer)
            .ToArray();
    }

    private static bool IsUnderDirectory(string path, string parent)
    {
        return IncrementalPaths.IsUnderDirectory(path, parent);
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
        catch (Exception)
        {
            // Disposal must continue releasing resources when a producer
            // task has already faulted. Its original caller observes the
            // startup/refresh failure; cleanup should not strand the host.
        }
    }

    private static async Task<Exception?> CaptureCleanupFailureAsync(Task? task)
    {
        if (task is null)
        {
            return null;
        }

        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
