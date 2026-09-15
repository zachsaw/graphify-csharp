using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Cli;

internal sealed class CliProgressReporter : IAsyncDisposable
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly IndexingObservation _observation;
    private readonly TextWriter _writer;
    private readonly Action? _beforeUpdateQueue;
    private readonly CancellationTokenSource _stop = new();
    private Task? _task;
    private Task? _disposeTask;
    private string? _pendingMessage;
    private string? _pendingSummary;
    private bool _startingPending;
    private bool _disabled;
    private bool _summaryRequested;
    private bool _summaryWritten;
    private int _resourcesDisposed;
    private string? _lastStage;
    private long? _lastOperation;
    private long? _lastCompleted;
    private bool _lastStartupPending;
    private string? _lastStartupDetail;
    private DateTimeOffset _lastWriteAt;

    internal CliProgressReporter(
        IndexingObservation observation,
        TextWriter? writer = null,
        Action? beforeUpdateQueue = null)
    {
        _observation = observation ?? throw new ArgumentNullException(nameof(observation));
        _writer = writer ?? Console.Error;
        _beforeUpdateQueue = beforeUpdateQueue;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_task is not null)
            {
                return;
            }

            // The renderer owns all TextWriter calls. This keeps a slow or
            // closed stderr stream off the indexing caller's stack while
            // still scheduling the starting notice before expensive work.
            _startingPending = true;
            _task = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            // Ensure Start returns before a custom writer can run synchronously.
            await Task.Yield();
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            TryWritePending();

            while (!_stop.IsCancellationRequested)
            {
                var snapshot = _observation.Snapshot();
                var activity = snapshot.Activity;
                if (activity is not null)
                {
                    _lastStartupPending = false;
                    _lastStartupDetail = null;
                    var stageChanged = !string.Equals(_lastStage, activity.Stage, StringComparison.Ordinal)
                        || _lastOperation != activity.OperationId;
                    var countChanged = _lastCompleted != activity.Completed;
                    var due = DateTimeOffset.UtcNow - _lastWriteAt >= HeartbeatInterval;
                    if (stageChanged || countChanged || due)
                    {
                        var progress = activity.Completed is null
                            ? string.Empty
                            : $" {activity.Completed}/{activity.Total?.ToString() ?? "?"} {activity.Unit}";
                        var detail = string.IsNullOrWhiteSpace(activity.Detail)
                            ? string.Empty
                            : $" ({activity.Detail})";
                        var heartbeat = !stageChanged && !countChanged && due
                            ? "; heartbeat"
                            : string.Empty;
                        var message =
                            $"[{FormatElapsed(activity.OperationElapsedMilliseconds)}] "
                            + $"{activity.OperationKind}/{activity.Stage}{progress}{detail}; "
                            + $"{FormatElapsed(activity.StageElapsedMilliseconds)} in stage{heartbeat}";
                        _beforeUpdateQueue?.Invoke();
                        QueueMessage(message);
                        _lastStage = activity.Stage;
                        _lastOperation = activity.OperationId;
                        _lastCompleted = activity.Completed;
                    }
                }
                else if (snapshot.StartupPending)
                {
                    var startupChanged = !_lastStartupPending
                        || !string.Equals(_lastStartupDetail, snapshot.StartupDetail, StringComparison.Ordinal);
                    var due = DateTimeOffset.UtcNow - _lastWriteAt >= HeartbeatInterval;
                    if (startupChanged || due)
                    {
                        var detail = string.IsNullOrWhiteSpace(snapshot.StartupDetail)
                            ? string.Empty
                            : $" ({snapshot.StartupDetail})";
                        QueueMessage($"Startup still pending{detail}; heartbeat");
                    }

                    _lastStartupPending = true;
                    _lastStartupDetail = snapshot.StartupDetail;
                }
                else
                {
                    _lastStartupPending = false;
                    _lastStartupDetail = null;
                }

                TryWritePending();
                await Task.Delay(RenderInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Progress is best effort. An unexpected renderer failure must not
            // turn a successful Roslyn operation into a failed command.
            DisableWriter();
        }
        finally
        {
            // A completion summary may have been queued just as cancellation
            // arrived. Drain the bounded priority slots. If a writer blocks,
            // DisposeAsync bounds the join and detaches this renderer task
            // without affecting the indexing lifetime.
            TryWritePending();
            TryWritePending();
        }
    }

    private void QueueMessage(string message)
    {
        lock (_gate)
        {
            if (!_disabled)
            {
                // One latest-message slot gives us bounded coalescing. The
                // observation snapshot remains the source of truth if a slow
                // writer drops intermediate presentation updates.
                _pendingMessage = message;
            }
        }
    }

    private void TryWritePending()
    {
        string? message;
        var kind = PendingMessageKind.None;
        lock (_gate)
        {
            if (_disabled)
            {
                _pendingMessage = null;
                _pendingSummary = null;
                _startingPending = false;
                return;
            }

            if (_startingPending)
            {
                message = "Starting; waiting for the indexer to begin.";
                _startingPending = false;
                kind = PendingMessageKind.Starting;
            }
            else if (_pendingSummary is not null)
            {
                message = _pendingSummary;
                _pendingSummary = null;
                kind = PendingMessageKind.Summary;
            }
            else
            {
                message = _pendingMessage;
                _pendingMessage = null;
                kind = message is null ? PendingMessageKind.None : PendingMessageKind.Update;
            }
        }

        if (message is null)
        {
            return;
        }

        try
        {
            _writer.WriteLine($"graphify-csharp: {message}");
            _writer.Flush();
            _lastWriteAt = DateTimeOffset.UtcNow;
            if (kind == PendingMessageKind.Summary)
            {
                lock (_gate)
                {
                    _summaryWritten = true;
                }
            }
        }
        catch (Exception)
        {
            if (kind == PendingMessageKind.Summary)
            {
                lock (_gate)
                {
                    _summaryWritten = true;
                }
            }

            DisableWriter();
        }
    }

    private void DisableWriter()
    {
        lock (_gate)
        {
            _disabled = true;
            _pendingMessage = null;
        }
    }

    public void WriteCompletionSummary(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var snapshot = _observation.Snapshot();
        var evidence = snapshot.Evidence;
        var evidenceText = evidence is null
            ? "evidence unavailable"
            : $"{evidence.Projects} projects, {evidence.DocumentInstances} document instances, {evidence.Declarations} declarations";
        var memoryText = snapshot.Resources?.WorkingSetBytes is { } workingSet
            ? $", RSS {FormatBytes(workingSet)}"
            : string.Empty;
        var operationText = snapshot.LastOperation is { } operation
            ? $"; {operation.OperationKind} {operation.Outcome} in {FormatElapsed(operation.ElapsedMilliseconds)}"
            : string.Empty;

        lock (_gate)
        {
            if (_summaryWritten)
            {
                return;
            }

            if (_summaryRequested)
            {
                return;
            }

            _summaryRequested = true;
            if (!_disabled)
            {
                _pendingSummary = $"{label}; {evidenceText}{memoryText}{operationText}";
            }
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.0} GiB"
            : $"{bytes / (1024d * 1024):0.0} MiB";

    private static string FormatElapsed(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? span.ToString(@"hh\:mm\:ss")
            : span.ToString(@"mm\:ss");
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var lastOperation = _observation.Snapshot().LastOperation;
        if (lastOperation?.Outcome is "failed" or "cancelled")
        {
            WriteCompletionSummary("Indexing did not complete");
        }

        _stop.Cancel();
        Task? task;
        lock (_gate)
        {
            task = _task;
        }

        if (task is null)
        {
            DisposeResources();
            return;
        }

        try
        {
            await task.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // TextWriter has no cancellation contract. Let the single writer
            // task finish on its own, but never make command shutdown wait for
            // an uncooperative terminal or test writer.
            _ = ObserveDetachedAsync(task);
            return;
        }

        DisposeResources();
    }

    private async Task ObserveDetachedAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // RunAsync already treats renderer errors as best effort; this
            // continuation only prevents an unobserved detached exception.
        }
        finally
        {
            DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _stop.Dispose();
        }
    }

    private enum PendingMessageKind
    {
        None,
        Starting,
        Summary,
        Update,
    }
}
