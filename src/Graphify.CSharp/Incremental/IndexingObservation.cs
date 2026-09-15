using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Graphify.CSharp.Incremental;

internal static class IndexingStages
{
    public const string Initializing = "initializing";
    public const string Inventory = "inventory";
    public const string LoadingProjects = "loading_projects";
    public const string Compiling = "compiling";
    public const string DiscoveringInputs = "discovering_inputs";
    public const string Cataloging = "cataloging";
    public const string ExtractingRelationships = "extracting_relationships";
    public const string ExtractingReferences = "extracting_references";
    public const string MergingEvidence = "merging_evidence";
    public const string Fingerprinting = "fingerprinting";
    public const string BuildingQueryIndex = "building_query_index";
    public const string Serializing = "serializing";
    public const string WritingCache = "writing_cache";
    public const string ReconcilingChanges = "reconciling_changes";
}

internal sealed class IndexingObservation : IAsyncDisposable
{
    private const int RecentEventCapacity = 32;
    private const int MaximumMessageLength = 512;
    private readonly object _gate = new();
    private readonly IProcessResourceSampler _resourceSampler;
    private readonly IObservationClock _clock;
    private readonly CancellationTokenSource _resourceStop = new();
    private readonly TaskCompletionSource<bool> _resourceStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _createdTimestamp;
    private readonly Queue<ObservationEvent> _recentEvents = new();
    private ActiveOperation? _activeOperation;
    private ObservationOperationSummary? _initialStartup;
    private ObservationOperationSummary? _lastOperation;
    private ObservationEvidenceSummary? _evidence;
    private ObservationResourceSnapshot? _resource;
    private ObservationEvent? _lastFailure;
    private RecoveryObservationSummary _recovery = new(0, 0, 0, null, null);
    private bool _startupPending;
    private string? _startupDetail;
    private long _recentEventsOmitted;
    private long _truncatedMessages;
    private Task? _resourceTask;
    private int _resourceSamplerDisposalDeferred;
    private long _nextOperationId;
    private int _disposed;

    public IndexingObservation(
        IProcessResourceSampler? resourceSampler = null,
        IObservationClock? clock = null)
    {
        _resourceSampler = resourceSampler ?? new ProcessResourceSampler();
        _clock = clock ?? new SystemObservationClock();
        _createdTimestamp = _clock.Timestamp;
    }

    internal static ObservationRuntimeSnapshot CreateRuntimeSnapshot() =>
        new(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            global::Graphify.CSharp.Roslyn.ExtractionParallelismOptions.Default.MaxDegreeOfParallelism);

