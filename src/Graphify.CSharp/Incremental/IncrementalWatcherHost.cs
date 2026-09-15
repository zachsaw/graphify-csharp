using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalWatcherHost : IAsyncDisposable, IWatcherManagementHost
{
    private readonly object _lifecycleGate = new();
    private readonly object _inventoryGate = new();
    private readonly object _recoveryGate = new();
    private readonly object _healthGate = new();
    private readonly object _transitionGate = new();
    private readonly ProjectLoadRequest _request;
    private readonly string? _outputPath;
    private readonly RefreshRequestIdentity _requestIdentity;
    private readonly IFileInventoryScanner _inventoryScanner;
    private readonly IFileChangeWatcherFactory _watcherFactory;
    private readonly IncrementalWatcherOptions _options;
    private readonly WatcherManagementOptions? _managementOptions;
    private readonly WatcherSessionRegistry? _managementRegistry;
    private readonly IReadOnlyList<WatcherRoot> _baseWatchRoots;
    private readonly IncrementalIndexSession _session;
    private readonly IndexingObservation _observation;
    private readonly SemaphoreSlim _recoverySignal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _workStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TransitionEventJournal _transitionEvents = new(TransitionEventJournalCapacity);
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly HashSet<string> _explicitExportPaths = new(IncrementalPaths.PathComparer);
    private TaskCompletionSource<bool> _healthy = NewHealthSource();
    private List<WatcherRoot> _watchRoots;
    private List<IFileChangeWatcher> _watchers = [];
    private FileInventorySnapshot? _inventory;
    private bool _inventoryBaselineWasBootstrap;
    private WatcherInputSnapshot _inputSnapshot;
    private OutputDestinationLease? _outputLease;
    private WatcherLease? _lease;
    private IncrementalRefreshControlServer? _controlServer;
    private SemanticQueryServer? _semanticServer;
    private WatcherManagementServer? _managementServer;
    private WatcherSessionDescriptor? _managementDescriptor;
    private bool _managementRegistered;
    private long _inputSnapshotEpoch;
    private bool _transitionInProgress;
    private Task? _startTask;
    private Task? _backupTask;
    private Task? _recoveryTask;
    private Task? _disposeTask;
    private bool _recoveryPending;
    private bool _recoverySignalQueued;
    private string _recoveryReason = "The watcher requires recovery.";
    private int _transitionJournalOverflowReported;
    private int _disposed;

    private const int TransitionEventJournalCapacity = 4096;

    public IncrementalWatcherHost(
        ProjectLoadRequest request,
        string? outputPath,
        IncrementalWatcherOptions? options = null,
        IProjectLoader? projectLoader = null,
        IncrementalCacheStore? cacheStore = null,
        IncrementalOutputPublisher? outputPublisher = null,
        IFileInventoryScanner? inventoryScanner = null,
        IFileChangeWatcherFactory? watcherFactory = null,
        Func<Guid>? sessionIdFactory = null,
        WatcherManagementOptions? managementOptions = null,
        IndexingObservation? observation = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _outputPath = string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFullPath(outputPath);
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
        _observation = observation ?? new IndexingObservation();
        _baseWatchRoots = GetWatchRoots(request);
        _watchRoots = _baseWatchRoots.ToList();
        _inputSnapshot = WatcherInputSnapshot.CreateBootstrap(
            _watchRoots.Select(root => root.CanonicalPath),
            _outputPath,
            _outputPath is null ? null : IncrementalCachePath.ForOutput(_outputPath),
            _watchRoots,
            knownInputPaths: [request.InputPath]);
        _session = new IncrementalIndexSession(
            request,
            _outputPath,
            projectLoader,
            cacheStore,
            outputPublisher,
            sessionIdFactory,
            OnTrustLost,
            OnInputSnapshotChanged,
            observation: _observation);
    }

    public IncrementalIndexSession Session => _session;

    internal IndexingObservation Observation => _observation;

    public string PipeName => _outputPath is null
        ? throw new InvalidOperationException("A query-only watcher has no legacy refresh endpoint.")
        : IncrementalRefreshControlChannel.ForRequest(_requestIdentity, _outputPath);

    public string LeasePath => _outputPath is null
        ? throw new InvalidOperationException("A query-only watcher has no output lease.")
        : WatcherLease.ForOutput(_outputPath, _requestIdentity);

    public string OutputLeasePath => _outputPath is null
        ? throw new InvalidOperationException("A query-only watcher has no output lease.")
        : OutputDestinationLease.ForOutput(_outputPath);

    internal string SemanticPipeName => _managementOptions is null
        ? throw new InvalidOperationException("Semantic queries require management state to be configured.")
        : SemanticQueryProtocol.ForSession(_session.SessionId, _managementOptions.StateDirectory);

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
                var inputSnapshot = Volatile.Read(ref _inputSnapshot);
                var sessionStatus = _session.Status;
                return Volatile.Read(ref _disposed) == 0
                    && _healthy.Task.IsCompletedSuccessfully
                    && !inputSnapshot.IsBootstrap
                    && sessionStatus is IncrementalSessionStatus.Refreshing or IncrementalSessionStatus.Ready;
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

    WatcherDiagnosticsSnapshot IWatcherManagementHost.GetDiagnosticsSnapshot() => GetDiagnosticsSnapshot();

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

    internal WatcherInspectionSnapshot GetInspectionSnapshot() =>
        GetInspectionSnapshot(_observation.Snapshot());

    private WatcherInspectionSnapshot GetInspectionSnapshot(IndexingObservationSnapshot observation)
    {
        var generation = _session.Generation;
        var ready = IsReady;
        var recoveryPending = IsRecoveryPending();
        var sessionState = _session.Status switch
        {
            IncrementalSessionStatus.Created => "starting",
            var status => status.ToString().ToLowerInvariant(),
        };
        var lifecycleState = _workStopped.Task.IsCompletedSuccessfully
            ? "stopped"
            : _stopRequested.Task.IsCompleted
                ? "stopping"
                : recoveryPending && !ready && sessionState is ("ready" or "refreshing")
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
            _outputPath is null ? null : generation.PublishedGeneration,
            _managementOptions is null ? null : SemanticPipeName,
            _managementOptions is null ? null : SemanticQueryProtocol.CurrentVersion,
            observation);
    }

    internal WatcherDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var observation = _observation.Snapshot();
        return new(
            "graphify-csharp/diagnostics/v1",
            DateTimeOffset.UtcNow,
            GetInspectionSnapshot(observation) with { Observation = null },
            IndexingObservation.CreateRuntimeSnapshot(),
            observation);
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
            SemanticQueryServer? semanticServer;
            WatcherManagementServer? managementServer;
            WatcherSessionDescriptor? managementDescriptor;
            var managementRegistered = false;
            lock (_lifecycleGate)
            {
                start = _startTask;
                controlServer = TakeControlServerLocked();
                semanticServer = _semanticServer;
                _semanticServer = null;
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
            if (semanticServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(semanticServer.DisposeAsync().AsTask()).ConfigureAwait(false));
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
                semanticServer ??= _semanticServer;
                _semanticServer = null;
            }

            if (controlServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false));
            }
            if (semanticServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(semanticServer.DisposeAsync().AsTask()).ConfigureAwait(false));
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
                semanticServer ??= _semanticServer;
                _semanticServer = null;
                lease = _lease;
                _lease = null;
                outputLease = _outputLease;
                _outputLease = null;
            }

            if (controlServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false));
            }
            if (semanticServer is not null)
            {
                RecordCleanupFailure(await CaptureCleanupFailureAsync(semanticServer.DisposeAsync().AsTask()).ConfigureAwait(false));
            }

            RecordCleanupFailure(TryDispose(lease));
            RecordCleanupFailure(await CaptureCleanupFailureAsync(_session.DisposeAsync().AsTask()).ConfigureAwait(false));
            RecordCleanupFailure(await CaptureCleanupFailureAsync(_observation.DisposeAsync().AsTask()).ConfigureAwait(false));
            RecordCleanupFailure(TryDispose(outputLease));
            RecordCleanupFailure(TryDispose(_exportGate));

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
        SemanticQueryServer? semanticServer = null;
        try
        {
            // The host owns startup before the session worker exists. Start
            // observation here so a slow lease, inventory or health barrier is
            // still visible and resource samples begin before the first scan.
            _observation.StartResourceSampling();
            _observation.SetStartupPending(true, "initializing");

            // Output-backed watchers retain both ownership barriers. Query-only
            // watchers deliberately acquire neither: their semantic state is
            // independent and has no canonical publication destination.
            if (_outputPath is not null)
            {
                // Publish the request-specific lease first so a matching client
                // waits for this startup, then acquire the destination-only lease
                // atomically before any cache or output operation can begin.
                lease = WatcherLease.Acquire(_outputPath, _requestIdentity);
                outputLease = OutputDestinationLease.Acquire(_outputPath);
            }
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    lease?.Dispose();
                    outputLease?.Dispose();
                    lease = null;
                    outputLease = null;
                    _observation.CompleteStartup("cancelled", "Watcher was stopped before startup completed.");
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
                semanticServer = new SemanticQueryServer(
                    SemanticPipeName,
                    _session.SessionId,
                    _requestIdentity,
                    ExecuteSemanticQueryAsync,
                    ExportSemanticAsync,
                    ExecuteSemanticRefreshAsync);
                await semanticServer.StartAsync(_stop.Token).ConfigureAwait(false);
                lock (_lifecycleGate)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        _semanticServer = semanticServer;
                        semanticServer = null;
                    }
                }

                await StartManagementAsync(_stop.Token).ConfigureAwait(false);
            }

            CreateAndStartWatchers();
            // Keep the refresh endpoint available throughout startup. A
            // requester that arrives before the first trusted publication must
            // receive a structured not-ready response instead of waiting for a
            // pipe that does not exist or reading a stale output file.
            if (_outputPath is not null)
            {
                await StartControlServerAsync().ConfigureAwait(false);
            }
            _observation.SetStartupPending(true, "inventory");
            await RefreshInventoryWithObservationAsync(
                    _stop.Token,
                    allowBootstrap: true)
                .ConfigureAwait(false);

            await _session.StartAsync(_stop.Token).ConfigureAwait(false);
            _observation.SetStartupPending(true, "final inventory");
            await RefreshInventoryBaselineAsync(
                    _stop.Token,
                    reconcileDifferences: true,
                    publishOutput: _outputPath is not null)
                .ConfigureAwait(false);

            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _backupTask = Task.Run(() => BackupScanLoopAsync(_stop.Token));
                    _recoveryTask = Task.Run(() => RecoveryLoopAsync(_stop.Token));
                }
            }

            _observation.SetStartupPending(true, "health check");
            MarkHealthy();
            // Startup is a foreground readiness barrier too. A watcher error
            // may have queued recovery while the initial Roslyn load was
            // running; do not let StartAsync return until that recovery has
            // either completed or the host has been stopped.
            await WaitUntilHealthyAsync(_stop.Token).ConfigureAwait(false);
            _observation.CompleteStartup("succeeded");
        }
        catch (Exception exception)
        {
            var outcome = exception is OperationCanceledException || _stop.IsCancellationRequested
                ? "cancelled"
                : "failed";
            _observation.CompleteStartup(outcome, exception.Message);
            lock (_healthGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _healthy.TrySetException(exception);
                }
            }

            IncrementalRefreshControlServer? controlServer;
            SemanticQueryServer? activeSemanticServer;
            lock (_lifecycleGate)
            {
                controlServer = TakeControlServerLocked();
                activeSemanticServer = _semanticServer;
                _semanticServer = null;
            }

            if (controlServer is not null)
            {
                await AwaitIgnoringCancellation(controlServer.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            await AwaitIgnoringCancellation(activeSemanticServer?.DisposeAsync().AsTask()).ConfigureAwait(false);

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
            await AwaitIgnoringCancellation(semanticServer?.DisposeAsync().AsTask()).ConfigureAwait(false);
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
                WatcherSessionRegistry.CurrentDescriptorSchemaVersion,
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
                WatcherManagementProtocol.CurrentVersion,
                SemanticPipeName,
                SemanticQueryProtocol.CurrentVersion);

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

    private async Task StartControlServerAsync()
    {
        var outputPath = _outputPath
            ?? throw new InvalidOperationException("A query-only watcher has no legacy refresh endpoint.");
        var controlServer = new IncrementalRefreshControlServer(
            PipeName,
            _requestIdentity,
            outputPath,
            (rebuild, cancellationToken) => RefreshAsync(rebuild, cancellationToken),
            GetInspectionSnapshot);
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
                    if (!TrySetInventory(scan, out var previous, out _))
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

        _observation.RecordEvent("recovery_started", reason);
        DisposeWatchers();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Validate the stable request coverage before reloading, but
                // preserve the previous inventory as the comparison baseline.
                // Replacing it here would erase edits made while the old
                // watcher was being torn down and the new one was starting.
                CreateAndStartWatchers(_baseWatchRoots);
                await RefreshInventoryBaselineAsync(
                        cancellationToken,
                        allowBootstrap: true,
                        preserveExistingBaseline: true)
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
                _observation.RecordRecovery("succeeded", reason);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DisposeWatchers();
                _observation.RecordRecovery("failed", exception.Message);
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

    private async Task<SemanticQueryResponse> ExecuteSemanticQueryAsync(
        SemanticQuerySpec specification,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            var trustVersion = _session.EventTrustVersion;
            Task<SemanticQueryResponse> query;
            lock (_transitionGate)
            {
                query = _session.ExecuteSemanticQueryAsync(specification, cancellationToken);
            }

            var response = await query.ConfigureAwait(false);
            await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            if (_session.IsEventTrustValid(trustVersion))
            {
                return response;
            }

            // The response may have been computed from the old workspace while
            // a watcher delivery loss was queued. Discard it and retry after
            // the host has completed its trusted recovery boundary.
        }
    }

    private async Task<SemanticQueryResponse> ExecuteSemanticRefreshAsync(
        bool rebuild,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            var trustVersion = _session.EventTrustVersion;
            Task<IncrementalRefreshResult> refresh;
            lock (_transitionGate)
            {
                refresh = _session.RefreshSemanticAsync(rebuild, cancellationToken);
            }

            var result = await refresh.ConfigureAwait(false);
            await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            if (_session.IsEventTrustValid(trustVersion))
            {
                return _session.CreateSemanticRefreshResponse(result, rebuild);
            }

            // A watcher delivery loss can be discovered while the refresh is
            // running. Do not report a successful refresh for evidence that
            // crossed that untrusted boundary; the host recovery loop will
            // establish a new trusted generation before retrying.
        }
    }

    private async Task<SemanticQueryResponse> ExportSemanticAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        string canonicalOutputPath;
        try
        {
            canonicalOutputPath = IncrementalPaths.CanonicalAbsolutePath(outputPath);
            _ = OutputDestinationLease.ForOutput(canonicalOutputPath);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return SemanticQueryResponse.Failure(
                _session.SessionId,
                "instance",
                "export",
                "invalid_arguments",
                exception.Message,
                _requestIdentity.CanonicalKey);
        }

        await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        OutputDestinationLease? temporaryLease = null;
        try
        {
            var trustVersion = _session.EventTrustVersion;
            WatcherInputSnapshot snapshot;
            lock (_transitionGate)
            {
                snapshot = Volatile.Read(ref _inputSnapshot);
                var conflict = GetExportConflict(snapshot, canonicalOutputPath);
                if (conflict is not null)
                {
                    return SemanticQueryResponse.Failure(
                        _session.SessionId,
                        "instance",
                        "export",
                        "output_conflict",
                        conflict,
                        _requestIdentity.CanonicalKey);
                }
            }

            var usesCanonicalLease = _outputPath is not null
                && string.Equals(
                    canonicalOutputPath,
                    _outputPath,
                    IncrementalPaths.PathComparison);
            if (!usesCanonicalLease)
            {
                temporaryLease = OutputDestinationLease.Acquire(canonicalOutputPath);
            }

            // Recheck membership after acquiring the destination lease. The
            // project boundary can be replaced while this request is waiting
            // for another export to finish; an evaluated input must always
            // win over the export exclusion.
            lock (_transitionGate)
            {
                snapshot = Volatile.Read(ref _inputSnapshot);
                var conflict = GetExportConflict(snapshot, canonicalOutputPath);
                if (conflict is not null)
                {
                    return SemanticQueryResponse.Failure(
                        _session.SessionId,
                        "instance",
                        "export",
                        "output_conflict",
                        conflict,
                        _requestIdentity.CanonicalKey);
                }

                _explicitExportPaths.Add(canonicalOutputPath);
                Volatile.Write(
                    ref _inputSnapshot,
                    snapshot.WithAdditionalToolPath(canonicalOutputPath));
            }

            var response = await _session
                .ExportAsync(canonicalOutputPath, cancellationToken)
                .ConfigureAwait(false);
            await WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            if (!_session.IsEventTrustValid(trustVersion))
            {
                return SemanticQueryResponse.Failure(
                    _session.SessionId,
                    "instance",
                    "export",
                    "stale_snapshot",
                    "Watcher delivery was lost while exporting; retry the export.",
                    _requestIdentity.CanonicalKey);
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            return SemanticQueryResponse.Failure(
                _session.SessionId,
                "instance",
                "export",
                "output_conflict",
                exception.Message,
                _requestIdentity.CanonicalKey);
        }
        catch (IOException exception)
        {
            return SemanticQueryResponse.Failure(
                _session.SessionId,
                "instance",
                "export",
                "io_error",
                exception.Message,
                _requestIdentity.CanonicalKey);
        }
        finally
        {
            temporaryLease?.Dispose();
            _exportGate.Release();
        }
    }

    private string? GetExportConflict(
        WatcherInputSnapshot snapshot,
        string canonicalOutputPath)
    {
        if (snapshot.IsKnownInput(canonicalOutputPath)
            || snapshot.IsKnownInputOnFileSystem(canonicalOutputPath))
        {
            return "The export destination is an evaluated project input and cannot be overwritten.";
        }

        // The lexical path is sufficient for normal watcher operations, but an
        // explicit export is allowed to name an existing parent through a
        // symlink or junction. Reject a destination whose physical identity
        // cannot be established rather than allowing the atomic publisher to
        // replace an input through an alias.
        if (!IncrementalPaths.TryResolvePhysicalPath(canonicalOutputPath, out var physicalOutputPath))
        {
            return "The export destination could not be resolved safely and cannot be overwritten.";
        }

        if (_managementOptions is not null
            && IncrementalPaths.IsPathOrUnder(
                canonicalOutputPath,
                _managementOptions.StateDirectory))
        {
            return "The export destination is inside the watcher state directory.";
        }

        if (_outputPath is not null
            && IncrementalPaths.IsPathOrUnder(
                canonicalOutputPath,
                GetInternalStateDirectory(_outputPath)))
        {
            return "The export destination is inside the watcher internal state directory.";
        }

        if (_managementOptions is not null)
        {
            if (!TryIsPhysicalPathOrUnder(
                    physicalOutputPath,
                    _managementOptions.StateDirectory,
                    out var isUnderStateDirectory))
            {
                return "The export destination could not be resolved safely and cannot be overwritten.";
            }

            if (isUnderStateDirectory)
            {
                return "The export destination is inside the watcher state directory.";
            }
        }

        if (_outputPath is not null)
        {
            if (!TryIsPhysicalPathOrUnder(
                    physicalOutputPath,
                    GetInternalStateDirectory(_outputPath),
                    out var isUnderInternalStateDirectory))
            {
                return "The export destination could not be resolved safely and cannot be overwritten.";
            }

            if (isUnderInternalStateDirectory)
            {
                return "The export destination is inside the watcher internal state directory.";
            }
        }

        return null;
    }

    private static bool TryIsPhysicalPathOrUnder(
        string path,
        string parent,
        out bool isUnder)
    {
        isUnder = false;
        if (!IncrementalPaths.TryResolvePhysicalPath(parent, out var physicalParent))
        {
            return false;
        }

        isUnder = IncrementalPaths.IsPathOrUnder(path, physicalParent);
        return true;
    }

    private static string GetInternalStateDirectory(string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The output path has no parent directory.");
        return IncrementalPaths.CanonicalAbsolutePath(
            Path.Combine(outputDirectory, ".graphify-csharp"));
    }

    private void OnWatcherPathChanged(FileChangeEvent change)
    {
        var journalOverflowed = false;
        FileChangeEvent? sessionChange = null;
        var capturedAgainstPreviousPolicy = false;
        try
        {
            lock (_transitionGate)
            {
                var snapshot = Volatile.Read(ref _inputSnapshot);
                var currentEpoch = Volatile.Read(ref _inputSnapshotEpoch);
                var capturedByWatcher = change.CaptureEpoch is not null;
                if (change.CaptureEpoch is { } captureEpoch
                    && captureEpoch != currentEpoch)
                {
                    // The dispatch predicate accepted this event under a
                    // previous immutable policy, but the callback reached the
                    // host after that policy was replaced. Keep it in the
                    // transition handoff when one is active; after the
                    // handoff, deliver it as an already-admitted cold event so
                    // the current policy cannot silently discard it.
                    var normalizedStaleChange = NormalizeChange(change, null) with
                    {
                        CaptureEpoch = null,
                    };
                    if (_transitionInProgress || snapshot.IsBootstrap)
                    {
                        journalOverflowed = !_transitionEvents.TryRecord(normalizedStaleChange);
                    }
                    else
                    {
                        sessionChange = normalizedStaleChange with
                        {
                            RequiresColdReconciliation = true,
                        };
                        capturedAgainstPreviousPolicy = true;
                    }
                }
                else
                {
                    var classification = snapshot.Classify(change);
                    if (!classification.Accepted)
                    {
                        return;
                    }

                    var normalizedChange = NormalizeChange(
                        change,
                        snapshot.IsBootstrap ? null : classification) with
                    {
                        CaptureEpoch = null,
                    };
                    if (snapshot.IsBootstrap || _transitionInProgress)
                    {
                        journalOverflowed = !_transitionEvents.TryRecord(normalizedChange);
                    }
                    else
                    {
                        sessionChange = normalizedChange;
                        // The dispatch predicate already admitted this event
                        // under the immutable snapshot identified by its
                        // epoch. Deliver that admission directly to the
                        // session so a transition cannot begin between this
                        // classification and the callback below.
                        capturedAgainstPreviousPolicy = capturedByWatcher;
                    }
                }

                // Deliver an accepted event while the same handoff gate is
                // held. Semantic requests use this gate when they capture
                // their target, so a callback that has crossed the watcher
                // predicate cannot be left in a race window between
                // classification and session enqueueing.
                if (sessionChange is not null)
                {
                    if (capturedAgainstPreviousPolicy)
                    {
                        _session.ReportCapturedFileChanged(sessionChange);
                    }
                    else
                    {
                        _session.ReportFileChanged(sessionChange);
                    }
                }
            }

            if (journalOverflowed)
            {
                ReportTransitionJournalOverflow(change.Path);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException exception)
        {
            SignalWatcherFailure($"The watcher could not enqueue '{change.Path}': {exception.Message}", reportToSession: false);
        }
        catch (ArgumentException exception)
        {
            SignalWatcherFailure($"The watcher received an invalid path near '{change.Path}': {exception.Message}", reportToSession: true);
        }
    }

    private FileChangeEvent? ShouldCaptureWatcherEvent(FileChangeEvent change)
    {
        try
        {
            lock (_transitionGate)
            {
                var snapshot = Volatile.Read(ref _inputSnapshot);
                var classification = snapshot.Classify(change);
                if (!classification.Accepted)
                {
                    return null;
                }

                return NormalizeChange(change, snapshot.IsBootstrap ? null : classification) with
                {
                    CaptureEpoch = Volatile.Read(ref _inputSnapshotEpoch),
                };
            }
        }
        catch (Exception exception)
        {
            SignalWatcherFailure(
                $"The watcher could not classify '{change.Path}': {exception.Message}",
                reportToSession: true);
            return null;
        }
    }

    private void ReportTransitionJournalOverflow(string path)
    {
        // A full transition journal means event delivery is no longer
        // lossless. Keep the recovery behavior reserved for this real loss;
        // ordinary edits during a load are replayed after evaluation.
        if (Interlocked.Exchange(ref _transitionJournalOverflowReported, 1) == 0)
        {
            SignalWatcherFailure(
                $"The file-system transition journal is full; event delivery became untrusted near '{path}'.",
                reportToSession: true);
        }
    }

    private bool OnInputSnapshotChanged(WatcherInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<FileChangeEvent> deferredEvents;
        WatcherInputSnapshot effectiveSnapshot;
        lock (_transitionGate)
        {
            effectiveSnapshot = snapshot;
            foreach (var exportPath in _explicitExportPaths)
            {
                effectiveSnapshot = effectiveSnapshot.WithAdditionalToolPath(exportPath);
            }

            Volatile.Write(ref _inputSnapshot, effectiveSnapshot);
            _inputSnapshotEpoch++;
            _transitionInProgress = true;
            Interlocked.Exchange(ref _transitionJournalOverflowReported, 0);
            if (effectiveSnapshot.IsBootstrap)
            {
                // Do not clear the journal here. Events already observed in
                // this transition must survive until the evaluated policy is
                // published and can classify them authoritatively.
                return false;
            }

            deferredEvents = _transitionEvents.Drain();
        }

        var coverageChanged = false;
        try
        {
            coverageChanged = ReplaceWatchersForSnapshot(effectiveSnapshot);
        }
        catch (Exception exception)
        {
            SignalWatcherFailure(
                $"The watcher could not establish coverage for evaluated inputs: {exception.Message}",
                reportToSession: true);
        }

        lock (_transitionGate)
        {
            deferredEvents = deferredEvents
                .Concat(_transitionEvents.Drain())
                .ToArray();
            _transitionInProgress = false;
        }

        // Replay after watcher coverage has been replaced. OnWatcherPathChanged
        // reclassifies each event against the evaluated policy, so noise from
        // the conservative bootstrap window is discarded without triggering a
        // recovery, while relevant source/project/restore edits retain their
        // normal warm/cold semantics.
        foreach (var deferredEvent in deferredEvents)
        {
            OnWatcherPathChanged(deferredEvent);
        }

        return coverageChanged;
    }

    private static FileChangeEvent NormalizeChange(
        FileChangeEvent change,
        WatcherEventClassification? classification) =>
        change with
        {
            Path = IncrementalPaths.CanonicalAbsolutePath(change.Path),
            OldPath = string.IsNullOrWhiteSpace(change.OldPath)
                ? null
                : IncrementalPaths.CanonicalAbsolutePath(change.OldPath),
            RequiresColdReconciliation = change.RequiresColdReconciliation
                || classification?.RequiresColdReconciliation == true,
        };

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
        _observation.RecordEvent("watcher_failure", reason);
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

                var sessionStatus = _session.Status;
                var inputSnapshot = Volatile.Read(ref _inputSnapshot);
                if (_healthy.Task.IsCompletedSuccessfully
                    && !inputSnapshot.IsBootstrap
                    && sessionStatus is IncrementalSessionStatus.Refreshing or IncrementalSessionStatus.Ready)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsRecoveryPending()
    {
        lock (_recoveryGate)
        {
            return _recoveryPending || _recoverySignalQueued;
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
        bool publishOutput = true,
        bool preserveExistingBaseline = false)
    {
        while (true)
        {
            var scan = await ScanInventoryAsync(includeContentHashes: false, cancellationToken).ConfigureAwait(false);
            if (!TrySetInventory(
                    scan,
                    out var previous,
                    out var previousWasBootstrap,
                    allowBootstrap,
                    preserveExistingBaseline))
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
            // this post-load scan. Source documents are already represented by
            // Roslyn's loaded solution, but a newly discovered dependency can
            // affect that solution before it was observed by the bootstrap
            // inventory. Feed dependency differences through the cold path.
            var changes = scan.Snapshot
                .CompareToEvents(previous)
                .ToArray();
            if (previousWasBootstrap && !scan.InputSnapshot.IsBootstrap)
            {
                changes = changes
                    .Where(change => change.Endpoints.Any(scan.InputSnapshot.IsKnownDependency))
                    .ToArray();
                if (changes.Length == 0)
                {
                    // The bootstrap baseline is intentionally narrow and
                    // contains only request-known inputs. Replace it with the
                    // evaluated baseline, then take one immediate
                    // verification scan to catch edits made during the
                    // handoff.
                    var verification = await ScanInventoryAsync(
                            includeContentHashes: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!TrySetInventory(verification, out previous, out _))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }

                    scan = verification;
                    changes = scan.Snapshot
                        .CompareToEvents(previous)
                        .ToArray();
                }
            }
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

    private async Task RefreshInventoryWithObservationAsync(
        CancellationToken cancellationToken,
        bool allowBootstrap = false,
        bool preserveExistingBaseline = false)
    {
        using var operation = _observation.BeginOperation("inventory");
        operation.SetStage(IndexingStages.Inventory);
        try
        {
            await RefreshInventoryBaselineAsync(
                    cancellationToken,
                    allowBootstrap: allowBootstrap,
                    preserveExistingBaseline: preserveExistingBaseline)
                .ConfigureAwait(false);
            operation.Complete();
        }
        catch (OperationCanceledException)
        {
            operation.Complete("cancelled", "Inventory was cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            operation.Complete("failed", exception.Message);
            throw;
        }
    }

    private bool TrySetInventory(
        InventoryScanResult scan,
        out FileInventorySnapshot? previous,
        out bool previousWasBootstrap,
        bool allowBootstrap = false,
        bool preserveExistingBaseline = false)
    {
        ArgumentNullException.ThrowIfNull(scan);
        previous = null;
        previousWasBootstrap = false;
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
                previousWasBootstrap = _inventoryBaselineWasBootstrap;
                if (!preserveExistingBaseline || _inventory is null)
                {
                    _inventory = scan.Snapshot ?? throw new ArgumentNullException(nameof(scan));
                    _inventoryBaselineWasBootstrap = scan.InputSnapshot.IsBootstrap;
                }
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
