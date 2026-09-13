using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graphify.CSharp.Incremental;

internal sealed class WatcherManagementServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _pipeName;
    private readonly Guid _sessionId;
    private readonly IWatcherManagementHost _host;
    private readonly WatcherManagementOptions _options;
    private readonly CancellationTokenSource _acceptStop = new();
    private readonly CancellationTokenSource _forcedStop = new();
    private readonly SemaphoreSlim _requestSlots = new(WatcherManagementProtocol.MaximumConcurrentRequests);
    private readonly TaskCompletionSource<bool> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly HashSet<Task> _handlers = [];
    private readonly HashSet<NamedPipeServerStream> _connections = [];
    private Task? _acceptTask;
    private Task? _disposeTask;
    private NamedPipeServerStream? _acceptingServer;
    private bool _disposed;

    public WatcherManagementServer(
        Guid sessionId,
        IWatcherManagementHost host,
        WatcherManagementOptions options)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A management server requires a session ID.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        _sessionId = sessionId;
        _host = host;
        _options = options;
        _pipeName = WatcherManagementProtocol.ForSession(sessionId, options.StateDirectory);
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

    /// <summary>
    /// Stops accepting new management requests while allowing already accepted
    /// requests, especially a graceful stop request, to finish.
    /// </summary>
    public void BeginShutdown()
    {
        lock (_gate)
        {
            try
            {
                if (!_acceptStop.IsCancellationRequested)
                {
                    _acceptStop.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // A concurrent DisposeAsync already stopped the listener.
            }

            _acceptingServer?.Dispose();
            _acceptingServer = null;
        }
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
        }

        BeginShutdown();

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
            try
            {
                await Task.WhenAll(handlers).WaitAsync(_options.DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A client that stopped reading must not keep process shutdown
                // hostage. Abort only this server's accepted connections.
                _forcedStop.Cancel();
                AbortConnections();
                await ObserveAsync(Task.WhenAll(handlers)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_forcedStop.IsCancellationRequested)
            {
                AbortConnections();
                await ObserveAsync(Task.WhenAll(handlers)).ConfigureAwait(false);
            }
        }

        _acceptStop.Dispose();
        _forcedStop.Dispose();
        _requestSlots.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_acceptStop.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (Exception exception)
                {
                    _listening.TrySetException(exception);
                    return;
                }

                lock (_gate)
                {
                    _acceptingServer = server;
                }

                _listening.TrySetResult(true);

                try
                {
                    await server.WaitForConnectionAsync(_acceptStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested)
                {
                    server.Dispose();
                    return;
                }
                catch (ObjectDisposedException) when (_acceptStop.IsCancellationRequested)
                {
                    server.Dispose();
                    return;
                }
                catch (IOException) when (!_acceptStop.IsCancellationRequested)
                {
                    server.Dispose();
                    continue;
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_acceptingServer, server))
                        {
                            _acceptingServer = null;
                        }
                    }
                }

                if (!server.IsConnected || _acceptStop.IsCancellationRequested)
                {
                    server.Dispose();
                    continue;
                }

                if (!_requestSlots.Wait(0))
                {
                    await TryWriteResponseWithTimeoutAsync(
                        server,
                        WatcherManagementResponse.ErrorResponse(
                            _sessionId,
                            "unknown",
                            "busy",
                            "The watcher management endpoint is busy; retry the request."),
                        _forcedStop.Token).ConfigureAwait(false);
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
            // A fatal listener failure leaves the process without management
            // ownership. Ask the CLI lifetime owner to shut down instead of
            // leaving an apparently live but unreachable session behind.
            _listening.TrySetException(exception);
            _host.RequestStop();
        }
        finally
        {
            _listening.TrySetCanceled(_acceptStop.Token);
        }
    }

    private async Task HandleConnectionSlotAsync(NamedPipeServerStream server)
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
            // A disconnected management client does not affect the watcher.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown can close a connection between a read and a write.
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
                // The server has already completed its bounded drain.
            }
        }
    }

    private async Task ProcessConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        WatcherManagementRequest? request;
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(_options.InspectTimeout);
        try
        {
            request = await ReadRequestAsync(server, readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A client that does not send one complete request cannot retain a
            // management slot indefinitely. Closing the connection is enough;
            // the client-side deadline reports the bounded failure.
            return;
        }
        catch (InvalidDataException exception)
        {
            await TryWriteResponseWithTimeoutAsync(
                    server,
                    WatcherManagementResponse.ErrorResponse(
                        Guid.Empty,
                        "unknown",
                        "invalid_frame",
                        exception.Message),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            return;
        }

        var command = request.Command ?? "unknown";
        if (request.ProtocolVersion != WatcherManagementProtocol.CurrentVersion)
        {
            await TryWriteResponseWithTimeoutAsync(
                    server,
                    WatcherManagementResponse.ErrorResponse(
                        request.SessionId,
                        command,
                        "protocol_mismatch",
                        $"Management protocol version {request.ProtocolVersion} is not supported."),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.SessionId != _sessionId)
        {
            await TryWriteResponseWithTimeoutAsync(
                    server,
                    WatcherManagementResponse.ErrorResponse(
                        request.SessionId,
                        command,
                        "session_mismatch",
                        "The request is addressed to a different watcher session."),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (command is not ("inspect" or "stop"))
        {
            await TryWriteResponseWithTimeoutAsync(
                    server,
                    WatcherManagementResponse.ErrorResponse(
                        _sessionId,
                        command,
                        "unsupported_command",
                        "The management command must be 'inspect' or 'stop'."),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        WatcherManagementResponse response;
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(command == "stop"
            ? _options.StopTimeout
            : _options.InspectTimeout);
        try
        {
            var mediator = new WatcherManagementMediator(new WatcherManagementServiceProvider());
            response = command == "inspect"
                ? await mediator.Send(new InspectSessionRequest(_host, _sessionId), requestTimeout.Token).ConfigureAwait(false)
                : await mediator.Send(new StopSessionRequest(_host, _sessionId), requestTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            response = WatcherManagementResponse.ErrorResponse(
                _sessionId,
                command,
                "timeout",
                $"The management command '{command}' exceeded its server-side deadline.");
        }
        catch (Exception exception)
        {
            response = WatcherManagementResponse.ErrorResponse(
                _sessionId,
                command,
                command == "stop" ? "stop_failed" : "inspect_failed",
                exception.Message);
        }

        await TryWriteResponseWithTimeoutAsync(server, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WatcherManagementRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WatcherManagementRequest>(payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            await WriteResponseAsync(
                    stream,
                    WatcherManagementResponse.ErrorResponse(
                        Guid.Empty,
                        "unknown",
                        "invalid_request",
                        $"The management request JSON is invalid: {exception.Message}"),
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    internal static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > WatcherManagementProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("The management frame is outside the supported size limit.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > WatcherManagementProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("The management frame is outside the supported size limit.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        WatcherManagementResponse response,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        await WriteFrameAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteResponseAsync(
        Stream stream,
        WatcherManagementResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
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

    private async Task TryWriteResponseWithTimeoutAsync(
        Stream stream,
        WatcherManagementResponse response,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InspectTimeout);
        await TryWriteResponseAsync(stream, response, timeout.Token).ConfigureAwait(false);
    }

    private async Task RemoveCompletedHandlerAsync(Task handler)
    {
        try
        {
            await handler.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // HandleConnectionSlotAsync contains the expected transport
            // failures. This observer prevents an unobserved task if a new
            // exception is introduced in connection bookkeeping.
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
        NamedPipeServerStream[] connections;
        lock (_gate)
        {
            connections = _connections.ToArray();
        }

        foreach (var connection in connections)
        {
            connection.Dispose();
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
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception)
        {
        }
    }
}