    public void StartResourceSampling(TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_resourceTask is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var sampleInterval = interval ?? TimeSpan.FromSeconds(5);
            if (sampleInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }

            _resourceTask = ResourceLoopAsync(sampleInterval);
        }
    }

    public IndexingObservationOperation BeginOperation(string kind, int attempt = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_activeOperation is not null)
            {
                throw new InvalidOperationException(
                    $"Observation operation '{_activeOperation.Kind}' is already active.");
            }

            var operation = new ActiveOperation(
                checked(++_nextOperationId),
                kind.Trim(),
                attempt,
                _clock.Timestamp,
                _clock.UtcNow);
            _activeOperation = operation;
            AddEventLocked("operation_started", $"{operation.Kind} operation started.", operation.Id);
            return new IndexingObservationOperation(this, operation.Id);
        }
    }

    public IndexingObservationSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _clock.Timestamp;
            var activity = _activeOperation is null
                ? null
                : CreateActivity(_activeOperation, now);
            return new IndexingObservationSnapshot(
                _clock.UtcNow,
                ElapsedMilliseconds(_createdTimestamp, now),
                activity,
                _evidence,
                _resource,
                _initialStartup,
                _lastOperation,
                _recovery,
                _recentEvents.ToArray(),
                _lastFailure,
                _recentEventsOmitted,
                _truncatedMessages,
                _startupPending,
                _startupDetail);
        }
    }

    internal void SetStartupPending(bool pending, string? detail = null)
    {
        if (!pending)
        {
            CompleteStartup("succeeded");
            return;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var normalizedDetail = !string.IsNullOrWhiteSpace(detail)
                ? Truncate(detail)
                : null;
            if (_startupPending
                && string.Equals(_startupDetail, normalizedDetail, StringComparison.Ordinal))
            {
                return;
            }

            _startupPending = true;
            _startupDetail = normalizedDetail;
            AddEventLocked(
                "startup_pending",
                normalizedDetail is null ? "Watcher startup is still pending." : normalizedDetail,
                null);
        }
    }

    internal void CompleteStartup(string outcome, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        var normalizedOutcome = outcome.Trim();
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_startupPending)
            {
                return;
            }

            _startupPending = false;
            _startupDetail = null;
            var completed = AddEventLocked(
                "startup_" + normalizedOutcome,
                detail ?? $"Watcher startup {normalizedOutcome}.",
                null);
            if (!string.Equals(normalizedOutcome, "succeeded", StringComparison.Ordinal))
            {
                _lastFailure = completed;
            }
        }
    }

    internal void ReportStage(long operationId, string stage, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0
                || _activeOperation is null
                || _activeOperation.Id != operationId)
            {
                return;
            }

            // A direct stage transition belongs to the operation owner and
            // closes any callback phase that was previously handed out.
            _activeOperation.CurrentPhaseId = 0;
            SetStageLocked(_activeOperation, stage, detail);
        }
    }

    internal void ReportWork(long operationId, long completed, long? total, string unit, string? detail = null)
    {
        if (completed < 0 || total is < 0 || (total is not null && completed > total))
        {
            throw new ArgumentOutOfRangeException(nameof(completed));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0
                || _activeOperation is null
                || _activeOperation.Id != operationId)
            {
                return;
            }

            ApplyWorkLocked(_activeOperation, completed, total, unit, detail);
        }
    }

    internal IndexingObservationPhase? BeginPhase(
        long operationId,
        string stage,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0
                || _activeOperation is null
                || _activeOperation.Id != operationId)
            {
                return null;
            }

            var phaseId = checked(++_activeOperation.NextPhaseId);
            _activeOperation.CurrentPhaseId = phaseId;
            SetStageLocked(_activeOperation, stage, detail, forceNewOccurrence: true);
            return new IndexingObservationPhase(this, operationId, phaseId);
        }
    }

    internal void ReportPhaseStage(
        long operationId,
        long phaseId,
        string stage,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            if (!IsCurrentPhaseLocked(operationId, phaseId))
            {
                return;
            }

            SetStageLocked(_activeOperation!, stage, detail);
        }
    }

    internal void ReportPhaseWork(
        long operationId,
        long phaseId,
        long completed,
        long? total,
        string unit,
        string? detail = null)
    {
        if (completed < 0 || total is < 0 || (total is not null && completed > total))
        {
            throw new ArgumentOutOfRangeException(nameof(completed));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        lock (_gate)
        {
            if (!IsCurrentPhaseLocked(operationId, phaseId))
            {
                return;
            }

            ApplyWorkLocked(_activeOperation!, completed, total, unit, detail);
        }
    }

    internal void EndPhase(long operationId, long phaseId)
    {
        lock (_gate)
        {
            if (IsCurrentPhaseLocked(operationId, phaseId))
            {
                _activeOperation!.CurrentPhaseId = 0;
            }
        }
    }

    internal void CompleteOperation(long operationId, string outcome, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0
                || _activeOperation is null
                || _activeOperation.Id != operationId)
            {
                return;
            }

            var operation = _activeOperation;
            var now = _clock.Timestamp;
            if (operation.Stage is not null)
            {
                operation.StageDurations[operation.Stage] =
                    operation.StageDurations.GetValueOrDefault(operation.Stage)
                    + ElapsedMilliseconds(operation.StageTimestamp, now);
            }
            var summary = new ObservationOperationSummary(
                operation.Id,
                operation.Kind,
                operation.Attempt,
                outcome,
                operation.StartedAtUtc,
                ElapsedMilliseconds(operation.StartedTimestamp, now),
                operation.StageDurations
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                _clock.UtcNow,
                message is null ? null : Truncate(message));
            if (string.Equals(operation.Kind, "startup", StringComparison.Ordinal)
                && _initialStartup is null)
            {
                _initialStartup = summary;
            }

            _lastOperation = summary;
            if (string.Equals(outcome, "failed", StringComparison.Ordinal)
                || string.Equals(outcome, "cancelled", StringComparison.Ordinal))
            {
                _lastFailure = AddEventLocked("operation_failed", message ?? $"{operation.Kind} operation {outcome}.", operation.Id);
            }
            else
            {
                AddEventLocked("operation_completed", $"{operation.Kind} operation {outcome}.", operation.Id);
            }

            operation.CurrentPhaseId = 0;
            _activeOperation = null;
        }
    }

    internal void PublishEvidence(ObservationEvidenceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _evidence = summary;
        }
    }

    internal void RecordRecovery(string outcome, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var attempts = checked(_recovery.Attempts + 1);
            var successes = _recovery.Successes + (string.Equals(outcome, "succeeded", StringComparison.Ordinal) ? 1 : 0);
            var failures = _recovery.Failures + (string.Equals(outcome, "failed", StringComparison.Ordinal) ? 1 : 0);
            _recovery = new RecoveryObservationSummary(
                attempts,
                successes,
                failures,
                _clock.UtcNow,
                reason is null ? null : Truncate(reason));
            AddEventLocked("recovery_" + outcome, reason ?? $"Watcher recovery {outcome}.", null);
        }
    }

    internal void RecordEvent(string kind, string message, long? operationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            AddEventLocked(kind, message, operationId);
        }
    }

    private void SetStageLocked(
        ActiveOperation operation,
        string stage,
        string? detail,
        bool forceNewOccurrence = false)
    {
        var normalized = stage.Trim();
        if (forceNewOccurrence
            || !string.Equals(operation.Stage, normalized, StringComparison.Ordinal))
        {
            var now = _clock.Timestamp;
            if (operation.Stage is not null)
            {
                operation.StageDurations[operation.Stage] =
                    operation.StageDurations.GetValueOrDefault(operation.Stage)
                    + ElapsedMilliseconds(operation.StageTimestamp, now);
            }

            operation.Stage = normalized;
            operation.StageTimestamp = now;
            operation.StageAtUtc = _clock.UtcNow;
            operation.Completed = null;
            operation.Total = null;
            operation.Unit = null;
            operation.Detail = detail is null ? null : Truncate(detail);
            operation.LastWorkTimestamp = 0;
            operation.LastWorkAtUtc = default;
            operation.HasWork = false;
            AddEventLocked("stage", detail is null ? normalized : $"{normalized}: {detail}", operation.Id);
        }
        else if (detail is not null)
        {
            operation.Detail = Truncate(detail);
        }
    }

    private bool IsCurrentPhaseLocked(long operationId, long phaseId) =>
        Volatile.Read(ref _disposed) == 0
        && _activeOperation is not null
        && _activeOperation.Id == operationId
        && _activeOperation.CurrentPhaseId == phaseId;

    private void ApplyWorkLocked(
        ActiveOperation operation,
        long completed,
        long? total,
        string unit,
        string? detail)
    {
        var sameUnit = string.Equals(operation.Unit, unit, StringComparison.Ordinal);
        if (sameUnit
            && operation.HasWork
            && operation.Completed is { } previous
            && completed < previous)
        {
            // Parallel callbacks can arrive out of order. Never publish a
            // smaller aggregate for one phase occurrence.
            return;
        }

        operation.Completed = sameUnit && operation.Completed is { } prior
            ? Math.Max(prior, completed)
            : completed;
        operation.Total = total;
        operation.Unit = unit;
        if (detail is not null)
        {
            operation.Detail = Truncate(detail);
        }

        // A zero-at-entry notification describes the denominator, not work
        // that has completed. The first positive completion establishes the
        // real last-work timestamp.
        if (completed > 0 || operation.HasWork)
        {
            operation.LastWorkTimestamp = _clock.Timestamp;
            operation.LastWorkAtUtc = _clock.UtcNow;
            operation.HasWork = true;
        }
    }

    private ObservationEvent AddEventLocked(string kind, string message, long? operationId)
    {
        var item = new ObservationEvent(
            _clock.UtcNow,
            kind,
            Truncate(message),
            operationId);
        if (_recentEvents.Count == RecentEventCapacity)
        {
            _recentEvents.Dequeue();
            _recentEventsOmitted = checked(_recentEventsOmitted + 1);
        }

        _recentEvents.Enqueue(item);
        return item;
    }

    private ObservationActivity CreateActivity(ActiveOperation operation, long now)
    {
        var stageTimestamp = operation.StageTimestamp == 0 ? operation.StartedTimestamp : operation.StageTimestamp;
        return new ObservationActivity(
            operation.Id,
            operation.Kind,
            operation.Attempt,
            operation.Stage ?? IndexingStages.Initializing,
            ElapsedMilliseconds(operation.StartedTimestamp, now),
            ElapsedMilliseconds(stageTimestamp, now),
            operation.Completed,
            operation.Total,
            operation.Unit,
            operation.Detail,
            operation.HasWork
                ? ElapsedMilliseconds(operation.LastWorkTimestamp, now)
                : null,
            operation.StartedAtUtc,
            operation.StageAtUtc == default ? null : operation.StageAtUtc,
            operation.HasWork ? operation.LastWorkAtUtc : null);
    }

    private async Task ResourceLoopAsync(TimeSpan interval)
    {
        try
        {
            // Do not execute an injected or platform resource provider inline
            // on StartResourceSampling's caller. A slow provider must never
            // hold the observation lock or delay startup/management commands.
            await Task.Yield();
            while (!_resourceStop.IsCancellationRequested)
            {
                var capture = Task.Run(_resourceSampler.Capture);
                var completed = await Task.WhenAny(capture, _resourceStopped.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completed, capture))
                {
                    // A provider is synchronous by contract and cannot be
                    // force-cancelled safely. Do not make process shutdown wait
                    // for it; observe the task so a late provider exception is
                    // not unobserved, and dispose an owned provider only after
                    // the call has returned.
                    if (capture.IsCompleted)
                    {
                        await ObserveCaptureAsync(capture, disposeAfter: null).ConfigureAwait(false);
                    }
                    else
                    {
                        if (_resourceSampler is IDisposable)
                        {
                            Interlocked.Exchange(ref _resourceSamplerDisposalDeferred, 1);
                        }

                        _ = ObserveDetachedCaptureAsync(capture, _resourceSampler as IDisposable);
                    }

                    return;
                }

                try
                {
                    var sample = await capture.ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _disposed) == 0)
                        {
                            _resource = sample;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    RecordEvent("resource_sample_unavailable", exception.Message);
                }

                await Task.Delay(interval, _resourceStop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_resourceStop.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveDetachedCaptureAsync(
        Task<ObservationResourceSnapshot> capture,
        IDisposable? disposeAfter)
    {
        await ObserveCaptureAsync(capture, disposeAfter).ConfigureAwait(false);
    }

    private static async Task ObserveCaptureAsync(
        Task<ObservationResourceSnapshot> capture,
        IDisposable? disposeAfter)
    {
        try
        {
            await capture.ConfigureAwait(false);
        }
        catch
        {
            // A detached sample cannot affect the already-disposed observer,
            // but its exception must still be observed.
        }
        finally
        {
            try
            {
                disposeAfter?.Dispose();
            }
            catch
            {
                // Resource sampling is best effort and must not surface a
                // late disposal failure on the thread-pool continuation.
            }
        }
    }

    private static long ElapsedMilliseconds(long start, long end) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(start, end).TotalMilliseconds);

    private string Truncate(string value)
    {
        if (value.Length <= MaximumMessageLength)
        {
            return value;
        }

        _truncatedMessages = checked(_truncatedMessages + 1);
        return value[..MaximumMessageLength];
    }

    private void ThrowIfDisposedLocked()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(IndexingObservation));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _resourceStop.Cancel();
        _resourceStopped.TrySetResult(true);
        Task? resourceTask;
        lock (_gate)
        {
            _activeOperation = null;
            resourceTask = _resourceTask;
        }

        if (resourceTask is not null)
        {
            await resourceTask.ConfigureAwait(false);
        }

        _resourceStop.Dispose();
        if (Volatile.Read(ref _resourceSamplerDisposalDeferred) == 0
            && _resourceSampler is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class ActiveOperation
    {
        public ActiveOperation(long id, string kind, int attempt, long startedTimestamp, DateTimeOffset startedAtUtc)
        {
            Id = id;
            Kind = kind;
            Attempt = attempt;
            StartedTimestamp = startedTimestamp;
            StartedAtUtc = startedAtUtc;
        }

        public long Id { get; }
        public string Kind { get; }
        public int Attempt { get; }
        public long StartedTimestamp { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public long NextPhaseId { get; set; }
        public long CurrentPhaseId { get; set; }
        public string? Stage { get; set; }
        public long StageTimestamp { get; set; }
        public DateTimeOffset StageAtUtc { get; set; }
        public long? Completed { get; set; }
        public long? Total { get; set; }
        public string? Unit { get; set; }
        public string? Detail { get; set; }
        public long LastWorkTimestamp { get; set; }
        public DateTimeOffset LastWorkAtUtc { get; set; }
        public bool HasWork { get; set; }
        public Dictionary<string, long> StageDurations { get; } = new(StringComparer.Ordinal);
    }
}

internal sealed class IndexingObservationOperation : IDisposable
{
    private readonly IndexingObservation _owner;
    private readonly long _id;
    private int _completed;

    internal IndexingObservationOperation(IndexingObservation owner, long id)
    {
        _owner = owner;
        _id = id;
    }

    public long Id => _id;

    public void SetStage(string stage, string? detail = null) => _owner.ReportStage(_id, stage, detail);

    public IndexingObservationPhase? BeginPhase(string stage, string? detail = null) =>
        _owner.BeginPhase(_id, stage, detail);

    public void ReportWork(long completed, long? total, string unit, string? detail = null) =>
        _owner.ReportWork(_id, completed, total, unit, detail);

    public void Complete(string outcome = "succeeded", string? message = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _owner.CompleteOperation(_id, outcome, message);
        }
    }

    public void Dispose() => Complete("cancelled");
}

