using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graphify.CSharp.Incremental;

internal sealed class WatcherManagementException : Exception
{
    public WatcherManagementException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

internal sealed record WatcherSessionProbeResult(
    WatcherSessionDescriptor Descriptor,
    string Reachability,
    WatcherInspectionSnapshot? Inspection,
    string? Diagnostic);

internal sealed class WatcherManagementClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<WatcherManagementResponse> InspectAsync(
        WatcherSessionDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        SendAsync(descriptor, "inspect", timeout, cancellationToken);

    public Task<WatcherManagementResponse> StopAsync(
        WatcherSessionDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        SendAsync(descriptor, "stop", timeout, cancellationToken);

    public async Task<WatcherSessionProbeResult> ProbeAsync(
        WatcherSessionDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var process = WatcherProcessIdentity.Probe(descriptor);
        if (process.Liveness == ProcessLiveness.Stale)
        {
            return new WatcherSessionProbeResult(
                descriptor,
                "stale",
                Inspection: null,
                process.Diagnostic);
        }

        try
        {
            var response = await InspectAsync(descriptor, timeout, cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                return new WatcherSessionProbeResult(
                    descriptor,
                    "protocol_error",
                    Inspection: null,
                    response.Message ?? response.ErrorCode ?? "The watcher rejected inspection.");
            }

            return new WatcherSessionProbeResult(
                descriptor,
                "reachable",
                response.Inspection,
                process.Diagnostic);
        }
        catch (WatcherManagementException exception) when (exception.ErrorCode is "unreachable" or "timeout")
        {
            return new WatcherSessionProbeResult(
                descriptor,
                "unreachable",
                Inspection: null,
                exception.Message);
        }
        catch (WatcherManagementException exception)
        {
            return new WatcherSessionProbeResult(
                descriptor,
                "protocol_error",
                Inspection: null,
                exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return new WatcherSessionProbeResult(
                descriptor,
                "protocol_error",
                Inspection: null,
                exception.Message);
        }
    }

    private static async Task<WatcherManagementResponse> SendAsync(
        WatcherSessionDescriptor descriptor,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;
        await using var pipe = new NamedPipeClientStream(
            ".",
            descriptor.ManagementEndpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(ToTimeoutMilliseconds(timeout), token).ConfigureAwait(false);
            var request = new WatcherManagementRequest(
                WatcherManagementProtocol.CurrentVersion,
                descriptor.SessionId,
                command);
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            await WatcherManagementServer.WriteFrameAsync(pipe, payload, token).ConfigureAwait(false);
            var responsePayload = await WatcherManagementServer.ReadFrameAsync(pipe, token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<WatcherManagementResponse>(responsePayload, JsonOptions)
                ?? throw new InvalidDataException("The watcher returned an empty management response.");
            ValidateResponse(response, descriptor, command);
            return response;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WatcherManagementException(
                "timeout",
                $"The watcher management request '{command}' timed out after {timeout}.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new WatcherManagementException(
                "timeout",
                $"The watcher management request '{command}' timed out after {timeout}.",
                exception);
        }
        catch (IOException exception)
        {
            throw new WatcherManagementException(
                "unreachable",
                "The watcher management endpoint could not be reached.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new WatcherManagementException(
                "unreachable",
                "The watcher management endpoint could not be reached.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new WatcherManagementException(
                "invalid_response",
                "The watcher returned invalid management JSON.",
                exception);
        }
        catch (InvalidDataException exception)
        {
            throw new WatcherManagementException(
                "invalid_response",
                "The watcher returned an invalid management response.",
                exception);
        }
    }

    private static void ValidateResponse(
        WatcherManagementResponse response,
        WatcherSessionDescriptor descriptor,
        string command)
    {
        if (!string.Equals(response.SchemaVersion, WatcherManagementProtocol.SchemaVersion, StringComparison.Ordinal)
            || response.ProtocolVersion != WatcherManagementProtocol.CurrentVersion)
        {
            throw new WatcherManagementException(
                "protocol_mismatch",
                "The watcher returned an unsupported management protocol version.");
        }

        if (response.SessionId != descriptor.SessionId)
        {
            throw new WatcherManagementException(
                "session_mismatch",
                "The watcher returned a response for a different session.");
        }

        if (!string.Equals(response.Command, command, StringComparison.Ordinal))
        {
            throw new WatcherManagementException(
                "invalid_response",
                "The watcher returned a response for a different command.");
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        (int)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
}
