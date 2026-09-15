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
                + $"ready={session.Ready,-5} pid={session.ProcessId} "
                + $"stage={session.Stage ?? "idle",-24} rss={FormatBytes(session.WorkingSetBytes),-12} "
                + $"uptime={FormatElapsed(session.UptimeMilliseconds)} input={session.InputPath}");
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
            : options.Kind == WatcherManagementCommandKind.Diagnostics
                ? await client.DiagnosticsAsync(descriptor, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false)
                : await client.StopAsync(descriptor, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (options.Kind == WatcherManagementCommandKind.Diagnostics
            && response.Success)
        {
            if (response.Diagnostics is null)
            {
                return WriteFailure(
                    options.Json,
                    "diagnostics_missing",
                    "The watcher reported success but did not return a diagnostics report.",
                    exitCode: 1);
            }

            var reportPath = Path.GetFullPath(options.OutputPath!);
            await WriteDiagnosticsReportAsync(reportPath, response.Diagnostics, cancellationToken).ConfigureAwait(false);
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DiagnosticsCliResponse(true, reportPath, null, null),
                    JsonOptions));
            }
            else
            {
                Console.WriteLine($"Wrote diagnostics to {reportPath}.");
            }

            return 0;
        }
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
            Console.WriteLine($"output: {inspection.OutputPath ?? "<none>"}");
            Console.WriteLine($"semantic-endpoint: {inspection.SemanticEndpoint ?? "<none>"}");
            Console.WriteLine($"generations: events={inspection.EventGeneration}, indexed={inspection.IndexedGeneration}, published={inspection.PublishedGeneration}");
            if (inspection.Observation?.StartupPending == true)
            {
                Console.WriteLine($"startup: pending ({inspection.Observation.StartupDetail ?? "host barrier"})");
            }
            WriteObservationText(inspection.Observation);
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

    private static async Task WriteDiagnosticsReportAsync(
        string outputPath,
        WatcherDiagnosticsSnapshot report,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new IOException("The diagnostics output path has no directory.");
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static void WriteObservationText(IndexingObservationSnapshot? observation)
    {
        if (observation is null)
        {
            return;
        }

        var activity = observation.Activity;
        Console.WriteLine($"activity: {(activity is null ? "idle" : $"{activity.OperationKind}/{activity.Stage} ({activity.OperationElapsedMilliseconds}ms)")}");
        if (activity?.Completed is not null)
        {
            Console.WriteLine($"progress: {activity.Completed}/{activity.Total?.ToString() ?? "?"} {activity.Unit}");
        }

        var evidence = observation.Evidence;
        if (evidence is not null)
        {
            Console.WriteLine($"evidence: {evidence.Projects} projects, {evidence.DocumentInstances} document instances, {evidence.Declarations} declarations, {evidence.ContributionEdges} contribution edges (revision {evidence.EvidenceRevision})");
            Console.WriteLine($"query-index: {(evidence.SemanticIndexBuilt ? $"built ({evidence.SemanticIndexEdges} edges)" : "not built")}");
            Console.WriteLine($"last-indexing-result: extracted={evidence.ExtractedProjects}, reused={evidence.ReusedProjects}");
        }

        var resources = observation.Resources;
        if (resources is not null)
        {
            Console.WriteLine($"memory: rss={FormatBytes(resources.WorkingSetBytes)}, peak-rss={FormatBytes(resources.PeakWorkingSetBytes)}, managed-heap-estimate={FormatBytes(resources.ManagedHeapEstimateBytes)}");
            var sampleAge = Math.Max(0, (observation.ObservedAtUtc - resources.SampledAtUtc).TotalMilliseconds);
            Console.WriteLine($"memory-sample-age: {FormatElapsed((long)sampleAge)}");
        }

        if (observation.LastOperation is { } lastOperation)
        {
            Console.WriteLine($"last-operation: {lastOperation.OperationKind} {lastOperation.Outcome} ({FormatElapsed(lastOperation.ElapsedMilliseconds)})");
        }

        if (observation.Recovery.Attempts > 0)
        {
            Console.WriteLine($"recovery: attempts={observation.Recovery.Attempts}, successes={observation.Recovery.Successes}, failures={observation.Recovery.Failures}");
        }

        if (observation.RecentEventsOmitted > 0 || observation.MessagesTruncated > 0)
        {
            Console.WriteLine($"diagnostics-bounds: events-omitted={observation.RecentEventsOmitted}, messages-truncated={observation.MessagesTruncated}");
        }
    }

    private static string FormatBytes(long? bytes) =>
        bytes is null
            ? "<unavailable>"
            : bytes.Value >= 1024L * 1024 * 1024
                ? $"{bytes.Value / (1024d * 1024 * 1024):0.0} GiB"
                : $"{bytes.Value / (1024d * 1024):0.0} MiB";

    private static string FormatElapsed(long? milliseconds) =>
        milliseconds is null
            ? "<unknown>"
            : TimeSpan.FromMilliseconds(milliseconds.Value).ToString(@"dd\.hh\:mm\:ss");

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
            inspection?.PublishedGeneration,
            probe.Descriptor.SemanticEndpoint,
            probe.Descriptor.SemanticProtocolVersion,
            inspection?.Observation?.Activity?.Stage
                ?? (inspection?.Observation?.StartupPending == true ? "startup_pending" : null),
            inspection?.Observation?.Resources?.WorkingSetBytes,
            inspection?.Observation?.Resources?.PeakWorkingSetBytes,
            inspection?.Observation?.UptimeMilliseconds);
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
        [property: JsonPropertyName("output_path")] string? OutputPath,
        [property: JsonPropertyName("management_endpoint")] string ManagementEndpoint,
        [property: JsonPropertyName("tool_version")] string ToolVersion,
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("event_generation")] long? EventGeneration,
        [property: JsonPropertyName("indexed_generation")] long? IndexedGeneration,
        [property: JsonPropertyName("published_generation")] long? PublishedGeneration,
        [property: JsonPropertyName("semantic_endpoint")] string? SemanticEndpoint,
        [property: JsonPropertyName("semantic_protocol_version")] int? SemanticProtocolVersion,
        [property: JsonPropertyName("stage")] string? Stage,
        [property: JsonPropertyName("working_set_bytes")] long? WorkingSetBytes,
        [property: JsonPropertyName("peak_working_set_bytes")] long? PeakWorkingSetBytes,
        [property: JsonPropertyName("uptime_ms")] long? UptimeMilliseconds);

    private sealed record WatcherManagementErrorResponse(
        [property: JsonPropertyName("schema_version")] string SchemaVersion,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error_code")] string ErrorCode,
        [property: JsonPropertyName("message")] string Message);

    private sealed record DiagnosticsCliResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("report_path")] string? ReportPath,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("message")] string? Message);
}
