using System.Text.Json;
using System.Text.Json.Serialization;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Cli;

internal static class WatcherManagementCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(
        WatcherManagementCommandLineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ShowHelp)
        {
            Console.WriteLine(WatcherManagementCommandLine.Usage);
            return 0;
        }

        try
        {
            var registry = new WatcherSessionRegistry(WatcherSessionRegistry.ResolveStateDirectory());
            return options.Kind == WatcherManagementCommandKind.List
                ? await ListAsync(registry, options.Json, cancellationToken).ConfigureAwait(false)
                : await ContactAsync(registry, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (CommandLineException exception)
        {
            return WriteFailure(options.Json, "invalid_arguments", exception.Message, exitCode: 2);
        }
        catch (WatcherManagementException exception) when (exception.ErrorCode == "ambiguous_session")
        {
            return WriteFailure(options.Json, exception.ErrorCode, exception.Message, exitCode: 2);
        }
        catch (WatcherManagementException exception)
        {
            return WriteFailure(options.Json, exception.ErrorCode, exception.Message, exitCode: 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return WriteFailure(options.Json, "management_failed", exception.Message, exitCode: 1);
        }
    }

    internal static int WriteCommandLineFailure(bool json, string message) =>
        WriteFailure(json, "invalid_arguments", message, exitCode: 2);

    private static async Task<int> ListAsync(
        WatcherSessionRegistry registry,
        bool json,
        CancellationToken cancellationToken)
    {
        var records = registry.ReadAll();
        var valid = records
            .Where(record => record.Descriptor is not null)
            .Select(record => record.Descriptor!)
            .ToArray();
        var client = new WatcherManagementClient();
        var probes = new WatcherSessionProbeResult[valid.Length];
        await Parallel.ForEachAsync(
                Enumerable.Range(0, valid.Length),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4,
                },
                async (index, token) =>
                {
                    probes[index] = await client
                        .ProbeAsync(valid[index], TimeSpan.FromSeconds(2), token)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);

        var sessions = probes.Select(ToListItem).ToArray();
        var diagnostics = records
            .Where(record => record.Descriptor is null)
            .Select(record => $"{record.ErrorCode}: {record.Diagnostic}")
            .Concat(probes
                .Where(probe => probe.Diagnostic is not null)
                .Select(probe => $"{probe.Descriptor.SessionId:D}: {probe.Diagnostic}"))
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToArray();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new WatcherManagementListResponse(
                    WatcherManagementProtocol.SchemaVersion,
                    Success: true,
                    sessions,
                    diagnostics,
                    ErrorCode: null,
                    Message: null),
                JsonOptions));
            return 0;
        }

        foreach (var session in sessions)
        {
            Console.WriteLine(
                $"{session.SessionId:D} {session.Reachability,-14} {session.LifecycleState,-10} "
                + $"ready={session.Ready,-5} pid={session.ProcessId} input={session.InputPath}");
        }

        foreach (var diagnostic in diagnostics)
        {
            Console.Error.WriteLine($"Diagnostic: {diagnostic}");
        }

        return 0;
    }

    private static async Task<int> ContactAsync(
        WatcherSessionRegistry registry,
        WatcherManagementCommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var descriptor = ResolveDescriptor(registry, options.SessionSelector!);
        var client = new WatcherManagementClient();
        if (options.Kind == WatcherManagementCommandKind.Stop)
        {
            var process = WatcherProcessIdentity.Probe(descriptor);
            if (process.Liveness == ProcessLiveness.Stale)
            {
                return WriteFailure(
                    options.Json,
                    "stale_session",
                    process.Diagnostic ?? "The selected watcher process is no longer the recorded process.",
                    exitCode: 1);
            }
        }

        var response = options.Kind == WatcherManagementCommandKind.Inspect
            ? await client.InspectAsync(descriptor, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false)
            : await client.StopAsync(descriptor, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else if (response.Success && response.Inspection is not null)
        {
            var inspection = response.Inspection;
            Console.WriteLine($"session: {inspection.SessionId:D}");
            Console.WriteLine($"state: {inspection.LifecycleState}");
            Console.WriteLine($"ready: {inspection.Ready}");
            Console.WriteLine($"pid: {inspection.ProcessId}");
            Console.WriteLine($"input: {inspection.InputPath}");
            Console.WriteLine($"root: {inspection.RepositoryRoot}");
            Console.WriteLine($"configuration: {inspection.Configuration}");
            Console.WriteLine($"target-framework: {inspection.TargetFramework ?? "<unresolved>"}");
            Console.WriteLine($"output: {inspection.OutputPath}");
            Console.WriteLine($"generations: events={inspection.EventGeneration}, indexed={inspection.IndexedGeneration}, published={inspection.PublishedGeneration}");
        }

        if (!response.Success)
        {
            return WriteFailure(
                options.Json,
                response.ErrorCode ?? "management_failed",
                response.Message ?? "The watcher rejected the management request.",
                exitCode: 1,
                alreadyWritten: options.Json);
        }

        if (!options.Json && options.Kind == WatcherManagementCommandKind.Stop)
        {
            Console.WriteLine($"Stopped {descriptor.SessionId:D}.");
        }

        return 0;
    }

    private static WatcherSessionDescriptor ResolveDescriptor(
        WatcherSessionRegistry registry,
        string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new CommandLineException("A session ID or prefix is required.");
        }

        var candidates = registry.ReadAll()
            .Where(record => record.Descriptor is not null)
            .Select(record => record.Descriptor!)
            .Where(descriptor => descriptor.SessionId.ToString("D").StartsWith(selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new WatcherManagementException("unknown_session", $"No watcher session matches '{selector}'."),
            _ => throw new WatcherManagementException(
                "ambiguous_session",
                $"More than one watcher session matches '{selector}': {string.Join(", ", candidates.Select(candidate => candidate.SessionId.ToString("D")))}"),
        };
    }

    private static WatcherManagementListItem ToListItem(WatcherSessionProbeResult probe)
    {
        var inspection = probe.Inspection;
        return new WatcherManagementListItem(
            probe.Descriptor.SessionId,
            probe.Descriptor.ProcessId,
            probe.Reachability,
            inspection?.LifecycleState ?? "unknown",
            inspection?.Ready ?? false,
            probe.Descriptor.InputPath,
            probe.Descriptor.RepositoryRoot,
            probe.Descriptor.Configuration,
            probe.Descriptor.TargetFramework,
            probe.Descriptor.OutputPath,
            probe.Descriptor.ManagementEndpoint,
            probe.Descriptor.ToolVersion,
            probe.Descriptor.ProtocolVersion,
            inspection?.EventGeneration,
            inspection?.IndexedGeneration,
            inspection?.PublishedGeneration);
    }

    private static int WriteFailure(
        bool json,
        string errorCode,
        string message,
        int exitCode,
        bool alreadyWritten = false)
    {
        if (json)
        {
            if (!alreadyWritten)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new WatcherManagementErrorResponse(
                        WatcherManagementProtocol.SchemaVersion,
                        Success: false,
                        errorCode,
                        message),
                    JsonOptions));
            }
        }
        else
        {
            Console.Error.WriteLine($"Error ({errorCode}): {message}");
        }

        return exitCode;
    }

    private sealed record WatcherManagementListResponse(
        [property: JsonPropertyName("schema_version")] string SchemaVersion,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("sessions")] IReadOnlyList<WatcherManagementListItem> Sessions,
        [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record WatcherManagementListItem(
        [property: JsonPropertyName("session_id")] Guid SessionId,
        [property: JsonPropertyName("process_id")] int ProcessId,
        [property: JsonPropertyName("reachability")] string Reachability,
        [property: JsonPropertyName("lifecycle_state")] string LifecycleState,
        [property: JsonPropertyName("ready")] bool Ready,
        [property: JsonPropertyName("input_path")] string InputPath,
        [property: JsonPropertyName("repository_root")] string RepositoryRoot,
        [property: JsonPropertyName("configuration")] string Configuration,
        [property: JsonPropertyName("target_framework")] string? TargetFramework,
        [property: JsonPropertyName("output_path")] string OutputPath,
        [property: JsonPropertyName("management_endpoint")] string ManagementEndpoint,
        [property: JsonPropertyName("tool_version")] string ToolVersion,
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("event_generation")] long? EventGeneration,
        [property: JsonPropertyName("indexed_generation")] long? IndexedGeneration,
        [property: JsonPropertyName("published_generation")] long? PublishedGeneration);

    private sealed record WatcherManagementErrorResponse(
        [property: JsonPropertyName("schema_version")] string SchemaVersion,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error_code")] string ErrorCode,
        [property: JsonPropertyName("message")] string Message);
}
