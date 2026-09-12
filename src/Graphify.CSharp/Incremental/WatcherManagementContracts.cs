using System.Diagnostics;
using System.Text.Json.Serialization;
using Mediator.Switch;

namespace Graphify.CSharp.Incremental;

internal static class WatcherManagementProtocol
{
    public const int CurrentVersion = 1;
    public const string SchemaVersion = "graphify-csharp/management/v1";
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumConcurrentRequests = 8;
    private const string EndpointPrefix = "gcm-";
    private const int EndpointHashLength = 24;

    public static string ForSession(Guid sessionId, string stateDirectory)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A management endpoint requires a session ID.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var key = $"management\u001F{sessionId:D}\u001F{IncrementalPaths.CanonicalAbsolutePath(stateDirectory)}";
        return $"{EndpointPrefix}{IncrementalHashing.Sha256(key)[..EndpointHashLength]}";
    }

    public static bool IsValidEndpoint(string? endpoint)
    {
        if (endpoint is null
            || endpoint.Length != EndpointPrefix.Length + EndpointHashLength
            || !endpoint.StartsWith(EndpointPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = EndpointPrefix.Length; index < endpoint.Length; index++)
        {
            var character = endpoint[index];
            if (!((character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record WatcherManagementOptions
{
    public WatcherManagementOptions(
        string stateDirectory,
        string toolVersion,
        TimeSpan? inspectTimeout = null,
        TimeSpan? stopTimeout = null,
        TimeSpan? drainTimeout = null)
    {
        StateDirectory = IncrementalPaths.CanonicalAbsolutePath(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ToolVersion = toolVersion.Trim();
        InspectTimeout = ValidateTimeout(inspectTimeout ?? TimeSpan.FromSeconds(2), nameof(inspectTimeout));
        StopTimeout = ValidateTimeout(stopTimeout ?? TimeSpan.FromSeconds(30), nameof(stopTimeout));
        DrainTimeout = ValidateTimeout(drainTimeout ?? TimeSpan.FromSeconds(5), nameof(drainTimeout));
    }

    public string StateDirectory { get; }

    public string ToolVersion { get; }

    public TimeSpan InspectTimeout { get; }

    public TimeSpan StopTimeout { get; }

    public TimeSpan DrainTimeout { get; }

    private static TimeSpan ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The management timeout must be between 1ms and 5 minutes.");
        }

        return value;
    }
}

internal sealed record WatcherSessionDescriptor(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("process_start_time_utc_ticks")] long? ProcessStartTimeUtcTicks,
    [property: JsonPropertyName("management_endpoint")] string ManagementEndpoint,
    [property: JsonPropertyName("input_path")] string InputPath,
    [property: JsonPropertyName("repository_root")] string RepositoryRoot,
    [property: JsonPropertyName("configuration")] string Configuration,
    [property: JsonPropertyName("target_framework")] string? TargetFramework,
    [property: JsonPropertyName("output_path")] string OutputPath,
    [property: JsonPropertyName("tool_version")] string ToolVersion,
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion);

internal sealed record WatcherInspectionSnapshot(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("process_start_time_utc_ticks")] long? ProcessStartTimeUtcTicks,
    [property: JsonPropertyName("input_path")] string InputPath,
    [property: JsonPropertyName("repository_root")] string RepositoryRoot,
    [property: JsonPropertyName("configuration")] string Configuration,
    [property: JsonPropertyName("target_framework")] string? TargetFramework,
    [property: JsonPropertyName("output_path")] string OutputPath,
    [property: JsonPropertyName("management_endpoint")] string ManagementEndpoint,
    [property: JsonPropertyName("tool_version")] string ToolVersion,
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("lifecycle_state")] string LifecycleState,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("event_generation")] long EventGeneration,
    [property: JsonPropertyName("indexed_generation")] long IndexedGeneration,
    [property: JsonPropertyName("published_generation")] long PublishedGeneration);

internal sealed record WatcherManagementRequest(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("command")] string? Command);

internal sealed record WatcherManagementResponse(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("inspection")] WatcherInspectionSnapshot? Inspection)
{
    public static WatcherManagementResponse SuccessResponse(
        Guid sessionId,
        string command,
        WatcherInspectionSnapshot inspection) =>
        new(
            WatcherManagementProtocol.SchemaVersion,
            WatcherManagementProtocol.CurrentVersion,
            sessionId,
            command,
            Success: true,
            ErrorCode: null,
            Message: null,
            inspection);

