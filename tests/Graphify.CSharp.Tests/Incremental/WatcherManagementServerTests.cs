using System.Text.Json;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class WatcherManagementServerTests
{
    [Fact]
    public async Task Generated_dispatch_reaches_inspect_handler_without_loading_roslyn()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "starting", ready: false));
        var options = new WatcherManagementOptions(root, "test", inspectTimeout: TimeSpan.FromSeconds(2));
        await using var server = new WatcherManagementServer(sessionId, host, options);
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var response = await new WatcherManagementClient().InspectAsync(
            CreateDescriptor(sessionId, server.PipeName, root),
            TimeSpan.FromSeconds(2));

        Assert.True(response.Success);
        Assert.Equal("inspect", response.Command);
        Assert.Equal("starting", response.Inspection!.LifecycleState);
        Assert.False(response.Inspection.Ready);
        Assert.Equal(0, host.StopRequestCount);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Info_is_an_inspect_alias_and_diagnostics_keeps_one_observation_payload()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "ready", ready: true));
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(root, "test"));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var descriptor = CreateDescriptor(sessionId, server.PipeName, root);

        var info = await SendRawAsync(
            descriptor.ManagementEndpoint,
            new WatcherManagementRequest(
                WatcherManagementProtocol.CurrentVersion,
                sessionId,
                "info"));
        Assert.True(info.Success);
        Assert.Equal("inspect", info.Command);
        Assert.NotNull(info.Inspection);
        Assert.Null(info.Diagnostics);

        var diagnostics = await new WatcherManagementClient().DiagnosticsAsync(
            descriptor,
            TimeSpan.FromSeconds(2));
        Assert.True(diagnostics.Success);
        Assert.Equal("diagnostics", diagnostics.Command);
        Assert.NotNull(diagnostics.Diagnostics);
        Assert.Null(diagnostics.Inspection);
        Assert.NotNull(diagnostics.Diagnostics!.Inspection);
        Assert.Null(diagnostics.Diagnostics.Inspection.Observation);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Invalid_protocol_and_session_requests_are_rejected_before_dispatch()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "ready", ready: true));
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(root, "test"));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var descriptor = CreateDescriptor(sessionId, server.PipeName, root);

        var protocolResponse = await SendRawAsync(
            descriptor.ManagementEndpoint,
            new WatcherManagementRequest(999, sessionId, "inspect"));
        Assert.False(protocolResponse.Success);
        Assert.Equal("protocol_mismatch", protocolResponse.ErrorCode);

        var sessionResponse = await SendRawAsync(
            descriptor.ManagementEndpoint,
            new WatcherManagementRequest(
                WatcherManagementProtocol.CurrentVersion,
                Guid.NewGuid(),
                "inspect"));
        Assert.False(sessionResponse.Success);
        Assert.Equal("session_mismatch", sessionResponse.ErrorCode);
        Assert.Equal(0, host.InspectCount);
        Assert.Equal(0, host.StopRequestCount);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Unsupported_command_is_rejected_without_dispatch()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "ready", ready: true));
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(root, "test"));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var response = await SendRawAsync(
            CreateDescriptor(sessionId, server.PipeName, root).ManagementEndpoint,
            new WatcherManagementRequest(
                WatcherManagementProtocol.CurrentVersion,
                sessionId,
                "old-worker-diagnostics"));

        Assert.False(response.Success);
        Assert.Equal("unsupported_command", response.ErrorCode);
        Assert.Equal(0, host.InspectCount);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Malformed_json_is_returned_as_a_bounded_structured_error()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "ready", ready: true));
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(root, "test"));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await using var client = await LocalIpcTransport.ConnectAsync(
            server.PipeName,
            TimeSpan.FromSeconds(5));
        await WatcherManagementServer.WriteFrameAsync(
            client,
            "{ not valid json }"u8.ToArray(),
            CancellationToken.None);
        var payload = await WatcherManagementServer.ReadFrameAsync(
            client,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var response = JsonSerializer.Deserialize<WatcherManagementResponse>(payload)!;

        Assert.False(response.Success);
        Assert.Equal("invalid_request", response.ErrorCode);
        Assert.True(payload.Length <= WatcherManagementProtocol.MaximumFrameBytes);
        Assert.Equal(0, host.InspectCount);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Oversized_diagnostics_are_reduced_to_a_bounded_protocol_error()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var huge = new string('x', 40_000);
        var snapshot = CreateSnapshot(sessionId, "ready", ready: true) with
        {
            InputPath = huge,
            RepositoryRoot = huge,
        };
        var host = new FakeManagementHost(snapshot);
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(root, "test"));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var (payload, response) = await SendRawWithPayloadAsync(
            CreateDescriptor(sessionId, server.PipeName, root).ManagementEndpoint,
            new WatcherManagementRequest(
                WatcherManagementProtocol.CurrentVersion,
                sessionId,
                "diagnostics"));

        Assert.True(payload.Length <= WatcherManagementProtocol.MaximumFrameBytes);
        Assert.False(response.Success);
        Assert.Equal("response_too_large", response.ErrorCode);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Reduced_diagnostics_preserve_failure_meaning_and_report_omissions()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var durations = Enumerable.Range(0, 10_000)
            .ToDictionary(index => $"stage-{index:D5}", _ => 1L, StringComparer.Ordinal);
        var observation = new IndexingObservationSnapshot(
            DateTimeOffset.UtcNow,
            100,
            null,
            null,
            null,
            new ObservationOperationSummary(
                1,
                "startup",
                1,
                "succeeded",
                DateTimeOffset.UtcNow,
                100,
                durations,
                DateTimeOffset.UtcNow,
                null),
            new ObservationOperationSummary(
                2,
                "refresh",
                1,
                "failed",
                DateTimeOffset.UtcNow,
                100,
                durations,
                DateTimeOffset.UtcNow,
                "refresh failed"),
            new RecoveryObservationSummary(0, 0, 0, null, null),
            Enumerable.Range(0, 32)
                .Select(index => new ObservationEvent(
                    DateTimeOffset.UtcNow,
                    "stage",
                    $"event-{index}",
                    1))
                .ToArray(),
            new ObservationEvent(DateTimeOffset.UtcNow, "operation_failed", "failure marker", 2));
        var host = new FakeManagementHost(
            CreateSnapshot(sessionId, "ready", ready: true) with { Observation = observation });
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(root, "test"));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var (payload, response) = await SendRawWithPayloadAsync(
            CreateDescriptor(sessionId, server.PipeName, root).ManagementEndpoint,
            new WatcherManagementRequest(
                WatcherManagementProtocol.CurrentVersion,
                sessionId,
                "diagnostics"));

        Assert.True(response.Success);
        Assert.True(payload.Length <= WatcherManagementProtocol.MaximumFrameBytes);
        var reduced = response.Diagnostics!.Observation;
        Assert.Equal("operation_failed", reduced.LastFailure!.Kind);
        Assert.Contains("initial_startup", reduced.OmittedFields!);
        Assert.Contains("last_operation", reduced.OmittedFields!);
        Assert.Contains("recent_events", reduced.OmittedFields!);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Inspect_remains_available_while_stop_waits_for_work_completion()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "ready", ready: true));
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(
                root,
                "test",
                inspectTimeout: TimeSpan.FromSeconds(2),
                stopTimeout: TimeSpan.FromSeconds(2),
                drainTimeout: TimeSpan.FromSeconds(2)));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var descriptor = CreateDescriptor(sessionId, server.PipeName, root);
        var client = new WatcherManagementClient();

        var stop = client.StopAsync(descriptor, TimeSpan.FromSeconds(2));
        await host.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);

        var inspection = await client.InspectAsync(descriptor, TimeSpan.FromSeconds(2));
        Assert.True(inspection.Success);
        Assert.Equal(1, host.StopRequestCount);

        host.ReleaseWork();
        var stopped = await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Success);
        Assert.Equal("stop", stopped.Command);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Invalid_endpoint_is_reported_as_a_structured_client_failure()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var descriptor = CreateDescriptor(Guid.NewGuid(), "\0", root);

            var exception = await Assert.ThrowsAsync<WatcherManagementException>(() =>
                new WatcherManagementClient().InspectAsync(
                    descriptor,
                    TimeSpan.FromSeconds(2)));

            Assert.Equal("invalid_endpoint", exception.ErrorCode);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Oversized_frame_gets_a_bounded_protocol_error()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "ready", ready: true));
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(root, "test"));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await using var client = await LocalIpcTransport.ConnectAsync(
            server.PipeName,
            TimeSpan.FromSeconds(5));
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            header,
            WatcherManagementProtocol.MaximumFrameBytes + 1);
        await client.WriteAsync(header);
        await client.FlushAsync();
        var responsePayload = await WatcherManagementServer.ReadFrameAsync(
            client,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var response = JsonSerializer.Deserialize<WatcherManagementResponse>(responsePayload)!;

        Assert.False(response.Success);
        Assert.Equal("invalid_frame", response.ErrorCode);
        Assert.Equal(0, host.InspectCount);

        DeleteTemporaryDirectory(root);
    }

    [Fact]
    public async Task Stalled_client_is_closed_after_request_deadline_and_does_not_block_inspection()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var host = new FakeManagementHost(CreateSnapshot(sessionId, "ready", ready: true));
        await using var server = new WatcherManagementServer(
            sessionId,
            host,
            new WatcherManagementOptions(
                root,
                "test",
                inspectTimeout: TimeSpan.FromMilliseconds(100)));
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await using (var stalledClient = await LocalIpcTransport.ConnectAsync(
                         server.PipeName,
                         TimeSpan.FromSeconds(5)))
        {
            await Assert.ThrowsAnyAsync<IOException>(
                () => WatcherManagementServer
                    .ReadFrameAsync(stalledClient, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var response = await new WatcherManagementClient().InspectAsync(
            CreateDescriptor(sessionId, server.PipeName, root),
            TimeSpan.FromSeconds(2));
        Assert.True(response.Success);

        DeleteTemporaryDirectory(root);
    }

    private static async Task<WatcherManagementResponse> SendRawAsync(
        string endpoint,
        WatcherManagementRequest request)
    {
        var (_, response) = await SendRawWithPayloadAsync(endpoint, request);
        return response;
    }

    private static async Task<(byte[] Payload, WatcherManagementResponse Response)> SendRawWithPayloadAsync(
        string endpoint,
        WatcherManagementRequest request)
    {
        await using var client = await LocalIpcTransport.ConnectAsync(
            endpoint,
            TimeSpan.FromSeconds(5));
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        await WatcherManagementServer.WriteFrameAsync(client, payload, CancellationToken.None);
        var responsePayload = await WatcherManagementServer.ReadFrameAsync(client, CancellationToken.None);
        return (
            responsePayload,
            JsonSerializer.Deserialize<WatcherManagementResponse>(responsePayload)
                ?? throw new InvalidDataException("The test server returned no response."));
    }

    private static WatcherSessionDescriptor CreateDescriptor(
        Guid sessionId,
        string endpoint,
        string root) =>
        new(
            WatcherSessionRegistry.DescriptorSchemaVersion,
            sessionId,
            Environment.ProcessId,
            WatcherProcessIdentity.CurrentStartTimeUtcTicks(),
            endpoint,
            Path.Combine(root, "input.csproj"),
            root,
            "Release",
            "net10.0",
            Path.Combine(root, "output.json"),
            "test",
            WatcherManagementProtocol.CurrentVersion);

    private static WatcherInspectionSnapshot CreateSnapshot(
        Guid sessionId,
        string state,
        bool ready) =>
        new(
            sessionId,
            Environment.ProcessId,
            WatcherProcessIdentity.CurrentStartTimeUtcTicks(),
            "/tmp/input.csproj",
            "/tmp",
            "Release",
            "net10.0",
            "/tmp/output.json",
            "gcm-test",
            "test",
            WatcherManagementProtocol.CurrentVersion,
            state,
            ready,
            1,
            1,
            1);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "graphify-csharp-management-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FakeManagementHost : IWatcherManagementHost
    {
        private readonly WatcherInspectionSnapshot _snapshot;
        private readonly TaskCompletionSource<bool> _workStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inspectCount;
        private int _stopRequestCount;

        public FakeManagementHost(WatcherInspectionSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public TaskCompletionSource<bool> StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InspectCount => Volatile.Read(ref _inspectCount);

        public int StopRequestCount => Volatile.Read(ref _stopRequestCount);

        public WatcherInspectionSnapshot GetInspectionSnapshot()
        {
            Interlocked.Increment(ref _inspectCount);
            return _snapshot;
        }

        public WatcherDiagnosticsSnapshot GetDiagnosticsSnapshot() =>
            new(
                "graphify-csharp/diagnostics/v1",
                DateTimeOffset.UtcNow,
                _snapshot with { Observation = null },
                new ObservationRuntimeSnapshot("test", "test", "test", 1, false, 1),
                _snapshot.Observation
                    ?? new IndexingObservationSnapshot(
                        DateTimeOffset.UtcNow,
                        0,
                        null,
                        null,
                        null,
                        null,
                        null,
                        new RecoveryObservationSummary(0, 0, 0, null, null),
                        Array.Empty<ObservationEvent>(),
                        null));

        public void RequestStop()
        {
            Interlocked.Increment(ref _stopRequestCount);
            StopRequested.TrySetResult(true);
        }

        public Task WaitForWorkStoppedAsync(CancellationToken cancellationToken) =>
            _workStopped.Task.WaitAsync(cancellationToken);

        public void ReleaseWork() => _workStopped.TrySetResult(true);
    }
}
