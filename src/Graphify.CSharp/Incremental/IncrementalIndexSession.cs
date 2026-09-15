using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading.Channels;
using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalIndexSession : IAsyncDisposable
{
    private const int EventQueueCapacity = 4096;
    private const int CommandQueueCapacity = 256;
    private readonly object _lifecycleGate = new();
    private readonly object _trustGate = new();
    private readonly ProjectLoadRequest _request;
    private readonly string? _outputPath;
    private readonly string? _cachePath;
    private readonly RefreshRequestIdentity _requestIdentity;
    private readonly IProjectLoader _projectLoader;
    private readonly IncrementalCacheStore _cacheStore;
    private readonly IncrementalOutputPublisher _outputPublisher;
    private readonly Action<string>? _trustLostCallback;
    private readonly Func<WatcherInputSnapshot, bool>? _inputSnapshotChangedCallback;
    private readonly string _semanticMode;
    private readonly byte[] _cursorSecret = RandomNumberGenerator.GetBytes(32);
    private readonly SemanticQueryEngine _semanticQueryEngine;
    private readonly Guid _sessionId;
    private readonly IndexingObservation _observation;
    private readonly bool _ownsObservation;
    // A bounded command queue prevents abandoned, cancelled requests from
    // accumulating without limit while the single worker is rebuilding a
    // solution. Callers receive a deterministic admission failure when the
    // queue is full and can retry after the current work drains.
    private readonly Channel<SessionCommand> _commands = Channel.CreateBounded<SessionCommand>(
        new BoundedChannelOptions(CommandQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    private readonly Channel<FileChangeCommand> _fileEvents = Channel.CreateBounded<FileChangeCommand>(
        new BoundedChannelOptions(EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly SemaphoreSlim _workSignal = new(0);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, DirtyPathState> _dirtyPaths = new(IncrementalPaths.PathComparer);
    private readonly Dictionary<string, SourceFingerprint> _consumedDependencyBaselines =
        new(IncrementalPaths.PathComparer);
    private readonly Dictionary<string, SourceFingerprint> _dependencyLoadStartBaselines =
        new(IncrementalPaths.PathComparer);
    private long _eventClock;
    private long _eventTrustVersion;
    private int _queuedEventCount;
    private int _backgroundIndexRequested;
    private int _eventDeliveryUntrusted;
    private int _publicationPending;
    private int _disposeRequested;
    private int _status = (int)IncrementalSessionStatus.Created;
    private Exception? _failure;
    private Task? _workerTask;
    private Task? _disposeTask;
    private TaskCompletionSource<IncrementalRefreshResult>? _activeRefreshCompletion;
    private LoadedSolution? _loadedSolution;
    private Solution? _currentRoslynSolution;
    private DeclarationCatalog? _catalog;
    private IReadOnlyDictionary<string, ProjectFingerprint> _fingerprints =
        new Dictionary<string, ProjectFingerprint>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, ProjectContributionEnvelope> _contributions =
        new Dictionary<string, ProjectContributionEnvelope>(StringComparer.Ordinal);
    private ImmutableArray<string> _globalDiagnostics = ImmutableArray<string>.Empty;
    private RefreshGeneration _generation;
    private string? _publishedOutputDigest;
    private int _requiresColdReconciliation;
    private WatcherInputSnapshot _inputSnapshot;
    private long _dependencyLoadTargetGeneration;
    private long _evidenceRevision;
    private long _compatibilityGraphBuildCount;
    private SemanticEvidenceIndex? _semanticIndex;
    private long _lastExtractedProjectCount;
    private long _lastReusedProjectCount;

    public IncrementalIndexSession(
        ProjectLoadRequest request,
        string? outputPath,
        IProjectLoader? projectLoader = null,
        IncrementalCacheStore? cacheStore = null,
        IncrementalOutputPublisher? outputPublisher = null,
        Func<Guid>? sessionIdFactory = null,
        Action<string>? trustLostCallback = null,
        Func<WatcherInputSnapshot, bool>? inputSnapshotChangedCallback = null,
        string semanticMode = "instance",
        SemanticQueryEngine? semanticQueryEngine = null,
        IndexingObservation? observation = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _outputPath = string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFullPath(outputPath);
        _cachePath = _outputPath is null ? null : IncrementalCachePath.ForOutput(_outputPath);
        _requestIdentity = new RefreshRequestIdentity(
            request.InputPath,
            request.RepositoryRoot,
            request.Configuration,
            request.TargetFramework);
        _projectLoader = projectLoader ?? new RoslynWorkspaceLoader();
        _cacheStore = cacheStore ?? new IncrementalCacheStore();
        _outputPublisher = outputPublisher ?? new IncrementalOutputPublisher();
        _trustLostCallback = trustLostCallback;
        _inputSnapshotChangedCallback = inputSnapshotChangedCallback;
        if (semanticMode is not ("instance" or "cold"))
        {
            throw new ArgumentException("Semantic mode must be 'instance' or 'cold'.", nameof(semanticMode));
        }

        _semanticMode = semanticMode;
        _semanticQueryEngine = semanticQueryEngine ?? new SemanticQueryEngine();
        _ownsObservation = observation is null;
        _observation = observation ?? new IndexingObservation();
        _sessionId = (sessionIdFactory ?? Guid.NewGuid)();
        _generation = new RefreshGeneration(_sessionId);
        var bootstrapRoots = new HashSet<string>(IncrementalPaths.PathComparer)
        {
            request.RepositoryRoot,
        };
        var inputPath = IncrementalPaths.CanonicalAbsolutePath(request.InputPath);
        var inputRoot = Directory.Exists(inputPath)
            ? inputPath
            : Path.GetDirectoryName(inputPath) ?? IncrementalPaths.CanonicalAbsolutePath(request.RepositoryRoot);
        if (!IncrementalPaths.IsUnderDirectory(inputRoot, request.RepositoryRoot))
        {
            bootstrapRoots.Add(inputRoot);
        }

        _inputSnapshot = WatcherInputSnapshot.CreateBootstrap(
            bootstrapRoots,
            _outputPath,
            _cachePath,
            bootstrapRoots.Select(root => new WatcherRoot(root, IncludeSubdirectories: true)),
            knownInputPaths: [request.InputPath]);
    }

    public IncrementalSessionStatus Status =>
        (IncrementalSessionStatus)Volatile.Read(ref _status);

    public Guid SessionId => _sessionId;

    public long EventGeneration => Volatile.Read(ref _eventClock);

    internal WatcherInputSnapshot InputSnapshot => Volatile.Read(ref _inputSnapshot);

    internal long EventTrustVersion => CaptureEventTrustVersion();

    internal RefreshGeneration Generation => Volatile.Read(ref _generation);

    internal long EvidenceRevision => Volatile.Read(ref _evidenceRevision);

    // This counter is intentionally internal: it makes the distinction
    // between a legacy refresh result and semantic reconciliation observable
    // in deterministic performance tests without adding wire/API surface.
    internal long CompatibilityGraphBuildCount => Volatile.Read(ref _compatibilityGraphBuildCount);

    internal IndexingObservation Observation => _observation;

    internal Task<SemanticQueryResponse> ExecuteSemanticQueryAsync(
        SemanticQuerySpec specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        SemanticQueryJsonParser.ValidateSpec(specification);
        EnsureWorkerStarted();
        var completion = new TaskCompletionSource<SemanticQueryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate)
        {
            ThrowIfSessionUnavailableLocked();
            var target = CaptureCurrentTargetLocked();
            if (!_commands.Writer.TryWrite(new SemanticQueryCommand(
                    target,
                    specification,
                    cancellationToken,
                    completion)))
            {
                completion.TrySetException(new InvalidOperationException("The incremental session is not accepting semantic queries."));
            }
            else
            {
                _workSignal.Release();
            }
        }

        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    internal Task<SemanticQueryResponse> ExportAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        EnsureWorkerStarted();
        var completion = new TaskCompletionSource<SemanticQueryResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate)
        {
            ThrowIfSessionUnavailableLocked();
            var target = CaptureCurrentTargetLocked();
            if (!_commands.Writer.TryWrite(new SemanticExportCommand(
                    target,
                    IncrementalPaths.CanonicalAbsolutePath(outputPath),
                    cancellationToken,
                    completion)))
            {
                completion.TrySetException(new InvalidOperationException(
                    "The incremental session is not accepting semantic exports."));
            }
            else
            {
                _workSignal.Release();
            }
        }

        // An export owns its destination until the worker has finished the
        // actual commit. Letting the caller's cancellation token detach from
        // this task would let the host release that ownership while the
        // worker was still writing the file. The request token is still linked
        // into the worker command, so cancellation stops work at the next
        // cooperative boundary; this task remains the join point for the
        // commit and its cleanup.
        return completion.Task;
    }

    internal bool IsEventTrustValid(long version)
    {
        lock (_trustGate)
        {
            return _eventTrustVersion == version && _eventDeliveryUntrusted == 0;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureWorkerStarted();
        return cancellationToken.CanBeCanceled
            ? _ready.Task.WaitAsync(cancellationToken)
            : _ready.Task;
    }

    public Task<IncrementalRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(rebuild: false, operationKind: "refresh", cancellationToken: cancellationToken);

    public Task<IncrementalRefreshResult> RebuildAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(rebuild: true, operationKind: "rebuild", cancellationToken: cancellationToken);

    internal Task<IncrementalRefreshResult> RecoverAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(
            rebuild: true,
            operationKind: "recovery",
            cancellationToken: cancellationToken,
            publishOutput: false,
            includeGraph: false);

    private Task<IncrementalRefreshResult> RefreshCoreAsync(
        bool rebuild,
        string operationKind,
        CancellationToken cancellationToken,
        bool publishOutput = true,
        bool includeGraph = true)
    {
        EnsureWorkerStarted();
        var completion = new TaskCompletionSource<IncrementalRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate)
        {
            ThrowIfSessionUnavailableLocked();
            // Event producers hold this gate from advancing the event clock
            // through enqueueing the corresponding command. Capture the
            // refresh target under the same gate so a target can never include
            // an event whose queue entry is still being published.
            var target = CaptureCurrentTargetLocked();
            if (!_commands.Writer.TryWrite(new RefreshCommand(target, rebuild, publishOutput, operationKind, completion)))
            {
                completion.TrySetException(new InvalidOperationException("The incremental session is not accepting refresh requests."));
            }
            else
            {
                _workSignal.Release();
            }
        }

        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public void ReportFileChanged(string path) =>
        ReportFileChanged(new FileChangeEvent(FileChangeKind.Changed, path));

    public void ReportFileChanged(FileChangeEvent change)
        => ReportFileChangedCore(change, alreadyClassified: false);

    internal void ReportCapturedFileChanged(FileChangeEvent change)
        => ReportFileChangedCore(change, alreadyClassified: true);

    private void ReportFileChangedCore(FileChangeEvent change, bool alreadyClassified)
    {
        ArgumentNullException.ThrowIfNull(change);
        var classification = new WatcherEventClassification(
            Accepted: alreadyClassified,
            RequiresColdReconciliation: alreadyClassified && change.RequiresColdReconciliation);
        if (!alreadyClassified)
        {
            try
            {
                classification = Volatile.Read(ref _inputSnapshot).Classify(change);
            }
            catch (ArgumentException)
            {
                Volatile.Write(ref _requiresColdReconciliation, 1);
                MarkEventDeliveryUntrusted("An invalid file-system event path was received.");
                return;
            }
        }

        if (!classification.Accepted)
        {
            return;
        }

        EnsureWorkerStarted();
        bool accepted;
        lock (_lifecycleGate)
        {
            ThrowIfSessionUnavailableLocked();
            var generation = Interlocked.Increment(ref _eventClock);
            var queued = Interlocked.Increment(ref _queuedEventCount);
            var normalizedChange = change with
            {
                Path = IncrementalPaths.CanonicalAbsolutePath(change.Path),
                OldPath = string.IsNullOrWhiteSpace(change.OldPath)
                    ? null
                    : IncrementalPaths.CanonicalAbsolutePath(change.OldPath),
                RequiresColdReconciliation = change.RequiresColdReconciliation
                    || classification.RequiresColdReconciliation,
                CaptureEpoch = null,
            };
            accepted = !string.IsNullOrWhiteSpace(normalizedChange.Path)
                && queued <= EventQueueCapacity
                && _fileEvents.Writer.TryWrite(new FileChangeCommand(normalizedChange, generation));
            if (!accepted)
            {
                Interlocked.Decrement(ref _queuedEventCount);
            }
            else if (!IsEventDeliveryUntrusted())
            {
                // Publish the background request before waking the worker.
                // Reversing these operations allows the worker to consume the
                // event signal, observe no request, and then sleep forever.
                Interlocked.Exchange(ref _backgroundIndexRequested, 1);
            }

            if (accepted)
            {
                _workSignal.Release();
            }
        }

        if (!accepted)
        {
            MarkEventDeliveryUntrusted("The file-system event queue is full or received an invalid path.");
        }
    }

    public void RequestBackgroundIndex()
    {
        EnsureWorkerStarted();
        lock (_lifecycleGate)
        {
            ThrowIfSessionUnavailableLocked();
            if (IsEventDeliveryUntrusted()
                || Volatile.Read(ref _requiresColdReconciliation) != 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref _backgroundIndexRequested, 1) == 0)
            {
                _workSignal.Release();
            }
        }
    }

    public void ReportWatcherFailure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        MarkEventDeliveryUntrusted(reason);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                return;
            }

            if (Status == IncrementalSessionStatus.Failed)
            {
                return;
            }

            if (_commands.Writer.TryWrite(new WatcherInvalidatedCommand(reason)))
            {
                _workSignal.Release();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposeRequested, 1);
                _disposeTask = DisposeCoreAsync();
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        Task? worker;
        lock (_lifecycleGate)
        {
            worker = _workerTask;
            Volatile.Write(ref _status, (int)IncrementalSessionStatus.Stopping);
            _stop.Cancel();
            _commands.Writer.TryComplete();
            _fileEvents.Writer.TryComplete();
            CancelPendingCommandsLocked();
            if (worker is null)
            {
                Volatile.Write(ref _status, (int)IncrementalSessionStatus.Stopped);
            }
            else
            {
                _workSignal.Release();
            }
        }

        if (worker is null)
        {
            _stop.Dispose();
            _workSignal.Dispose();
            if (_ownsObservation)
            {
                await _observation.DisposeAsync().ConfigureAwait(false);
            }
            return;
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _status, (int)IncrementalSessionStatus.Stopped);
            _stop.Dispose();
            _workSignal.Dispose();
            if (_ownsObservation)
            {
                await _observation.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void EnsureWorkerStarted()
    {
        lock (_lifecycleGate)
        {
            EnsureWorkerStartedLocked();
        }
    }

    private void EnsureWorkerStartedLocked()
    {
        ThrowIfDisposeRequestedLocked();
        if (Status == IncrementalSessionStatus.Failed)
        {
            throw new InvalidOperationException(
                "The incremental session failed during startup.",
                Volatile.Read(ref _failure));
        }

        if (_workerTask is not null)
        {
            return;
        }

        Volatile.Write(ref _status, (int)IncrementalSessionStatus.Starting);
        _workerTask = Task.Run(RunAsync);
    }

    private void ThrowIfDisposeRequestedLocked()
    {
        if (Volatile.Read(ref _disposeRequested) != 0
            || Status is IncrementalSessionStatus.Stopping or IncrementalSessionStatus.Stopped)
        {
            throw new ObjectDisposedException(nameof(IncrementalIndexSession));
        }
    }

    private void ThrowIfSessionUnavailableLocked()
    {
        ThrowIfDisposeRequestedLocked();
        if (Status == IncrementalSessionStatus.Failed)
        {
            throw new InvalidOperationException(
                "The incremental session failed during startup.",
                Volatile.Read(ref _failure));
        }
    }

    private async Task RunAsync()
    {
        IndexingObservationOperation? startupOperation = null;
        try
        {
            _observation.StartResourceSampling();
            startupOperation = _observation.BeginOperation("startup");
            startupOperation.SetStage(IndexingStages.Initializing);
            await InitializeAsync(_stop.Token, startupOperation).ConfigureAwait(false);
            startupOperation.Complete();
            startupOperation = null;
            TrySetStatusIfActive(IncrementalSessionStatus.Ready);
            _ready.TrySetResult(true);

            while (!_stop.IsCancellationRequested)
            {
                await _workSignal.WaitAsync(_stop.Token).ConfigureAwait(false);
                while (_commands.Reader.TryRead(out var command))
                {
                    await HandleCommandAsync(command, _stop.Token).ConfigureAwait(false);
                }

                while (_fileEvents.Reader.TryRead(out var fileEvent))
                {
                    Interlocked.Decrement(ref _queuedEventCount);
                    HandleFileChange(fileEvent);
                }

                // Foreground commands win over background work that was
                // requested before the worker reached this point.
                while (_commands.Reader.TryRead(out var foregroundCommand))
                {
                    await HandleCommandAsync(foregroundCommand, _stop.Token).ConfigureAwait(false);
                }

                if (Interlocked.Exchange(ref _backgroundIndexRequested, 0) != 0)
                {
                    TrySetStatusIfActive(IncrementalSessionStatus.Refreshing);
                    try
                    {
                        await IndexBackgroundAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Background work is opportunistic. A transient write,
                        // project change, or Roslyn failure must invalidate the
                        // warm boundary and allow the host (when present) to
                        // perform a trusted cold recovery. A standalone session
                        // will take the same cold path on its next foreground
                        // refresh.
                        Volatile.Write(ref _requiresColdReconciliation, 1);
                        MarkEventDeliveryUntrusted(
                            $"Background indexing failed and requires cold recovery: {exception.Message}");
                    }
                    finally
                    {
                        TrySetStatusIfActive(IncrementalSessionStatus.Ready);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            startupOperation?.Complete("cancelled", "Startup was cancelled.");
            lock (_lifecycleGate)
            {
                CancelPendingCommandsLocked();
            }
        }
        catch (Exception exception)
        {
            startupOperation?.Complete("failed", exception.Message);
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposeRequested) != 0)
                {
                    CancelPendingCommandsLocked();
                }
                else
                {
                    Volatile.Write(ref _failure, exception);
                    Volatile.Write(ref _status, (int)IncrementalSessionStatus.Failed);
                    _commands.Writer.TryComplete(exception);
                    _fileEvents.Writer.TryComplete(exception);
                    _ready.TrySetException(exception);
                    FailPendingCommands(exception);
                }
            }
        }
        finally
        {
            _loadedSolution?.Dispose();
            _loadedSolution = null;
        }
    }

    private async Task InitializeAsync(
        CancellationToken cancellationToken,
        IndexingObservationOperation? operation = null)
    {
        await LoadAndExtractAllAsync(cancellationToken, operation).ConfigureAwait(false);
        // Establish the boundary before draining callbacks. An event accepted
        // after this point must remain newer than the startup snapshot even if
        // its callback is already waiting in the queue.
        var target = CaptureCurrentTarget();
        DrainFileEvents();
        CaptureConsumedDependencyBaselines();
        DiscardAlreadyConsumedDependencyChanges();
        var dueStates = DueDirtyPathStates(target.EventGeneration);
        var duePaths = dueStates.Select(state => state.Path).ToArray();
        if (IsEventDeliveryUntrusted() || duePaths.Length > 0)
        {
            await ReconcileAsync(
                    target,
                    forceCold: IsEventDeliveryUntrusted(),
                    publishOutput: _outputPath is not null,
                    includeGraph: true,
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _generation = _generation.AdvanceEventsThrough(
            Math.Max(_generation.EventGeneration, target.EventGeneration));
        _generation = MarkGenerationIndexed(target.EventGeneration);
        PublishEvidenceObservation();
        if (_outputPath is null)
        {
            return;
        }

        operation?.SetStage(IndexingStages.Serializing);
        await PublishCurrentAsync(
            target,
            extractedProjectCount: _contributions.Count,
            reusedProjectCount: 0,
            outputRepublished: true,
            cancellationToken: cancellationToken,
            operation: operation).ConfigureAwait(false);
    }

    private async Task HandleCommandAsync(SessionCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case RefreshCommand refresh:
                if (!TryBeginRefresh(refresh))
                {
                    break;
                }

                IndexingObservationOperation? operation = null;
                try
                {
                    operation = _observation.BeginOperation(refresh.OperationKind);
                    operation.SetStage(IndexingStages.ReconcilingChanges);
                    TrySetStatusIfActive(IncrementalSessionStatus.Refreshing);
                    var result = await ReconcileAsync(
                            refresh.Target,
                            forceCold: refresh.Rebuild,
                            publishOutput: refresh.PublishOutput,
                            includeGraph: true,
                            operation,
                            cancellationToken)
                        .ConfigureAwait(false);
                    operation.Complete();
                    TrySetStatusIfActive(IncrementalSessionStatus.Ready);
                    // A completed refresh is the foreground readiness barrier.
                    // Publish the state before completing the task so callers
                    // cannot observe a completed refresh while the session
                    // still reports itself as Refreshing.
                    refresh.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    operation?.Complete("cancelled", "The refresh was cancelled.");
                    refresh.Completion.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    operation?.Complete("failed", exception.Message);
                    TrySetStatusIfActive(IncrementalSessionStatus.Ready);
                    refresh.Completion.TrySetException(exception);
                }
                finally
                {
                    operation?.Dispose();
                    EndRefresh(refresh.Completion);
                }

                break;
            case WatcherInvalidatedCommand:
                Volatile.Write(ref _requiresColdReconciliation, 1);
                break;
            case SemanticQueryCommand semantic:
                await HandleSemanticQueryAsync(semantic, cancellationToken).ConfigureAwait(false);
                break;
            case SemanticExportCommand export:
                await HandleSemanticExportAsync(export, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleSemanticQueryAsync(
        SemanticQueryCommand command,
        CancellationToken sessionCancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            sessionCancellationToken,
            command.RequestCancellationToken);
        IndexingObservationOperation? operation = null;
        try
        {
            operation = _observation.BeginOperation("query_index");
            operation.SetStage(IndexingStages.ReconcilingChanges);
            await ReconcileAsync(
                    command.Target,
                    forceCold: false,
                    publishOutput: false,
                    includeGraph: false,
                    operation,
                    requestCancellation.Token)
                .ConfigureAwait(false);
            requestCancellation.Token.ThrowIfCancellationRequested();
            var view = CreateSemanticEvidenceView(
                command.Target,
                requestCancellation.Token,
                operation);
            var mediator = new WatcherManagementMediator(new WatcherManagementServiceProvider());
            var response = await mediator
                .Send(
                    new ExecuteSemanticQueryRequest(command.Specification, view, _semanticQueryEngine),
                    requestCancellation.Token)
                .ConfigureAwait(false);
            command.Completion.TrySetResult(response);
            operation.Complete();
        }
        catch (OperationCanceledException) when (
            command.RequestCancellationToken.IsCancellationRequested
            && !sessionCancellationToken.IsCancellationRequested)
        {
            operation?.Complete("cancelled", "The semantic query was cancelled.");
            command.Completion.TrySetCanceled(command.RequestCancellationToken);
        }
        catch (OperationCanceledException) when (sessionCancellationToken.IsCancellationRequested)
        {
            // The command has already left the queue, so the worker's
            // pending-command drain cannot complete it during shutdown.
            // Complete active semantic work explicitly before the worker exits.
            operation?.Complete("cancelled", "The semantic query was cancelled during shutdown.");
            command.Completion.TrySetCanceled(sessionCancellationToken);
        }
        catch (SemanticQueryException exception)
        {
            operation?.Complete("failed", exception.Message);
            command.Completion.TrySetResult(SemanticQueryResponse.Failure(
                _semanticMode == "cold" ? null : _sessionId,
                _semanticMode,
                command.Specification.Command,
                exception.Code,
                exception.Message,
                _requestIdentity.CanonicalKey));
        }
        catch (NotSupportedException exception)
        {
            operation?.Complete("failed", exception.Message);
            command.Completion.TrySetResult(SemanticQueryResponse.Failure(
                _semanticMode == "cold" ? null : _sessionId,
                _semanticMode,
                command.Specification.Command,
                "unsupported_capability",
                exception.Message,
                _requestIdentity.CanonicalKey));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            operation?.Complete("failed", exception.Message);
            command.Completion.TrySetResult(SemanticQueryResponse.Failure(
                _semanticMode == "cold" ? null : _sessionId,
                _semanticMode,
                command.Specification.Command,
                "internal_error",
                exception.Message,
                _requestIdentity.CanonicalKey));
        }
        finally
        {
            operation?.Dispose();
        }
    }

    private async Task HandleSemanticExportAsync(
        SemanticExportCommand command,
        CancellationToken sessionCancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            sessionCancellationToken,
            command.RequestCancellationToken);
        IndexingObservationOperation? operation = null;
        try
        {
            operation = _observation.BeginOperation("export");
            operation.SetStage(IndexingStages.ReconcilingChanges);
            await ReconcileAsync(
                    command.Target,
                    forceCold: false,
                    publishOutput: false,
                    includeGraph: false,
                    operation,
                    requestCancellation.Token)
                .ConfigureAwait(false);
            requestCancellation.Token.ThrowIfCancellationRequested();
            var view = CreateSemanticEvidenceView(
                command.Target,
                requestCancellation.Token,
                operation);
            operation.SetStage(IndexingStages.Serializing);
            if (view.InputSnapshot.IsKnownInput(command.OutputPath)
                || view.InputSnapshot.IsKnownInputOnFileSystem(command.OutputPath)
                || (_outputPath is not null
                    && IsPhysicalPathOrUnder(
                        command.OutputPath,
                        GetInternalStateDirectory(_outputPath))))
            {
                throw new SemanticQueryException(
                    "output_conflict",
                    "The export destination is an evaluated project input or watcher internal state path and cannot be overwritten.");
            }

            var graph = MergeContributions(_contributions.Values);
            var diagnostics = view.Diagnostics.ToArray();
            var outputDigest = await _outputPublisher
                .PublishAsync(command.OutputPath, graph, diagnostics, requestCancellation.Token)
                .ConfigureAwait(false);
            var responseDiagnostics = SemanticQueryEngine.DisplayDiagnostics(
                diagnostics,
                out var diagnosticsTruncated);
            var response = SemanticQueryResponse.SuccessResponse(
                view.SessionId == Guid.Empty ? null : view.SessionId,
                view.Mode,
                "export",
                new SemanticQuerySnapshot(
                    view.SnapshotId,
                    view.TargetEventGeneration,
                    view.Generation.IndexedGeneration,
                    view.Generation.EventGeneration),
                new SemanticQueryScope(
                    view.AnalysisKey,
                    new SemanticQueryFilters(),
                    "observed_static",
                    view.InputSnapshot.InputDiscoveryComplete,
                    diagnostics.Length > 0,
                    diagnostics.Length == 0,
                    Array.Empty<string>()),
                Array.Empty<System.Text.Json.JsonElement>(),
                new SemanticQueryPage(0, false, null),
                responseDiagnostics,
                diagnosticsTruncated,
                new SemanticExportResult(
                    command.OutputPath,
                    outputDigest,
                    graph.Nodes.Length,
                    graph.Edges.Length));
            command.Completion.TrySetResult(response);
            operation.Complete();
        }
        catch (OperationCanceledException) when (
            command.RequestCancellationToken.IsCancellationRequested
            && !sessionCancellationToken.IsCancellationRequested)
        {
            operation?.Complete("cancelled", "The semantic export was cancelled.");
            command.Completion.TrySetCanceled(command.RequestCancellationToken);
        }
        catch (OperationCanceledException) when (sessionCancellationToken.IsCancellationRequested)
        {
            operation?.Complete("cancelled", "The semantic export was cancelled during shutdown.");
            command.Completion.TrySetCanceled(sessionCancellationToken);
        }
        catch (SemanticQueryException exception)
        {
            operation?.Complete("failed", exception.Message);
            command.Completion.TrySetResult(SemanticQueryResponse.Failure(
                _semanticMode == "cold" ? null : _sessionId,
                _semanticMode,
                "export",
                exception.Code,
                exception.Message,
                _requestIdentity.CanonicalKey));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            operation?.Complete("failed", exception.Message);
            command.Completion.TrySetResult(SemanticQueryResponse.Failure(
                _semanticMode == "cold" ? null : _sessionId,
                _semanticMode,
                "export",
                "io_error",
                exception.Message,
                _requestIdentity.CanonicalKey));
        }
        finally
        {
            operation?.Dispose();
        }
    }

    private async Task<IncrementalRefreshResult> ReconcileAsync(
        RefreshTarget target,
        bool forceCold,
        bool publishOutput,
        bool includeGraph,
        IndexingObservationOperation? operation,
        CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        operation?.SetStage(IndexingStages.ReconcilingChanges);
        DrainFileEvents();
        DiscardAlreadyConsumedDependencyChanges();
        forceCold |= IsEventDeliveryUntrusted()
            || Volatile.Read(ref _requiresColdReconciliation) != 0
            || !Volatile.Read(ref _inputSnapshot).InputDiscoveryComplete;
        var dueStates = DueDirtyPathStates(target.EventGeneration);
        forceCold |= dueStates.Any(state => state.RequiresColdReconciliation);
        var duePaths = dueStates.Select(state => state.Path).ToArray();
        var dirtyProjectKeys = forceCold
            ? _fingerprints.Keys.ToHashSet(StringComparer.Ordinal)
            : ResolveDirtyProjects(duePaths);
        forceCold |= Volatile.Read(ref _requiresColdReconciliation) != 0;
        if (forceCold)
        {
            dirtyProjectKeys = _fingerprints.Keys.ToHashSet(StringComparer.Ordinal);
        }
        var fullRebuild = forceCold;
        var publicationPending = _outputPath is not null
            && Volatile.Read(ref _publicationPending) != 0;
        var outputChanged = dirtyProjectKeys.Count > 0
            || fullRebuild
            || publicationPending;
        if (!outputChanged)
        {
            _generation = _generation.AdvanceEventsThrough(
                Math.Max(_generation.EventGeneration, target.EventGeneration));
            _generation = MarkGenerationIndexed(target.EventGeneration);
            PublishEvidenceObservation();
            if (!publishOutput)
            {
                return CreateUnpublishedResult(
                    extractedProjectCount: 0,
                    reusedProjectCount: _contributions.Count,
                    includeGraph);
            }

            var result = await PublishCurrentAsync(
                    target,
                    extractedProjectCount: 0,
                    reusedProjectCount: _contributions.Count,
                    outputRepublished: _generation.PublishedGeneration < target.EventGeneration,
                    cancellationToken: cancellationToken,
                    operation: operation)
                .ConfigureAwait(false);
            ClearDirtyPaths(duePaths, target.EventGeneration);
            return result;
        }

        if (!fullRebuild && dirtyProjectKeys.Count == 0 && publicationPending)
        {
            // Background indexing has already installed the current evidence;
            // only the output-backed publication is still pending. Do not run
            // a second catalog/compilation pass merely because a query or a
            // foreground refresh reached this boundary.
            _generation = _generation.AdvanceEventsThrough(
                Math.Max(_generation.EventGeneration, target.EventGeneration));
            _generation = MarkGenerationIndexed(target.EventGeneration);
            PublishEvidenceObservation();
            if (!publishOutput)
            {
                return CreateUnpublishedResult(
                    extractedProjectCount: 0,
                    reusedProjectCount: _contributions.Count,
                    includeGraph);
            }

            var pendingPublication = await PublishCurrentAsync(
                    target,
                    extractedProjectCount: 0,
                    reusedProjectCount: _contributions.Count,
                    outputRepublished: true,
                    cancellationToken: cancellationToken,
                    operation: operation)
                .ConfigureAwait(false);
            ClearDirtyPaths(duePaths, target.EventGeneration);
            return pendingPublication;
        }

        var extractedProjectCount = 0;
        var reusedProjectCount = 0;
        if (fullRebuild)
        {
            var trustVersionAtStart = CaptureEventTrustVersion();
            await LoadAndExtractAllAsync(cancellationToken, operation).ConfigureAwait(false);
            DrainFileEvents();
            CaptureConsumedDependencyBaselines();
            extractedProjectCount = _contributions.Count;
            Volatile.Write(
                ref _requiresColdReconciliation,
                Volatile.Read(ref _inputSnapshot).InputDiscoveryComplete ? 0 : 1);
            if (!TryAcknowledgeEventTrust(trustVersionAtStart))
            {
                Volatile.Write(ref _requiresColdReconciliation, 1);
            }
        }
        else
        {
            var updated = await ApplySourceChangesAsync(duePaths, operation, cancellationToken).ConfigureAwait(false);
            if (updated is null)
            {
                await LoadAndExtractAllAsync(cancellationToken, operation).ConfigureAwait(false);
                DrainFileEvents();
                CaptureConsumedDependencyBaselines();
                extractedProjectCount = _contributions.Count;
                Volatile.Write(
                    ref _requiresColdReconciliation,
                    IsEventDeliveryUntrusted()
                    || !Volatile.Read(ref _inputSnapshot).InputDiscoveryComplete
                        ? 1
                        : 0);
            }
            else
            {
                _currentRoslynSolution = updated;
                await RefreshAnalyzedProjectsAsync(updated, operation, cancellationToken).ConfigureAwait(false);
                operation?.SetStage(IndexingStages.Fingerprinting);
                var newFingerprints = new IncrementalProjectFingerprintBuilder().BuildAll(_loadedSolution!);
                dirtyProjectKeys = ExpandReverseDependencies(dirtyProjectKeys, newFingerprints);
                using var catalogPhase = operation?.BeginPhase(IndexingStages.Cataloging);
                var catalog = await new DeclarationCatalogBuilder()
                    .BuildAsync(
                        _loadedSolution!,
                        progress: progress => ReportCatalogProgress(catalogPhase, progress),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                using var extractionPhase = operation?.BeginPhase(IndexingStages.ExtractingRelationships);
                var extractor = new SemanticReferenceExtractor();
                var extracted = await extractor
                    .ExtractContributionsAsync(
                        _loadedSolution!,
                        catalog,
                        dirtyProjectKeys,
                        progress: progress => ReportExtractionProgress(extractionPhase, progress),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var extractedByProject = extracted.ToDictionary(
                    contribution => contribution.Project.Key,
                    StringComparer.Ordinal);
                var merged = new Dictionary<string, ProjectContributionEnvelope>(StringComparer.Ordinal);
                foreach (var project in _loadedSolution!.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (dirtyProjectKeys.Contains(project.Identity.Key))
                    {
                        if (!extractedByProject.TryGetValue(project.Identity.Key, out var projectContribution))
                        {
                            throw new InvalidDataException($"No contribution was extracted for project '{project.Identity.Key}'.");
                        }

                        merged.Add(
                            project.Identity.Key,
                            new ProjectContributionEnvelope(
                                newFingerprints[project.Identity.Key],
                                projectContribution.Graph,
                                projectContribution.Diagnostics));
                        extractedProjectCount++;
                    }
                    else if (_contributions.TryGetValue(project.Identity.Key, out var cachedContribution))
                    {
                        merged.Add(project.Identity.Key, cachedContribution);
                        reusedProjectCount++;
                    }
                    else
                    {
                        throw new InvalidDataException($"Project '{project.Identity.Key}' has no warm contribution.");
                    }
                }

                _catalog = catalog;
                _fingerprints = newFingerprints;
                _contributions = merged;
                _lastExtractedProjectCount = extractedProjectCount;
                _lastReusedProjectCount = reusedProjectCount;
                _globalDiagnostics = _loadedSolution.Diagnostics
                    .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
                    .Concat(_inputSnapshot.InputDiscoveryDiagnostics)
                    .Concat(catalog.Diagnostics)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
                    .ToImmutableArray();
                MarkEvidenceReplaced();
            }
        }

        _generation = _generation.AdvanceEventsThrough(
            Math.Max(_generation.EventGeneration, target.EventGeneration));
        _generation = MarkGenerationIndexed(target.EventGeneration);
        PublishEvidenceObservation();
        if (!publishOutput)
        {
            // Recovery and opportunistic background indexing may update the
            // in-memory Roslyn state without touching the public JSON. Keep
            // an explicit publication obligation even when the triggering
            // event was observed only during a transition and therefore did
            // not receive its own event generation.
            if (_outputPath is not null)
            {
                Interlocked.Exchange(ref _publicationPending, 1);
            }
            ClearDirtyPaths(duePaths, target.EventGeneration);
            return CreateUnpublishedResult(extractedProjectCount, reusedProjectCount, includeGraph);
        }

        var published = await PublishCurrentAsync(
            target,
            extractedProjectCount,
            reusedProjectCount,
            outputRepublished: true,
            cancellationToken: cancellationToken,
            operation: operation).ConfigureAwait(false);
        ClearDirtyPaths(duePaths, target.EventGeneration);
        return published;
    }

    private async Task IndexBackgroundAsync(CancellationToken cancellationToken)
    {
        if (IsEventDeliveryUntrusted())
        {
            return;
        }

        DrainFileEvents();
        var target = CaptureCurrentTarget();
        var operation = _observation.BeginOperation("background_refresh");
        try
        {
            operation.SetStage(IndexingStages.ReconcilingChanges);
            await ReconcileAsync(
                    target,
                    forceCold: false,
                    publishOutput: false,
                    includeGraph: false,
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
            operation.Complete();
        }
        catch (OperationCanceledException)
        {
            operation.Complete("cancelled", "The background refresh was cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            operation.Complete("failed", exception.Message);
            throw;
        }
    }

    private IncrementalRefreshResult CreateUnpublishedResult(
        int extractedProjectCount,
        int reusedProjectCount,
        bool includeGraph)
    {
        var graph = GraphSnapshot.Create(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>());
        if (includeGraph && _outputPath is not null)
        {
            Interlocked.Increment(ref _compatibilityGraphBuildCount);
            graph = MergeContributions(_contributions.Values);
        }

        return new IncrementalRefreshResult(
            // Semantic/background callers consume the in-memory evidence
            // directly and never use this compatibility result. Avoid merging
            // all contributions merely to manufacture an indexing result.
            graph,
            _publishedOutputDigest,
            IncrementalCacheLoadStatus.Missing,
            extractedProjectCount,
            reusedProjectCount,
            outputRepublished: false,
            _generation);
    }

    private async Task<IncrementalRefreshResult> PublishCurrentAsync(
        RefreshTarget target,
        int extractedProjectCount,
        int reusedProjectCount,
        bool outputRepublished,
        CancellationToken cancellationToken,
        IndexingObservationOperation? operation = null)
    {
        var outputPath = _outputPath
            ?? throw new InvalidOperationException("This query-only session does not publish a canonical output.");
        var cachePath = _cachePath
            ?? throw new InvalidOperationException("This query-only session does not persist an output cache.");
        var graph = MergeContributions(_contributions.Values);
        var diagnostics = _globalDiagnostics
            .Concat(_contributions.Values.SelectMany(contribution => contribution.Diagnostics))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToArray();
        operation?.SetStage(IndexingStages.Serializing);
        var didPublish = outputRepublished;
        var outputDigest = outputRepublished
            ? await _outputPublisher.PublishAsync(outputPath, graph, diagnostics, cancellationToken).ConfigureAwait(false)
            : await ReadPublishedDigestAsync(outputPath, graph, diagnostics, cancellationToken).ConfigureAwait(false);
        if (!outputRepublished && !string.Equals(outputDigest, _publishedOutputDigest, StringComparison.Ordinal))
        {
            outputDigest = await _outputPublisher
                .PublishAsync(outputPath, graph, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            didPublish = true;
        }

        _publishedOutputDigest = outputDigest;

        _generation = MarkGenerationPublished(target.EventGeneration);
        var manifest = _contributions.Values.Select(contribution => new IncrementalManifestEntry(
                contribution.Fingerprint,
                contribution.ContributionKey))
            .ToArray();
        var state = new IncrementalCacheState(
            _requestIdentity,
            _contributions.Values,
            manifest,
            _generation,
            _globalDiagnostics,
            outputPath,
            outputDigest);
        operation?.SetStage(IndexingStages.WritingCache);
        await _cacheStore.SaveAsync(cachePath, state, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _publicationPending, 0);
        return new IncrementalRefreshResult(
            graph,
            outputDigest,
            IncrementalCacheLoadStatus.Missing,
            extractedProjectCount,
            reusedProjectCount,
            didPublish,
            _generation);
    }

    private async Task<string> ReadPublishedDigestAsync(
        string outputPath,
        GraphSnapshot graph,
        IReadOnlyList<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (File.Exists(outputPath))
        {
            try
            {
                return IncrementalHashing.Sha256File(outputPath);
            }
            catch (IOException)
            {
            }
        }

        return await _outputPublisher.PublishAsync(outputPath, graph, diagnostics, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task LoadAndExtractAllAsync(
        CancellationToken cancellationToken,
        IndexingObservationOperation? operation = null)
    {
        // A newly discovered external root is subscribed only after the first
        // evaluated load. Repeat once after coverage is established so an edit
        // in that observation gap cannot survive in the published snapshot.
        while (await LoadAndExtractAllOnceAsync(cancellationToken, operation).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<bool> LoadAndExtractAllOnceAsync(
        CancellationToken cancellationToken,
        IndexingObservationOperation? operation = null)
    {
        var transitionPublished = false;
        try
        {
            // Capture only dependencies already implicated by the current event
            // set. Comparing this affected-path baseline with the post-load bytes
            // lets us distinguish an equivalent generated rewrite from a change
            // that arrived while Roslyn was reading the project. No repository-
            // wide hash scan is introduced.
            _dependencyLoadTargetGeneration = Volatile.Read(ref _eventClock);
            CaptureDependencyLoadStartBaselines();
            Volatile.Write(ref _requiresColdReconciliation, 1);

            // Keep the last known coverage alive, but temporarily stop treating
            // project membership and evaluated output roots as authoritative.
            // This closes the interval between the old Roslyn load and publication
            // of the next evaluated input snapshot. Events observed in this
            // transition are journaled and reclassified once MSBuild has produced
            // the new boundary; only actual delivery loss requires recovery.
            var previousSnapshot = Volatile.Read(ref _inputSnapshot);
            var transitionSnapshot = WatcherInputSnapshot.CreateBootstrap(
                previousSnapshot.DiscoveryRoots.Length > 0
                    ? previousSnapshot.DiscoveryRoots
                    : [IncrementalPaths.CanonicalAbsolutePath(_request.RepositoryRoot)],
                _outputPath,
                _cachePath,
                previousSnapshot.WatchRoots.Length > 0
                    ? previousSnapshot.WatchRoots
                    : [new WatcherRoot(_request.RepositoryRoot, IncludeSubdirectories: true)],
                knownInputPaths: previousSnapshot.KnownInputPaths
                    .Append(_request.InputPath));
            Volatile.Write(ref _inputSnapshot, transitionSnapshot);
            transitionPublished = true;
            NotifyInputSnapshotChanged(transitionSnapshot, out _);

            _loadedSolution?.Dispose();
            _loadedSolution = null;
            operation?.SetStage(IndexingStages.LoadingProjects);
            _loadedSolution = _projectLoader is RoslynWorkspaceLoader roslynLoader
                ? await roslynLoader.LoadAsync(_request, operation, cancellationToken).ConfigureAwait(false)
                : await _projectLoader.LoadAsync(_request, cancellationToken).ConfigureAwait(false);
            _currentRoslynSolution = _loadedSolution.Workspace.CurrentSolution;

            // Publish evaluated membership before the expensive catalog/extraction
            // work. Events arriving during extraction are then retained against
            // the new snapshot and reconciled by the caller after this load.
            var inputSnapshot = WatcherInputSnapshot.Create(
                _loadedSolution,
                _request,
                _outputPath,
                _cachePath);
            Volatile.Write(ref _inputSnapshot, inputSnapshot);
            NotifyInputSnapshotChanged(inputSnapshot, out var requiresCoverageVerification);

            using var catalogPhase = operation?.BeginPhase(IndexingStages.Cataloging);
            _catalog = await new DeclarationCatalogBuilder()
                .BuildAsync(
                    _loadedSolution,
                    progress: progress => ReportCatalogProgress(catalogPhase, progress),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using var extractionPhase = operation?.BeginPhase(IndexingStages.ExtractingRelationships);
            var extractor = new SemanticReferenceExtractor();
            var extracted = await extractor
                .ExtractContributionsAsync(
                    _loadedSolution,
                    _catalog,
                    progress: progress => ReportExtractionProgress(extractionPhase, progress),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            operation?.SetStage(IndexingStages.Fingerprinting);
            _fingerprints = new IncrementalProjectFingerprintBuilder().BuildAll(_loadedSolution);
            _contributions = extracted.ToDictionary(
                contribution => contribution.Project.Key,
                contribution => new ProjectContributionEnvelope(
                    _fingerprints[contribution.Project.Key],
                    contribution.Graph,
                    contribution.Diagnostics),
                StringComparer.Ordinal);
            _lastExtractedProjectCount = _contributions.Count;
            _lastReusedProjectCount = 0;
            _globalDiagnostics = _loadedSolution.Diagnostics
                .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
                .Concat(inputSnapshot.InputDiscoveryDiagnostics)
                .Concat(_catalog.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
                .ToImmutableArray();
            operation?.SetStage(IndexingStages.MergingEvidence);
            MarkEvidenceReplaced();
            Volatile.Write(
                ref _requiresColdReconciliation,
                inputSnapshot.InputDiscoveryComplete ? 0 : 1);
            return requiresCoverageVerification;
        }
        catch (Exception exception) when (
            transitionPublished
            && Volatile.Read(ref _disposeRequested) == 0
            && !_stop.IsCancellationRequested)
        {
            // The old solution is deliberately discarded before a cold load.
            // If a request cancels or Roslyn fails after that boundary is
            // published, leave the worker immediately but independently wake
            // the host's shared recovery loop. Otherwise the next query would
            // wait forever on the bootstrap snapshot with no event to wake it.
            Volatile.Write(ref _requiresColdReconciliation, 1);
            MarkEventDeliveryUntrusted(
                $"Cold reconciliation was interrupted after the workspace boundary was replaced: {exception.Message}");
            throw;
        }
    }

    private async Task<Solution?> ApplySourceChangesAsync(
        IReadOnlyList<string> duePaths,
        IndexingObservationOperation? operation,
        CancellationToken cancellationToken)
    {
        if (_loadedSolution is null || _currentRoslynSolution is null)
        {
            return null;
        }

        var solution = _currentRoslynSolution;
        operation?.SetStage(IndexingStages.DiscoveringInputs);
        for (var pathIndex = 0; pathIndex < duePaths.Count; pathIndex++)
        {
            var path = duePaths[pathIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var exists = File.Exists(path);
            var changedDocument = false;
            foreach (var project in _loadedSolution.Projects)
            {
                foreach (var document in project.Project.Documents
                    .Where(document => PathsEqual(document.FilePath, path)))
                {
                    changedDocument = true;
                    if (solution.GetDocument(document.Id) is null)
                    {
                        return null;
                    }

                    solution = exists
                        ? solution.WithDocumentText(
                            document.Id,
                            SourceText.From(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)),
                            PreservationMode.PreserveIdentity)
                        : solution;
                }
            }

            if (!exists || !changedDocument)
            {
                return null;
            }

            operation?.ReportWork(pathIndex + 1, duePaths.Count, "files", path);
        }

        return solution;
    }

    private async Task RefreshAnalyzedProjectsAsync(
        Solution solution,
        IndexingObservationOperation? operation,
        CancellationToken cancellationToken)
    {
        var projects = new List<AnalyzedProject>(_loadedSolution!.Projects.Length);
        operation?.SetStage(IndexingStages.Compiling);
        var projectIndex = 0;
        foreach (var existing in _loadedSolution.Projects)
        {
            var project = solution.GetProject(existing.Project.Id);
            if (project is null)
            {
                throw new InvalidDataException($"Warm Roslyn solution lost project '{existing.Identity.Key}'.");
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                throw new InvalidDataException($"Roslyn did not produce a warm compilation for '{existing.Identity.Key}'.");
            }

            projects.Add(new AnalyzedProject(project, existing.Identity, compilation));
            operation?.ReportWork(
                ++projectIndex,
                _loadedSolution.Projects.Length,
                "projects",
                project.Name);
        }

        _loadedSolution.ReplaceProjects(projects);
    }

    private void DrainFileEvents()
    {
        while (_fileEvents.Reader.TryRead(out var fileEvent))
        {
            Interlocked.Decrement(ref _queuedEventCount);
            HandleFileChange(fileEvent);
        }
    }

    private void HandleFileChange(FileChangeCommand fileEvent)
    {
        try
        {
            foreach (var endpoint in fileEvent.Change.Endpoints)
            {
                var path = IncrementalPaths.CanonicalAbsolutePath(endpoint);
                if (_dirtyPaths.TryGetValue(path, out var existing))
                {
                    _dirtyPaths[path] = existing with
                    {
                        LastGeneration = Math.Max(existing.LastGeneration, fileEvent.Generation),
                        RequiresColdReconciliation = existing.RequiresColdReconciliation
                            || fileEvent.Change.RequiresColdReconciliation,
                    };
                }
                else
                {
                    _dirtyPaths.Add(
                        path,
                        new DirtyPathState(
                            path,
                            fileEvent.Generation,
                            fileEvent.Generation,
                            fileEvent.Change.RequiresColdReconciliation));
                }
            }

            _generation = _generation.AdvanceEventsThrough(
                Math.Max(_generation.EventGeneration, fileEvent.Generation));
        }
        catch (ArgumentException)
        {
            Volatile.Write(ref _requiresColdReconciliation, 1);
            MarkEventDeliveryUntrusted("An invalid file-system event path was received.");
        }
    }

    private void MarkEventDeliveryUntrusted(string reason)
    {
        lock (_trustGate)
        {
            _eventTrustVersion++;
            _eventDeliveryUntrusted = 1;
        }

        try
        {
            // Every loss advances the epoch and notifies the host. The host
            // coalesces the semaphore signal, while a second loss during a
            // rebuild remains visible and keeps recovery pending.
            _trustLostCallback?.Invoke(reason);
        }
        catch
        {
            // A watcher callback must never fail because a recovery signal
            // consumer is stopping. The session remains untrusted.
        }
    }

    private bool IsEventDeliveryUntrusted()
    {
        lock (_trustGate)
        {
            return _eventDeliveryUntrusted != 0;
        }
    }

    private long CaptureEventTrustVersion()
    {
        lock (_trustGate)
        {
            return _eventTrustVersion;
        }
    }

    private bool TryAcknowledgeEventTrust(long version)
    {
        lock (_trustGate)
        {
            if (_eventTrustVersion != version)
            {
                return false;
            }

            _eventDeliveryUntrusted = 0;
            return true;
        }
    }

    private void NotifyInputSnapshotChanged(WatcherInputSnapshot snapshot, out bool coverageChanged)
    {
        coverageChanged = false;
        try
        {
            coverageChanged = _inputSnapshotChangedCallback?.Invoke(snapshot) == true;
        }
        catch
        {
            // Snapshot publication is an optimization boundary for the host.
            // A watcher coverage failure is reported through its normal
            // recovery path; it must not strand the Roslyn worker here.
        }
    }

    private HashSet<string> ResolveDirtyProjects(IReadOnlyList<string> duePaths)
    {
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in duePaths)
        {
            if (_inputSnapshot.IsKnownSource(path))
            {
                var matchingProjects = ResolveProjectsForPath(path);
                if (matchingProjects.Count > 0)
                {
                    dirty.UnionWith(matchingProjects);
                    continue;
                }
            }

            // Only exact source edits are eligible for the warm document path.
            // New/deleted/renamed sources and every other accepted input must
            // be re-evaluated by MSBuild so Include/Remove/Condition semantics
            // remain authoritative.
            Volatile.Write(ref _requiresColdReconciliation, 1);
            return _fingerprints.Keys.ToHashSet(StringComparer.Ordinal);
        }

        return dirty.Count == 0
            ? dirty
            : ExpandReverseDependencies(dirty, _fingerprints);
    }

    private HashSet<string> ResolveProjectsForPath(string path)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);
        if (_loadedSolution is null)
        {
            return matches;
        }

        foreach (var projectKey in _inputSnapshot.GetSourceProjects(path))
        {
            if (_fingerprints.ContainsKey(projectKey))
            {
                matches.Add(projectKey);
            }
        }

        return matches;
    }

    private static bool PathsEqual(string? first, string second) =>
        first is not null
        && string.Equals(
            IncrementalPaths.CanonicalAbsolutePath(first),
            IncrementalPaths.CanonicalAbsolutePath(second),
            IncrementalPaths.PathComparison);

    private IReadOnlyList<DirtyPathState> DueDirtyPathStates(long targetGeneration) => _dirtyPaths.Values
        .Where(path => path.FirstGeneration <= targetGeneration)
        .OrderBy(path => path.Path, IncrementalPaths.PathComparer)
        .ToArray();

    private void CaptureConsumedDependencyBaselines()
    {
        foreach (var path in _dirtyPaths.Keys.ToArray())
        {
            if (!Volatile.Read(ref _inputSnapshot).IsKnownDependency(path))
            {
                continue;
            }

            try
            {
                _consumedDependencyBaselines[path] = SourceFingerprint.FromFile(
                    path,
                    _request.RepositoryRoot,
                    includeContentHash: true);
            }
            catch (IOException)
            {
                _consumedDependencyBaselines.Remove(path);
            }
            catch (UnauthorizedAccessException)
            {
                _consumedDependencyBaselines.Remove(path);
            }
        }
    }

    private void CaptureDependencyLoadStartBaselines()
    {
        _dependencyLoadStartBaselines.Clear();
        var snapshot = Volatile.Read(ref _inputSnapshot);
        foreach (var path in _dirtyPaths.Keys.ToArray())
        {
            if (!snapshot.IsKnownDependency(path))
            {
                continue;
            }

            try
            {
                _dependencyLoadStartBaselines[path] = SourceFingerprint.FromFile(
                    path,
                    _request.RepositoryRoot,
                    includeContentHash: true);
            }
            catch (IOException)
            {
                // A missing/unreadable baseline cannot prove equivalence. The
                // corresponding event remains dirty and takes the cold path.
            }
            catch (UnauthorizedAccessException)
            {
                // See the IOException case above.
            }
        }
    }

    private void DiscardAlreadyConsumedDependencyChanges()
    {
        foreach (var state in _dirtyPaths.Values.ToArray())
        {
            if (!Volatile.Read(ref _inputSnapshot).IsKnownDependency(state.Path)
                || !_consumedDependencyBaselines.TryGetValue(state.Path, out var baseline))
            {
                continue;
            }

            var eventArrivedDuringLoad = state.LastGeneration > _dependencyLoadTargetGeneration;
            _dependencyLoadStartBaselines.TryGetValue(state.Path, out var loadStart);
            if (eventArrivedDuringLoad && loadStart is null)
            {
                // A missing pre-load fingerprint cannot prove that Roslyn
                // consumed the bytes represented by this event. Retain the
                // event until a subsequent load can establish a trustworthy
                // before/after comparison, even when the event was queued
                // before the load's generation boundary was captured.
                continue;
            }

            try
            {
                var current = SourceFingerprint.FromFile(
                    state.Path,
                    _request.RepositoryRoot,
                    includeContentHash: true);
                var equivalent = eventArrivedDuringLoad
                    ? loadStart!.ContentEquals(current)
                    : baseline.ContentEquals(current);
                if (equivalent)
                {
                    _dirtyPaths.Remove(state.Path);
                }
            }
            catch (IOException)
            {
                // If equivalence cannot be established, retain the dirty
                // path and take the conservative cold path.
            }
            catch (UnauthorizedAccessException)
            {
                // See the IOException case above.
            }
        }
    }

    private void ClearDirtyPaths(IReadOnlyList<string> paths, long targetGeneration)
    {
        foreach (var path in paths)
        {
            if (!_dirtyPaths.TryGetValue(path, out var state))
            {
                continue;
            }

            if (state.LastGeneration <= targetGeneration)
            {
                _dirtyPaths.Remove(path);
            }
            else
            {
                _dirtyPaths[path] = state with
                {
                    FirstGeneration = state.LastGeneration,
                };
            }
        }
    }

    private HashSet<string> ExpandReverseDependencies(
        IEnumerable<string> initial,
        IReadOnlyDictionary<string, ProjectFingerprint> fingerprints)
    {
        var dirty = initial.ToHashSet(StringComparer.Ordinal);
        var expanded = dirty.ToHashSet(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (projectKey, fingerprint) in fingerprints)
            {
                if (expanded.Contains(projectKey)
                    || !fingerprint.ProjectReferenceKeys
                        .Select(ReferenceProjectKey)
                        .Any(expanded.Contains))
                {
                    continue;
                }

                dirty.Add(projectKey);
                if (expanded.Add(projectKey))
                {
                    changed = true;
                }
            }
        }

        return dirty;
    }

    private RefreshGeneration MarkGenerationIndexed(long targetGeneration)
    {
        return _generation.IndexedGeneration >= targetGeneration
            ? _generation
            : _generation.MarkIndexed(targetGeneration);
    }

    private RefreshGeneration MarkGenerationPublished(long targetGeneration)
    {
        return _generation.PublishedGeneration >= targetGeneration
            ? _generation
            : _generation.MarkPublished(targetGeneration);
    }

    private void MarkEvidenceReplaced()
    {
        _semanticIndex = null;
        _evidenceRevision = checked(_evidenceRevision + 1);
    }

    private SemanticEvidenceView CreateSemanticEvidenceView(
        RefreshTarget target,
        CancellationToken cancellationToken,
        IndexingObservationOperation? operation = null)
    {
        if (_loadedSolution is null || _catalog is null)
        {
            throw new InvalidOperationException("Semantic evidence is not ready.");
        }

        var indexWasBuilt = _semanticIndex is null;
        if (indexWasBuilt)
        {
            operation?.SetStage(IndexingStages.BuildingQueryIndex);
        }

        _semanticIndex ??= SemanticEvidenceIndex.Create(
            _catalog,
            _contributions.Values,
            cancellationToken);
        if (indexWasBuilt)
        {
            PublishEvidenceObservation();
        }
        return new SemanticEvidenceView(
            _semanticMode == "cold" ? Guid.Empty : _sessionId,
            _semanticMode,
            _requestIdentity.CanonicalKey,
            _loadedSolution,
            _catalog,
            _semanticIndex,
            Volatile.Read(ref _inputSnapshot),
            _generation,
            target.EventGeneration,
            _evidenceRevision,
            _cursorSecret,
            _globalDiagnostics,
            _contributions.Values
                .SelectMany(contribution => contribution.Diagnostics)
                .ToArray());
    }

    private void PublishEvidenceObservation()
    {
        if (_loadedSolution is null || _catalog is null)
        {
            return;
        }

        var documentInstances = _loadedSolution.Projects
            .Sum(project => project.Project.Documents.LongCount());
        var syntaxTrees = _loadedSolution.Projects
            .Sum(project => project.Compilation.SyntaxTrees.LongCount());
        _observation.PublishEvidence(new ObservationEvidenceSummary(
            _evidenceRevision,
            _generation.IndexedGeneration,
            _loadedSolution.Projects.Length,
            documentInstances,
            syntaxTrees,
            SourceGeneratedDocuments: null,
            Declarations: _catalog.Declarations.Length,
            ContributionNodes: _contributions.Values.Sum(contribution => (long)contribution.Graph.Nodes.Length),
            ContributionEdges: _contributions.Values.Sum(contribution => (long)contribution.Graph.Edges.Length),
            SemanticIndexBuilt: _semanticIndex is not null,
            SemanticIndexEdges: _semanticIndex?.Edges.Length,
            QueryCachesBuilt: _semanticIndex is null ? Array.Empty<string>() : ["semantic_index"],
            ExtractedProjects: _lastExtractedProjectCount,
            ReusedProjects: _lastReusedProjectCount,
            ObservedAtUtc: DateTimeOffset.UtcNow));
    }

    private static void ReportCatalogProgress(
        IndexingObservationPhase? phase,
        DeclarationCatalogBuilder.CatalogProgress progress)
    {
        phase?.SetStage(IndexingStages.Cataloging, progress.ProjectName);
        phase?.ReportWork(
            progress.CompletedProjects,
            progress.TotalProjects,
            "projects",
            progress.ProjectName);
    }

    private static void ReportExtractionProgress(
        IndexingObservationPhase? phase,
        SemanticReferenceExtractor.ExtractionProgress progress)
    {
        phase?.SetStage(progress.Stage, progress.Detail);
        if (progress.Completed > 0
            || (progress.Stage == IndexingStages.ExtractingReferences && progress.Total is not null))
        {
            phase?.ReportWork(progress.Completed, progress.Total, progress.Unit, progress.Detail);
        }
    }

    private static string GetInternalStateDirectory(string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The output path has no parent directory.");
        return IncrementalPaths.CanonicalAbsolutePath(
            Path.Combine(outputDirectory, ".graphify-csharp"));
    }

    private static bool IsPhysicalPathOrUnder(string path, string parent)
    {
        return IncrementalPaths.TryResolvePhysicalPath(path, out var physicalPath)
            && IncrementalPaths.TryResolvePhysicalPath(parent, out var physicalParent)
            && IncrementalPaths.IsPathOrUnder(physicalPath, physicalParent);
    }

    private void ValidateTarget(RefreshTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.SessionId != _sessionId)
        {
            throw new InvalidOperationException("The refresh target belongs to a different incremental session.");
        }
    }

    private RefreshTarget CaptureCurrentTarget()
    {
        lock (_lifecycleGate)
        {
            return CaptureCurrentTargetLocked();
        }
    }

    private RefreshTarget CaptureCurrentTargetLocked() => new(
        _sessionId,
        Volatile.Read(ref _eventClock));

    private void FailPendingCommands(Exception exception)
    {
        while (_commands.Reader.TryRead(out var command))
        {
            if (command is RefreshCommand refresh)
            {
                if (exception is OperationCanceledException)
                {
                    refresh.Completion.TrySetCanceled();
                }
                else
                {
                    refresh.Completion.TrySetException(exception);
                }
            }
            else if (command is SemanticQueryCommand semantic)
            {
                if (exception is OperationCanceledException)
                {
                    semantic.Completion.TrySetCanceled();
                }
                else
                {
                    semantic.Completion.TrySetException(exception);
                }
            }
            else if (command is SemanticExportCommand export)
            {
                if (exception is OperationCanceledException)
                {
                    export.Completion.TrySetCanceled();
                }
                else
                {
                    export.Completion.TrySetException(exception);
                }
            }
        }
    }

    private bool TryBeginRefresh(RefreshCommand refresh)
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                refresh.Completion.TrySetCanceled(_stop.Token);
                return false;
            }

            if (Status == IncrementalSessionStatus.Failed)
            {
                refresh.Completion.TrySetException(
                    new InvalidOperationException(
                        "The incremental session failed during startup.",
                        Volatile.Read(ref _failure)));
                return false;
            }

            _activeRefreshCompletion = refresh.Completion;
            return true;
        }
    }

    private void EndRefresh(TaskCompletionSource<IncrementalRefreshResult> completion)
    {
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_activeRefreshCompletion, completion))
            {
                _activeRefreshCompletion = null;
            }
        }
    }

    private void TrySetStatusIfActive(IncrementalSessionStatus status)
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposeRequested) == 0)
            {
                Volatile.Write(ref _status, (int)status);
            }
        }
    }

    private void CancelPendingCommandsLocked()
    {
        _activeRefreshCompletion?.TrySetCanceled(_stop.Token);
        _ready.TrySetCanceled(_stop.Token);
        FailPendingCommands(new OperationCanceledException(_stop.Token));
    }

    private static GraphSnapshot MergeContributions(IEnumerable<ProjectContributionEnvelope> contributions) =>
        GraphSnapshot.Create(
            contributions.SelectMany(contribution => contribution.Graph.Nodes),
            contributions.SelectMany(contribution => contribution.Graph.Edges));

    private static string ReferenceProjectKey(string referenceKey)
    {
        var separator = referenceKey.IndexOf('\u001F');
        return separator < 0 ? referenceKey : referenceKey[..separator];
    }

    private abstract record SessionCommand;

    private sealed record RefreshCommand(
        RefreshTarget Target,
        bool Rebuild,
        bool PublishOutput,
        string OperationKind,
        TaskCompletionSource<IncrementalRefreshResult> Completion) : SessionCommand;

    private sealed record WatcherInvalidatedCommand(string Reason) : SessionCommand;

    private sealed record SemanticQueryCommand(
        RefreshTarget Target,
        SemanticQuerySpec Specification,
        CancellationToken RequestCancellationToken,
        TaskCompletionSource<SemanticQueryResponse> Completion) : SessionCommand;

    private sealed record SemanticExportCommand(
        RefreshTarget Target,
        string OutputPath,
        CancellationToken RequestCancellationToken,
        TaskCompletionSource<SemanticQueryResponse> Completion) : SessionCommand;

    private sealed record FileChangeCommand(FileChangeEvent Change, long Generation);

    private sealed record DirtyPathState(
        string Path,
        long FirstGeneration,
        long LastGeneration,
        bool RequiresColdReconciliation);
}
