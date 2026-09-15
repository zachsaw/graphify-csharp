using System.Buffers.Binary;
using System.Text.Json;

namespace Graphify.CSharp.Incremental;

internal sealed class SemanticQueryServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Guid _sessionId;
    private readonly string _analysisKey;
    private readonly Func<SemanticQuerySpec, CancellationToken, Task<SemanticQueryResponse>> _query;
    private readonly Func<string, CancellationToken, Task<SemanticQueryResponse>> _export;
    private readonly Func<bool, CancellationToken, Task<SemanticQueryResponse>>? _refresh;
    private readonly TimeSpan _transportTimeout;
    private readonly CancellationTokenSource _acceptStop = new();
    private readonly CancellationTokenSource _forcedStop = new();
    private readonly SemaphoreSlim _requestSlots;
    private readonly TaskCompletionSource<bool> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly HashSet<Task> _handlers = [];
    private readonly HashSet<Stream> _connections = [];
    private Task? _acceptTask;
    private Task? _disposeTask;
    private LocalIpcListener? _acceptingListener;
    private bool _disposed;

    public SemanticQueryServer(
        string pipeName,
        Guid sessionId,
        RefreshRequestIdentity analysis,
        Func<SemanticQuerySpec, CancellationToken, Task<SemanticQueryResponse>> query,
        Func<string, CancellationToken, Task<SemanticQueryResponse>> export,
        Func<bool, CancellationToken, Task<SemanticQueryResponse>>? refresh = null,
        int maximumConcurrentRequests = SemanticQueryProtocol.MaximumConcurrentRequests,
        TimeSpan? transportTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A semantic query server requires a session ID.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(analysis);
        _pipeName = pipeName;
        _sessionId = sessionId;
        _analysisKey = analysis.CanonicalKey;
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _refresh = refresh;
        if (maximumConcurrentRequests is < 1 or > SemanticQueryProtocol.MaximumConcurrentRequests)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentRequests),
                $"The semantic server capacity must be between 1 and {SemanticQueryProtocol.MaximumConcurrentRequests}.");
        }

        _requestSlots = new SemaphoreSlim(maximumConcurrentRequests, maximumConcurrentRequests);
        _transportTimeout = transportTimeout ?? TimeSpan.FromMilliseconds(
            SemanticQueryProtocol.TransportTimeoutMilliseconds);
        if (_transportTimeout <= TimeSpan.Zero
            || _transportTimeout > TimeSpan.FromMilliseconds(SemanticQueryProtocol.MaximumTimeoutMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportTimeout),
                "The semantic transport timeout must be positive and no greater than one hour.");
        }
    }

    public string PipeName => _pipeName;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task listening;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _acceptTask ??= RunAsync();
            listening = _listening.Task;
        }

        return cancellationToken.CanBeCanceled
            ? listening.WaitAsync(cancellationToken)
            : listening;
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _acceptStop.Cancel();
            _acceptingListener?.Dispose();
            foreach (var connection in _connections)
            {
                connection.Dispose();
            }
        }

        Task? acceptTask;
        lock (_gate)
        {
            acceptTask = _acceptTask;
        }

        await ObserveAsync(acceptTask).ConfigureAwait(false);
        Task[] handlers;
        lock (_gate)
        {
            handlers = _handlers.ToArray();
        }

        if (handlers.Length > 0)
        {
            _forcedStop.Cancel();
            AbortConnections();
            await ObserveAsync(Task.WhenAll(handlers)).ConfigureAwait(false);
        }

        _acceptStop.Dispose();
        _forcedStop.Dispose();
        _requestSlots.Dispose();
    }

    private async Task RunAsync()
    {
        LocalIpcListener? listener = null;
        try
        {
            listener = LocalIpcTransport.CreateListener(_pipeName);
            lock (_gate)
            {
                _acceptingListener = listener;
            }

            _listening.TrySetResult(true);
            while (!_acceptStop.IsCancellationRequested)
            {
                Stream server;
                try
                {
                    server = await listener.AcceptAsync(_acceptStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (_acceptStop.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException) when (!_acceptStop.IsCancellationRequested)
                {
                    continue;
                }

                if (_acceptStop.IsCancellationRequested)
                {
                    server.Dispose();
                    continue;
                }

                if (!_requestSlots.Wait(0))
                {
                    await TryWriteAsync(
                            server,
                            SemanticQueryResponse.Failure(
                                _sessionId,
                                "instance",
                                "unknown",
                                "server_busy",
                                "The semantic query endpoint is busy; retry the request."),
                            _forcedStop.Token)
                        .ConfigureAwait(false);
                    server.Dispose();
                    continue;
                }

                lock (_gate)
                {
                    _connections.Add(server);
                }

                var handler = HandleConnectionSlotAsync(server);
                lock (_gate)
                {
                    _handlers.Add(handler);
                }

                _ = RemoveCompletedHandlerAsync(handler);
            }
        }
        catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_acceptStop.IsCancellationRequested)
        {
        }
        catch (IOException) when (_acceptStop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!_acceptStop.IsCancellationRequested)
        {
            _listening.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_acceptingListener, listener))
                {
                    _acceptingListener = null;
                }
            }

            listener?.Dispose();
            _listening.TrySetCanceled(_acceptStop.Token);
        }
    }

    private async Task HandleConnectionSlotAsync(Stream server)
    {
        try
        {
            await ProcessConnectionAsync(server, _forcedStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_forcedStop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(server);
            }

            server.Dispose();
            try
            {
                _requestSlots.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task ProcessConnectionAsync(
        Stream server,
        CancellationToken serverCancellationToken)
    {
        byte[] payload;
        using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken))
        {
            readTimeout.CancelAfter(_transportTimeout);
            try
            {
                payload = await ReadFrameAsync(server, readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!serverCancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidDataException exception)
            {
                await TryWriteAsync(
                        server,
                        SemanticQueryResponse.Failure(
                            _sessionId,
                            "instance",
                            "unknown",
                            "invalid_request",
                            exception.Message),
                        serverCancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        SemanticQueryWireRequest request;
        try
        {
            request = SemanticQueryJsonParser.Parse(payload);
        }
        catch (SemanticQueryException exception)
        {
            await TryWriteAsync(
                    server,
                    SemanticQueryResponse.Failure(
                        _sessionId,
                        "instance",
                        "unknown",
                        exception.Code,
                        exception.Message),
                    serverCancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var command = request.Command;
        if (request.ProtocolVersion != SemanticQueryProtocol.CurrentVersion)
        {
            await TryWriteAsync(
                    server,
                    SemanticQueryResponse.Failure(
                        request.SessionId,
                        "instance",
                        command,
                        "incompatible_protocol",
                        $"Semantic protocol version {request.ProtocolVersion} is not supported."),
                    serverCancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.SessionId != _sessionId)
        {
            await TryWriteAsync(
                    server,
                    SemanticQueryResponse.Failure(
                        request.SessionId,
                        "instance",
                        command,
                        "session_mismatch",
                        "The request is addressed to a different semantic session."),
                    serverCancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.AnalysisKey, _analysisKey, StringComparison.Ordinal))
        {
            await TryWriteAsync(
                    server,
                    SemanticQueryResponse.Failure(
                        _sessionId,
                        "instance",
                        command,
                        "session_mismatch",
                        "The request does not match this session's analysis configuration."),
                    serverCancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        requestTimeout.CancelAfter(request.TimeoutMilliseconds);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestTimeout.Token);
        var disconnectSignal = new TaskCompletionSource<DisconnectSignal>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = MonitorDisconnectAsync(
            server,
            operationCancellation,
            disconnectSignal);
        SemanticQueryResponse response;
        try
        {
            response = command switch
            {
                "export" => await _export(
                        request.Spec.OutputPath!,
                        operationCancellation.Token)
                    .ConfigureAwait(false),
                "refresh" when _refresh is not null => await _refresh(
                        request.Spec.Rebuild,
                        operationCancellation.Token)
                    .ConfigureAwait(false),
                "refresh" => SemanticQueryResponse.Failure(
                    _sessionId,
                    "instance",
                    command,
                    "unsupported_capability",
                    "The semantic endpoint does not support refresh requests."),
                _ => await _query(request.Spec, operationCancellation.Token).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            var disconnectReason = disconnectSignal.Task.Status == TaskStatus.RanToCompletion
                ? disconnectSignal.Task.Result
                : DisconnectSignal.None;
            response = SemanticQueryResponse.Failure(
                _sessionId,
                "instance",
                command,
                disconnectReason == DisconnectSignal.ExtraBytes
                    ? "invalid_request"
                    : serverCancellationToken.IsCancellationRequested ? "cancelled" : "timeout",
                disconnectReason == DisconnectSignal.ExtraBytes
                    ? "A semantic connection may contain only one request frame."
                    : serverCancellationToken.IsCancellationRequested
                        ? "The semantic request was cancelled."
                        : "The semantic request exceeded its deadline.");
        }
        catch (SemanticQueryException exception)
        {
            response = SemanticQueryResponse.Failure(
                _sessionId,
                "instance",
                command,
                exception.Code,
                exception.Message);
        }
        catch (NotSupportedException exception)
        {
            response = SemanticQueryResponse.Failure(
                _sessionId,
                "instance",
                command,
                "unsupported_capability",
                exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = SemanticQueryResponse.Failure(
                _sessionId,
                "instance",
                command,
                "internal_error",
                exception.Message);
        }
        finally
        {
            operationCancellation.Cancel();
            await ObserveAsync(monitor).ConfigureAwait(false);
        }

        await TryWriteAsync(server, response, serverCancellationToken).ConfigureAwait(false);
    }

    private static async Task MonitorDisconnectAsync(
        Stream stream,
        CancellationTokenSource operationCancellation,
        TaskCompletionSource<DisconnectSignal> signal)
    {
        var buffer = new byte[1];
        try
        {
            var read = await stream.ReadAsync(buffer, operationCancellation.Token).ConfigureAwait(false);
            if (read == 0)
            {
                signal.TrySetResult(DisconnectSignal.Closed);
                operationCancellation.Cancel();
            }
            else
            {
                // Extra client bytes are rejected by the one-request framing
                // rule. Cancellation causes the pending operation to stop and
                // the caller reports the protocol error rather than swallowing
                // it as an ordinary handler exception.
                signal.TrySetResult(DisconnectSignal.ExtraBytes);
                operationCancellation.Cancel();
            }
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            signal.TrySetResult(DisconnectSignal.Closed);
            operationCancellation.Cancel();
        }
    }

    private enum DisconnectSignal
    {
        None,
        Closed,
        ExtraBytes,
    }

    private async Task RemoveCompletedHandlerAsync(Task handler)
    {
        try
        {
            await handler.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                _handlers.Remove(handler);
            }
        }
    }

    private void AbortConnections()
    {
        Stream[] connections;
        lock (_gate)
        {
            connections = _connections.ToArray();
        }

        foreach (var connection in connections)
        {
            try
            {
                connection.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > SemanticQueryProtocol.MaximumResponseBytes)
        {
            throw new InvalidDataException("The semantic response frame is outside the supported size limit.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken,
        int maximumPayloadBytes = SemanticQueryProtocol.MaximumRequestBytes)
    {
        var header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maximumPayloadBytes)
        {
            throw new InvalidDataException("The semantic request frame is outside the supported size limit.");
        }

        var payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private async Task TryWriteAsync(
        Stream stream,
        SemanticQueryResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(response, SemanticQueryJson.SerializerOptions);
            if (payload.Length > SemanticQueryProtocol.MaximumResponseBytes)
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(
                    SemanticQueryResponse.Failure(
                        response.SessionId,
                        response.Mode,
                        response.Command,
                        "response_too_large",
                        "The semantic response exceeds the 4 MiB response limit."),
                    SemanticQueryJson.SerializerOptions);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_transportTimeout);
            await WriteFrameAsync(stream, payload, timeout.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