internal sealed class IndexingObservationPhase : IDisposable
{
    private readonly IndexingObservation _owner;
    private readonly long _operationId;
    private readonly long _phaseId;
    private int _disposed;

    internal IndexingObservationPhase(IndexingObservation owner, long operationId, long phaseId)
    {
        _owner = owner;
        _operationId = operationId;
        _phaseId = phaseId;
    }

    public void SetStage(string stage, string? detail = null)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _owner.ReportPhaseStage(_operationId, _phaseId, stage, detail);
        }
    }

    public void ReportWork(long completed, long? total, string unit, string? detail = null)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _owner.ReportPhaseWork(_operationId, _phaseId, completed, total, unit, detail);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.EndPhase(_operationId, _phaseId);
        }
    }
}

internal sealed record ObservationActivity(
    [property: JsonPropertyName("operation_id")] long OperationId,
    [property: JsonPropertyName("operation_kind")] string OperationKind,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("operation_elapsed_ms")] long OperationElapsedMilliseconds,
    [property: JsonPropertyName("stage_elapsed_ms")] long StageElapsedMilliseconds,
    [property: JsonPropertyName("completed")] long? Completed,
    [property: JsonPropertyName("total")] long? Total,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("last_work_age_ms")] long? LastWorkAgeMilliseconds,
    [property: JsonPropertyName("started_at_utc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("stage_started_at_utc")] DateTimeOffset? StageStartedAtUtc = null,
    [property: JsonPropertyName("last_work_at_utc")] DateTimeOffset? LastWorkAtUtc = null);

internal sealed record ObservationOperationSummary(
    [property: JsonPropertyName("operation_id")] long OperationId,
    [property: JsonPropertyName("operation_kind")] string OperationKind,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("started_at_utc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMilliseconds,
    [property: JsonPropertyName("stage_durations_ms")] IReadOnlyDictionary<string, long> StageDurationsMilliseconds,
    [property: JsonPropertyName("completed_at_utc")] DateTimeOffset CompletedAtUtc,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record ObservationEvidenceSummary(
    [property: JsonPropertyName("evidence_revision")] long EvidenceRevision,
    [property: JsonPropertyName("indexed_generation")] long IndexedGeneration,
    [property: JsonPropertyName("projects")] long Projects,
    [property: JsonPropertyName("document_instances")] long DocumentInstances,
    [property: JsonPropertyName("compilation_syntax_trees")] long CompilationSyntaxTrees,
    [property: JsonPropertyName("source_generated_documents")] long? SourceGeneratedDocuments,
    [property: JsonPropertyName("declarations")] long Declarations,
    [property: JsonPropertyName("contribution_nodes")] long ContributionNodes,
    [property: JsonPropertyName("contribution_edges")] long ContributionEdges,
    [property: JsonPropertyName("semantic_index_built")] bool SemanticIndexBuilt,
    [property: JsonPropertyName("semantic_index_edges")] long? SemanticIndexEdges,
    [property: JsonPropertyName("query_caches_built")] IReadOnlyList<string> QueryCachesBuilt,
    [property: JsonPropertyName("extracted_projects")] long ExtractedProjects,
    [property: JsonPropertyName("reused_projects")] long ReusedProjects,
    [property: JsonPropertyName("observed_at_utc")] DateTimeOffset ObservedAtUtc);

internal sealed record ObservationResourceSnapshot(
    [property: JsonPropertyName("sampled_at_utc")] DateTimeOffset SampledAtUtc,
    [property: JsonPropertyName("working_set_bytes")] long? WorkingSetBytes,
    [property: JsonPropertyName("peak_working_set_bytes")] long? PeakWorkingSetBytes,
    [property: JsonPropertyName("managed_heap_estimate_bytes")] long? ManagedHeapEstimateBytes,
    [property: JsonPropertyName("gc_heap_size_bytes_at_last_gc")] long? GcHeapSizeBytesAtLastGc,
    [property: JsonPropertyName("gc_committed_bytes_at_last_gc")] long? GcCommittedBytesAtLastGc,
    [property: JsonPropertyName("gc_fragmented_bytes_at_last_gc")] long? GcFragmentedBytesAtLastGc,
    [property: JsonPropertyName("gc_index")] long? GcIndex,
    [property: JsonPropertyName("process_cpu_time_ms")] long? ProcessCpuTimeMilliseconds);

internal sealed record RecoveryObservationSummary(
    [property: JsonPropertyName("attempts")] long Attempts,
    [property: JsonPropertyName("successes")] long Successes,
    [property: JsonPropertyName("failures")] long Failures,
    [property: JsonPropertyName("last_at_utc")] DateTimeOffset? LastAtUtc,
    [property: JsonPropertyName("last_reason")] string? LastReason);

internal sealed record ObservationEvent(
    [property: JsonPropertyName("at_utc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("operation_id")] long? OperationId);

internal sealed record IndexingObservationSnapshot(
    [property: JsonPropertyName("observed_at_utc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("uptime_ms")] long UptimeMilliseconds,
    [property: JsonPropertyName("activity")] ObservationActivity? Activity,
    [property: JsonPropertyName("evidence")] ObservationEvidenceSummary? Evidence,
    [property: JsonPropertyName("resources")] ObservationResourceSnapshot? Resources,
    [property: JsonPropertyName("initial_startup")] ObservationOperationSummary? InitialStartup,
    [property: JsonPropertyName("last_operation")] ObservationOperationSummary? LastOperation,
    [property: JsonPropertyName("recovery")] RecoveryObservationSummary Recovery,
    [property: JsonPropertyName("recent_events")] IReadOnlyList<ObservationEvent> RecentEvents,
    [property: JsonPropertyName("last_failure")] ObservationEvent? LastFailure,
    [property: JsonPropertyName("recent_events_omitted")] long RecentEventsOmitted = 0,
    [property: JsonPropertyName("messages_truncated")] long MessagesTruncated = 0,
    [property: JsonPropertyName("startup_pending")] bool StartupPending = false,
    [property: JsonPropertyName("startup_detail")] string? StartupDetail = null,
    [property: JsonPropertyName("omitted_fields")] IReadOnlyList<string>? OmittedFields = null);

internal sealed record ObservationRuntimeSnapshot(
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("os_description")] string OsDescription,
    [property: JsonPropertyName("process_architecture")] string ProcessArchitecture,
    [property: JsonPropertyName("effective_processor_count")] int EffectiveProcessorCount,
    [property: JsonPropertyName("server_gc")] bool ServerGc,
    [property: JsonPropertyName("extraction_max_degree_of_parallelism")] int ExtractionMaxDegreeOfParallelism);

internal interface IProcessResourceSampler
{
    ObservationResourceSnapshot Capture();
}

internal sealed class ProcessResourceSampler : IProcessResourceSampler, IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private int _disposed;

    public ObservationResourceSnapshot Capture()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ProcessResourceSampler));
        }

        _process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return new ObservationResourceSnapshot(
            DateTimeOffset.UtcNow,
            Read(() => _process.WorkingSet64),
            ReadPositive(() => _process.PeakWorkingSet64),
            Read(() => GC.GetTotalMemory(false)),
            gc.Index == 0 ? null : gc.HeapSizeBytes,
            gc.Index == 0 ? null : gc.TotalCommittedBytes,
            gc.Index == 0 ? null : gc.FragmentedBytes,
            gc.Index == 0 ? null : gc.Index,
            Read(() => checked((long)_process.TotalProcessorTime.TotalMilliseconds)));
    }

    private static long? Read(Func<long> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadPositive(Func<long> read)
    {
        var value = Read(read);
        return value is > 0 ? value : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _process.Dispose();
        }
    }
}

internal interface IObservationClock
{
    long Timestamp { get; }

    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemObservationClock : IObservationClock
{
    public long Timestamp => Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
