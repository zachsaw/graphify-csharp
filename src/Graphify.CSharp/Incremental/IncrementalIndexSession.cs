using System.Collections.Immutable;
using System.Threading.Channels;
using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalIndexSession : IAsyncDisposable
{
    private const int EventQueueCapacity = 4096;
    private readonly object _lifecycleGate = new();
    private readonly ProjectLoadRequest _request;
    private readonly string _outputPath;
    private readonly string _cachePath;
    private readonly RefreshRequestIdentity _requestIdentity;
    private readonly IProjectLoader _projectLoader;
    private readonly IncrementalCacheStore _cacheStore;
    private readonly IncrementalOutputPublisher _outputPublisher;
    private readonly Func<Guid> _sessionIdFactory;
    private readonly Action<string>? _trustLostCallback;
    private readonly Channel<SessionCommand> _commands = Channel.CreateUnbounded<SessionCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
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
    private readonly Dictionary<string, DirtyPathState> _dirtyPaths = new(StringComparer.Ordinal);
    private long _eventClock;
    private long _eventTrustVersion;
    private int _queuedEventCount;
    private int _backgroundIndexRequested;
    private int _eventDeliveryUntrusted;
    private int _status = (int)IncrementalSessionStatus.Created;
    private Exception? _failure;
    private Task? _workerTask;
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
    private bool _requiresColdReconciliation;

    public IncrementalIndexSession(
        ProjectLoadRequest request,
        string outputPath,
        IProjectLoader? projectLoader = null,
        IncrementalCacheStore? cacheStore = null,
        IncrementalOutputPublisher? outputPublisher = null,
        Func<Guid>? sessionIdFactory = null,
        Action<string>? trustLostCallback = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        _outputPath = Path.GetFullPath(outputPath);
        _cachePath = IncrementalCachePath.ForOutput(_outputPath);
        _requestIdentity = new RefreshRequestIdentity(
            request.InputPath,
            request.RepositoryRoot,
            request.Configuration,
            request.TargetFramework);
        _projectLoader = projectLoader ?? new RoslynWorkspaceLoader();
        _cacheStore = cacheStore ?? new IncrementalCacheStore();
        _outputPublisher = outputPublisher ?? new IncrementalOutputPublisher();
        _sessionIdFactory = sessionIdFactory ?? Guid.NewGuid;
        _trustLostCallback = trustLostCallback;
        _generation = new RefreshGeneration(_sessionIdFactory());
    }

    public IncrementalSessionStatus Status =>
        (IncrementalSessionStatus)Volatile.Read(ref _status);

    public Guid SessionId => _generation.SessionId;

    public long EventGeneration => Volatile.Read(ref _eventClock);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureWorkerStarted();
        return cancellationToken.CanBeCanceled
            ? _ready.Task.WaitAsync(cancellationToken)
            : _ready.Task;
    }

    public Task<IncrementalRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(rebuild: false, cancellationToken);

    public Task<IncrementalRefreshResult> RebuildAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(rebuild: true, cancellationToken);

    private Task<IncrementalRefreshResult> RefreshCoreAsync(bool rebuild, CancellationToken cancellationToken)
    {
        EnsureWorkerStarted();
        var target = new RefreshTarget(
            _generation.SessionId,
            Volatile.Read(ref _eventClock));
        var completion = new TaskCompletionSource<IncrementalRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new RefreshCommand(target, rebuild, completion)))
        {
            completion.TrySetException(new InvalidOperationException("The incremental session is not accepting refresh requests."));
        }

        _workSignal.Release();
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public void ReportFileChanged(string path)
    {
        EnsureWorkerStarted();
        var generation = Interlocked.Increment(ref _eventClock);
        var queued = Interlocked.Increment(ref _queuedEventCount);
        var accepted = !string.IsNullOrWhiteSpace(path)
            && queued <= EventQueueCapacity
            && _fileEvents.Writer.TryWrite(new FileChangeCommand(path, generation));
        if (!accepted)
        {
            Interlocked.Decrement(ref _queuedEventCount);
            MarkEventDeliveryUntrusted("The file-system event queue is full or received an invalid path.");
        }
        else if (Volatile.Read(ref _eventDeliveryUntrusted) == 0
            && Interlocked.Exchange(ref _backgroundIndexRequested, 1) == 0)
        {
            // The event signal below wakes the worker; this flag causes the
            // worker to index after it has drained the event queue.
        }

        if (accepted)
        {
            _workSignal.Release();
        }
    }

    public void RequestBackgroundIndex()
    {
        EnsureWorkerStarted();
        if (Volatile.Read(ref _eventDeliveryUntrusted) != 0
            || _requiresColdReconciliation)
        {
            return;
        }

        if (Interlocked.Exchange(ref _backgroundIndexRequested, 1) == 0)
        {
            _workSignal.Release();
        }
    }

    public void ReportWatcherFailure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        MarkEventDeliveryUntrusted(reason);
        if (_commands.Writer.TryWrite(new WatcherInvalidatedCommand(reason)))
        {
            _workSignal.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_lifecycleGate)
        {
            worker = _workerTask;
            if (worker is null)
            {
                _status = (int)IncrementalSessionStatus.Stopped;
                _stop.Dispose();
                _workSignal.Dispose();
                return;
            }

            _stop.Cancel();
            _commands.Writer.TryComplete();
            _fileEvents.Writer.TryComplete();
            _workSignal.Release();
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
            _status = (int)IncrementalSessionStatus.Stopped;
            _stop.Dispose();
            _workSignal.Dispose();
        }
    }

    private void EnsureWorkerStarted()
    {
        lock (_lifecycleGate)
        {
            if (Status == IncrementalSessionStatus.Stopped)
            {
                throw new ObjectDisposedException(nameof(IncrementalIndexSession));
            }

            if (Status == IncrementalSessionStatus.Failed)
            {
                throw new InvalidOperationException("The incremental session failed during startup.", _failure);
            }

            if (_workerTask is not null)
            {
                return;
            }

            _status = (int)IncrementalSessionStatus.Starting;
            _workerTask = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await InitializeAsync(_stop.Token).ConfigureAwait(false);
            _status = (int)IncrementalSessionStatus.Ready;
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
                    _status = (int)IncrementalSessionStatus.Refreshing;
                    try
                    {
                        await IndexBackgroundAsync(_stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (Status != IncrementalSessionStatus.Failed)
                        {
                            _status = (int)IncrementalSessionStatus.Ready;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _ready.TrySetCanceled(_stop.Token);
            FailPendingCommands(new OperationCanceledException(_stop.Token));
        }
        catch (Exception exception)
        {
            _failure = exception;
            _status = (int)IncrementalSessionStatus.Failed;
            _ready.TrySetException(exception);
            FailPendingCommands(exception);
        }
        finally
        {
            _loadedSolution?.Dispose();
            _loadedSolution = null;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadAndExtractAllAsync(cancellationToken).ConfigureAwait(false);
        DrainFileEvents();
        var target = new RefreshTarget(_generation.SessionId, Volatile.Read(ref _eventClock));
        var duePaths = DueDirtyPaths(target.EventGeneration);
        if (Volatile.Read(ref _eventDeliveryUntrusted) != 0 || duePaths.Count > 0)
        {
            await ReconcileAsync(
                    target,
                    forceCold: Volatile.Read(ref _eventDeliveryUntrusted) != 0,
                    publishOutput: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await PublishCurrentAsync(
            target,
            extractedProjectCount: _contributions.Count,
            reusedProjectCount: 0,
            outputRepublished: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCommandAsync(SessionCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case RefreshCommand refresh:
                try
                {
                    _status = (int)IncrementalSessionStatus.Refreshing;
                    var result = await ReconcileAsync(
                            refresh.Target,
                            forceCold: refresh.Rebuild,
                            publishOutput: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    refresh.Completion.TrySetResult(result);
                    _status = (int)IncrementalSessionStatus.Ready;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _status = (int)IncrementalSessionStatus.Ready;
                    refresh.Completion.TrySetException(exception);
                }

                break;
            case WatcherInvalidatedCommand:
                _requiresColdReconciliation = true;
                break;
        }
    }

    private async Task<IncrementalRefreshResult> ReconcileAsync(
        RefreshTarget target,
        bool forceCold,
        bool publishOutput,
        CancellationToken cancellationToken)
    {
        ValidateTarget(target);
        DrainFileEvents();
        forceCold |= Volatile.Read(ref _eventDeliveryUntrusted) != 0 || _requiresColdReconciliation;
        var duePaths = DueDirtyPaths(target.EventGeneration);
        var dirtyProjectKeys = forceCold
            ? _fingerprints.Keys.ToHashSet(StringComparer.Ordinal)
            : ResolveDirtyProjects(duePaths);
        forceCold |= _requiresColdReconciliation;
        if (forceCold)
        {
            dirtyProjectKeys = _fingerprints.Keys.ToHashSet(StringComparer.Ordinal);
        }
        var fullRebuild = forceCold;
        var outputChanged = dirtyProjectKeys.Count > 0 || fullRebuild;
        if (!outputChanged)
        {
            _generation = _generation.AdvanceEventsThrough(
                Math.Max(_generation.EventGeneration, target.EventGeneration));
            _generation = MarkGenerationIndexed(target.EventGeneration);
            if (!publishOutput)
            {
                return CreateUnpublishedResult(
                    extractedProjectCount: 0,
                    reusedProjectCount: _contributions.Count);
            }

            var result = await PublishCurrentAsync(
                    target,
                    extractedProjectCount: 0,
                    reusedProjectCount: _contributions.Count,
                    outputRepublished: _generation.PublishedGeneration < target.EventGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            ClearDirtyPaths(duePaths, target.EventGeneration);
            return result;
        }

        var extractedProjectCount = 0;
        var reusedProjectCount = 0;
        if (fullRebuild)
        {
            var trustVersionAtStart = Volatile.Read(ref _eventTrustVersion);
            await LoadAndExtractAllAsync(cancellationToken).ConfigureAwait(false);
            extractedProjectCount = _contributions.Count;
            _requiresColdReconciliation = false;
            if (Volatile.Read(ref _eventTrustVersion) == trustVersionAtStart)
            {
                Interlocked.Exchange(ref _eventDeliveryUntrusted, 0);
            }
            else
            {
                _requiresColdReconciliation = true;
            }
        }
        else
        {
            var updated = await ApplySourceChangesAsync(duePaths, cancellationToken).ConfigureAwait(false);
            if (updated is null)
            {
                await LoadAndExtractAllAsync(cancellationToken).ConfigureAwait(false);
                extractedProjectCount = _contributions.Count;
                _requiresColdReconciliation = false;
            }
            else
            {
                _currentRoslynSolution = updated;
                await RefreshAnalyzedProjectsAsync(updated, cancellationToken).ConfigureAwait(false);
                var newFingerprints = new IncrementalProjectFingerprintBuilder().BuildAll(_loadedSolution!);
                dirtyProjectKeys = ExpandReverseDependencies(dirtyProjectKeys, newFingerprints);
                var catalog = await new DeclarationCatalogBuilder()
                    .BuildAsync(_loadedSolution!, cancellationToken)
                    .ConfigureAwait(false);
                var extractor = new SemanticReferenceExtractor();
                var extracted = await extractor
                    .ExtractContributionsAsync(_loadedSolution!, catalog, dirtyProjectKeys, cancellationToken)
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
                _globalDiagnostics = _loadedSolution.Diagnostics
                    .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
                    .Concat(catalog.Diagnostics)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
                    .ToImmutableArray();
            }
        }

        _generation = _generation.AdvanceEventsThrough(
            Math.Max(_generation.EventGeneration, target.EventGeneration));
        _generation = MarkGenerationIndexed(target.EventGeneration);
        if (!publishOutput)
        {
            ClearDirtyPaths(duePaths, target.EventGeneration);
            return CreateUnpublishedResult(extractedProjectCount, reusedProjectCount);
        }

        var published = await PublishCurrentAsync(
            target,
            extractedProjectCount,
            reusedProjectCount,
            outputRepublished: true,
            cancellationToken).ConfigureAwait(false);
        ClearDirtyPaths(duePaths, target.EventGeneration);
        return published;
    }

    private async Task IndexBackgroundAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _eventDeliveryUntrusted) != 0)
        {
            return;
        }

        DrainFileEvents();
        var target = new RefreshTarget(_generation.SessionId, Volatile.Read(ref _eventClock));
        await ReconcileAsync(
                target,
                forceCold: false,
                publishOutput: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private IncrementalRefreshResult CreateUnpublishedResult(
        int extractedProjectCount,
        int reusedProjectCount)
    {
        if (string.IsNullOrWhiteSpace(_publishedOutputDigest))
        {
            throw new InvalidOperationException("Background indexing cannot complete before the initial output is published.");
        }

        return new IncrementalRefreshResult(
            MergeContributions(_contributions.Values),
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
        CancellationToken cancellationToken)
    {
        var graph = MergeContributions(_contributions.Values);
        var diagnostics = _globalDiagnostics
            .Concat(_contributions.Values.SelectMany(contribution => contribution.Diagnostics))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToArray();
        var didPublish = outputRepublished;
        var outputDigest = outputRepublished
            ? await _outputPublisher.PublishAsync(_outputPath, graph, diagnostics, cancellationToken).ConfigureAwait(false)
            : await ReadPublishedDigestAsync(_outputPath, graph, diagnostics, cancellationToken).ConfigureAwait(false);
        if (!outputRepublished && !string.Equals(outputDigest, _publishedOutputDigest, StringComparison.Ordinal))
        {
            outputDigest = await _outputPublisher
                .PublishAsync(_outputPath, graph, diagnostics, cancellationToken)
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
            _outputPath,
            outputDigest);
        await _cacheStore.SaveAsync(_cachePath, state, cancellationToken).ConfigureAwait(false);
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

    private async Task LoadAndExtractAllAsync(CancellationToken cancellationToken)
    {
        _requiresColdReconciliation = true;
        _loadedSolution?.Dispose();
        _loadedSolution = null;
        _loadedSolution = await _projectLoader.LoadAsync(_request, cancellationToken).ConfigureAwait(false);
        _currentRoslynSolution = _loadedSolution.Workspace.CurrentSolution;
        _catalog = await new DeclarationCatalogBuilder().BuildAsync(_loadedSolution, cancellationToken).ConfigureAwait(false);
        var extractor = new SemanticReferenceExtractor();
        var extracted = await extractor
            .ExtractContributionsAsync(_loadedSolution, _catalog, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _fingerprints = new IncrementalProjectFingerprintBuilder().BuildAll(_loadedSolution);
        _contributions = extracted.ToDictionary(
            contribution => contribution.Project.Key,
            contribution => new ProjectContributionEnvelope(
                _fingerprints[contribution.Project.Key],
                contribution.Graph,
                contribution.Diagnostics),
            StringComparer.Ordinal);
        _globalDiagnostics = _loadedSolution.Diagnostics
            .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
            .Concat(_catalog.Diagnostics)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
        _requiresColdReconciliation = false;
    }

    private async Task<Solution?> ApplySourceChangesAsync(
        IReadOnlyList<string> duePaths,
        CancellationToken cancellationToken)
    {
        if (_loadedSolution is null || _currentRoslynSolution is null)
        {
            return null;
        }

        var solution = _currentRoslynSolution;
        foreach (var path in duePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
                        : solution.RemoveDocument(document.Id);
                }
            }

            if (exists && !changedDocument)
            {
                var projectKeys = ResolveProjectsForPath(path);
                if (projectKeys.Count == 0)
                {
                    return null;
                }

                var sourceText = SourceText.From(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                foreach (var projectKey in projectKeys)
                {
                    var project = _loadedSolution.Projects.Single(item => item.Identity.Key == projectKey);
                    solution = solution.AddDocument(
                        DocumentId.CreateNewId(project.Project.Id, Path.GetFileName(path)),
                        Path.GetFileName(path),
                        sourceText,
                        filePath: path);
                }
            }
        }

        return solution;
    }

    private async Task RefreshAnalyzedProjectsAsync(Solution solution, CancellationToken cancellationToken)
    {
        var projects = new List<AnalyzedProject>(_loadedSolution!.Projects.Length);
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
            var path = IncrementalPaths.CanonicalAbsolutePath(fileEvent.Path);
            if (_dirtyPaths.TryGetValue(path, out var existing))
            {
                _dirtyPaths[path] = existing with { LastGeneration = Math.Max(existing.LastGeneration, fileEvent.Generation) };
            }
            else
            {
                _dirtyPaths.Add(path, new DirtyPathState(path, fileEvent.Generation, fileEvent.Generation));
            }

            _generation = _generation.AdvanceEventsThrough(
                Math.Max(_generation.EventGeneration, fileEvent.Generation));
        }
        catch (ArgumentException)
        {
            _requiresColdReconciliation = true;
            MarkEventDeliveryUntrusted("An invalid file-system event path was received.");
        }
    }

    private void MarkEventDeliveryUntrusted(string reason)
    {
        if (Interlocked.Exchange(ref _eventDeliveryUntrusted, 1) == 0)
        {
            Interlocked.Increment(ref _eventTrustVersion);
            try
            {
                _trustLostCallback?.Invoke(reason);
            }
            catch
            {
                // A watcher callback must never fail because a recovery signal
                // consumer is stopping. The session remains untrusted.
            }
        }
    }

    private HashSet<string> ResolveDirtyProjects(IReadOnlyList<string> duePaths)
    {
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in duePaths)
        {
            if (IsBuildInput(path))
            {
                _requiresColdReconciliation = true;
                return _fingerprints.Keys.ToHashSet(StringComparer.Ordinal);
            }

            var matchingProjects = ResolveProjectsForPath(path);
            if (matchingProjects.Count > 0)
            {
                dirty.UnionWith(matchingProjects);
                continue;
            }

            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                _requiresColdReconciliation = true;
                return _fingerprints.Keys.ToHashSet(StringComparer.Ordinal);
            }
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

        foreach (var project in _loadedSolution.Projects)
        {
            var fingerprint = _fingerprints.GetValueOrDefault(project.Identity.Key);
            if (fingerprint is null)
            {
                continue;
            }

            var relativePath = IncrementalPaths.CanonicalRelativePath(path, _request.RepositoryRoot);
            if (fingerprint.SourceFiles.Any(source => string.Equals(source.RelativePath, relativePath, StringComparison.Ordinal))
                || fingerprint.ProjectFile is { } projectFile
                    && string.Equals(projectFile.RelativePath, relativePath, StringComparison.Ordinal)
                || IsUnderProjectDirectory(path, project.Identity))
            {
                if (!IsIgnoredPath(path))
                {
                    matches.Add(project.Identity.Key);
                }
            }
        }

        return matches;
    }

    private bool IsUnderProjectDirectory(string path, ProjectIdentity identity)
    {
        var projectPath = Path.Combine(
            _request.RepositoryRoot,
            identity.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var projectDirectory = Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath) ?? _request.RepositoryRoot;
        var relative = Path.GetRelativePath(projectDirectory, path);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private IReadOnlyList<string> DueDirtyPaths(long targetGeneration) => _dirtyPaths.Values
        .Where(path => path.FirstGeneration <= targetGeneration)
        .Select(path => path.Path)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

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

    private void ValidateTarget(RefreshTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.SessionId != _generation.SessionId)
        {
            throw new InvalidOperationException("The refresh target belongs to a different incremental session.");
        }
    }

    private void FailPendingCommands(Exception exception)
    {
        while (_commands.Reader.TryRead(out var command))
        {
            if (command is RefreshCommand refresh)
            {
                refresh.Completion.TrySetException(exception);
            }
        }
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

    private static bool PathsEqual(string? first, string second) =>
        first is not null
        && string.Equals(
            IncrementalPaths.CanonicalAbsolutePath(first),
            IncrementalPaths.CanonicalAbsolutePath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsBuildInput(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), "global.json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), "Directory.Build.props", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), "Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), "Directory.Packages.props", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredPath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));
    }

    private abstract record SessionCommand;

    private sealed record RefreshCommand(
        RefreshTarget Target,
        bool Rebuild,
        TaskCompletionSource<IncrementalRefreshResult> Completion) : SessionCommand;

    private sealed record WatcherInvalidatedCommand(string Reason) : SessionCommand;

    private sealed record FileChangeCommand(string Path, long Generation);

    private sealed record DirtyPathState(string Path, long FirstGeneration, long LastGeneration);
}
