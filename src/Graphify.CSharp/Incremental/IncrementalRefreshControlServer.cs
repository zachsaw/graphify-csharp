using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalRefreshControlServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _pipeName;
    private readonly string _requestDigest;
    private readonly string _outputPathIdentity;
    private readonly Func<bool, CancellationToken, Task<IncrementalRefreshResult>> _refresh;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _serverTask;
    private Task? _disposeTask;
    private NamedPipeServerStream? _activeServer;
    private bool _disposed;

    public IncrementalRefreshControlServer(
        string pipeName,
        RefreshRequestIdentity requestIdentity,
        string outputPath,
        Func<bool, CancellationToken, Task<IncrementalRefreshResult>> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(requestIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        _pipeName = pipeName;
        _requestDigest = requestIdentity.Digest;
        _outputPathIdentity = IncrementalRefreshControlChannel.OutputPathIdentity(outputPath);
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _serverTask ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposeTask = DisposeCoreAsync();
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        Task? serverTask;
        lock (_gate)
        {
            _disposed = true;
            _stop.Cancel();
            _activeServer?.Dispose();
            serverTask = _serverTask;
        }

        try
        {
            if (serverTask is not null)
            {
                await serverTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                lock (_gate)
                {
                    _activeServer = server;
                }

                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _activeServer = null;
                    }
                }

                try
                {
                    await ProcessConnectionAsync(server, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A client can disappear while a refresh is publishing.
                    // Keep the local service available for the next request.
                }
                catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The same applies when a client closes during shutdown.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            server,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            server,
            System.Text.Encoding.UTF8,
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        string? line;
        try
        {
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(line) || line.Length > 1024)
        {
            await WriteResponseAsync(
                writer,
                ControlResponse.Error("invalid_request", "The refresh request was empty or too large."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        ControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ControlRequest>(line, JsonOptions);
        }
        catch (JsonException exception)
        {
            await WriteResponseAsync(
                writer,
                ControlResponse.Error("invalid_request", $"The refresh request was invalid: {exception.Message}"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var command = request?.Command;
        if (command is not ("refresh" or "rebuild"))
        {
            await WriteResponseAsync(
                writer,
                ControlResponse.Error("invalid_request", "The refresh command must be 'refresh' or 'rebuild'."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request?.RequestDigest, _requestDigest, StringComparison.Ordinal))
        {
            await WriteResponseAsync(
                writer,
                ControlResponse.Error(
                    "configuration_mismatch",
                    "The refresh request does not match the watcher's analysis configuration."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request?.OutputPathIdentity, _outputPathIdentity, StringComparison.Ordinal))
        {
            await WriteResponseAsync(
                writer,
                ControlResponse.Error(
                    "output_mismatch",
                    "The refresh request does not match the watcher's output path."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await _refresh(command == "rebuild", cancellationToken).ConfigureAwait(false);
            await WriteResponseAsync(
                writer,
                ControlResponse.FromResult(result, _requestDigest, _outputPathIdentity),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            await TryWriteErrorAsync(writer, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Keep the local control channel alive if a new Roslyn or file
            // system exception is introduced above the session boundary.
            await TryWriteErrorAsync(writer, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryWriteErrorAsync(
        StreamWriter writer,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponseAsync(
                    writer,
                    ControlResponse.Error("refresh_failed", message),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client may have disconnected while the operation failed.
        }
        catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client may have disconnected while the operation failed.
        }
    }

    private static async Task WriteResponseAsync(
        StreamWriter writer,
        ControlResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await writer.WriteLineAsync(json).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record ControlRequest(
        [property: JsonPropertyName("command")] string? Command,
        [property: JsonPropertyName("request_digest")] string? RequestDigest,
        [property: JsonPropertyName("output_path_identity")] string? OutputPathIdentity);

    internal sealed record ControlResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("request_digest")] string? RequestDigest,
        [property: JsonPropertyName("output_path_identity")] string? OutputPathIdentity,
        [property: JsonPropertyName("output_digest")] string? OutputDigest,
        [property: JsonPropertyName("nodes")] int? NodeCount,
        [property: JsonPropertyName("edges")] int? EdgeCount,
        [property: JsonPropertyName("extracted_projects")] int? ExtractedProjectCount,
        [property: JsonPropertyName("reused_projects")] int? ReusedProjectCount,
        [property: JsonPropertyName("output_republished")] bool? OutputRepublished,
        [property: JsonPropertyName("event_generation")] long? EventGeneration,
        [property: JsonPropertyName("indexed_generation")] long? IndexedGeneration,
        [property: JsonPropertyName("published_generation")] long? PublishedGeneration)
    {
        public static ControlResponse FromResult(
            IncrementalRefreshResult result,
            string requestDigest,
            string outputPathIdentity)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestDigest);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPathIdentity);
            return new ControlResponse(
                Success: true,
                Message: null,
                ErrorCode: null,
                RequestDigest: requestDigest,
                OutputPathIdentity: outputPathIdentity,
                OutputDigest: result.OutputDigest,
                NodeCount: result.Graph.Nodes.Length,
                EdgeCount: result.Graph.Edges.Length,
                ExtractedProjectCount: result.ExtractedProjectCount,
                ReusedProjectCount: result.ReusedProjectCount,
                OutputRepublished: result.OutputRepublished,
                EventGeneration: result.Generation?.EventGeneration,
                IndexedGeneration: result.Generation?.IndexedGeneration,
                PublishedGeneration: result.Generation?.PublishedGeneration);
        }

        public static ControlResponse Error(string errorCode, string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            return new ControlResponse(
                Success: false,
                Message: message,
                ErrorCode: errorCode,
                RequestDigest: null,
                OutputPathIdentity: null,
                OutputDigest: null,
                NodeCount: null,
                EdgeCount: null,
                ExtractedProjectCount: null,
                ReusedProjectCount: null,
                OutputRepublished: null,
                EventGeneration: null,
                IndexedGeneration: null,
                PublishedGeneration: null);
        }
    }
}