    public static WatcherManagementResponse ErrorResponse(
        Guid sessionId,
        string command,
        string errorCode,
        string message) =>
        new(
            WatcherManagementProtocol.SchemaVersion,
            WatcherManagementProtocol.CurrentVersion,
            sessionId,
            command,
            Success: false,
            errorCode,
            message,
            Inspection: null);
}

internal interface IWatcherManagementHost
{
    WatcherInspectionSnapshot GetInspectionSnapshot();

    void RequestStop();

    Task WaitForWorkStoppedAsync(CancellationToken cancellationToken);
}

internal sealed record InspectSessionRequest(
    IWatcherManagementHost Host,
    Guid SessionId) : IRequest<WatcherManagementResponse>;

internal sealed record StopSessionRequest(
    IWatcherManagementHost Host,
    Guid SessionId) : IRequest<WatcherManagementResponse>;

[SwitchMediator]
internal partial class WatcherManagementMediator;

internal sealed class InspectSessionHandler : IRequestHandler<InspectSessionRequest, WatcherManagementResponse>
{
    public Task<WatcherManagementResponse> Handle(
        InspectSessionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            WatcherManagementResponse.SuccessResponse(
                request.SessionId,
                "inspect",
                request.Host.GetInspectionSnapshot()));
    }
}

internal sealed class StopSessionHandler : IRequestHandler<StopSessionRequest, WatcherManagementResponse>
{
    public async Task<WatcherManagementResponse> Handle(
        StopSessionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Host.RequestStop();
        await request.Host.WaitForWorkStoppedAsync(cancellationToken).ConfigureAwait(false);
        return WatcherManagementResponse.SuccessResponse(
            request.SessionId,
            "stop",
            request.Host.GetInspectionSnapshot());
    }
}

internal sealed class WatcherManagementServiceProvider : ISwitchMediatorServiceProvider
{
    private readonly InspectSessionHandler _inspect = new();
    private readonly StopSessionHandler _stop = new();

    public T Get<T>() where T : notnull
    {
        if (typeof(T) == typeof(InspectSessionHandler))
        {
            return (T)(object)_inspect;
        }

        if (typeof(T) == typeof(StopSessionHandler))
        {
            return (T)(object)_stop;
        }

        throw new InvalidOperationException($"No management handler is registered for '{typeof(T).FullName}'.");
    }
}

internal static class WatcherProcessIdentity
{
    public static long? CurrentStartTimeUtcTicks()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (IsProcessIdentityAccessFailure(exception))
        {
            return null;
        }
    }

    public static ProcessIdentityProbe Probe(WatcherSessionDescriptor descriptor)
    {
        try
        {
            using var process = Process.GetProcessById(descriptor.ProcessId);
            if (descriptor.ProcessStartTimeUtcTicks is null)
            {
                return new ProcessIdentityProbe(ProcessLiveness.Unknown, "The descriptor has no process-start identity.");
            }

            long startTime;
            try
            {
                startTime = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception exception) when (IsProcessIdentityAccessFailure(exception))
            {
                return new ProcessIdentityProbe(ProcessLiveness.Unknown, "The process start identity could not be read.");
            }

            return startTime == descriptor.ProcessStartTimeUtcTicks
                ? new ProcessIdentityProbe(ProcessLiveness.Live, null)
                : new ProcessIdentityProbe(ProcessLiveness.Stale, "The descriptor's PID has been reused by another process.");
        }
        catch (ArgumentException)
        {
            return new ProcessIdentityProbe(ProcessLiveness.Stale, "The descriptor's process no longer exists.");
        }
        catch (InvalidOperationException)
        {
            return new ProcessIdentityProbe(ProcessLiveness.Unknown, "The process identity could not be inspected.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessIdentityProbe(ProcessLiveness.Unknown, "The process identity could not be inspected.");
        }
        catch (UnauthorizedAccessException)
        {
            return new ProcessIdentityProbe(ProcessLiveness.Unknown, "The process identity could not be inspected.");
        }
    }

    private static bool IsProcessIdentityAccessFailure(Exception exception) =>
        exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or NotSupportedException;
}

internal enum ProcessLiveness
{
    Live,
    Stale,
    Unknown,
}

internal sealed record ProcessIdentityProbe(ProcessLiveness Liveness, string? Diagnostic);
