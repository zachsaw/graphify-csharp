using System.IO.Pipes;
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

        await using var client = new NamedPipeClientStream(
            ".",
            server.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
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

        await using (var stalledClient = new NamedPipeClientStream(
                         ".",
                         server.PipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await stalledClient.ConnectAsync(5000);
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
        await using var client = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        await WatcherManagementServer.WriteFrameAsync(client, payload, CancellationToken.None);
        var responsePayload = await WatcherManagementServer.ReadFrameAsync(client, CancellationToken.None);
        return JsonSerializer.Deserialize<WatcherManagementResponse>(responsePayload)
            ?? throw new InvalidDataException("The test server returned no response.");
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
