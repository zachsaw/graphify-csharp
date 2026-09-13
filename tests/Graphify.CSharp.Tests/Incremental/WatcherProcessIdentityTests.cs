using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class WatcherProcessIdentityTests
{
    [Fact]
    public void Probes_the_current_process_as_live_despite_start_time_read_jitter()
    {
        var recordedStartTime = WatcherProcessIdentity.CurrentStartTimeUtcTicks();
        Assert.NotNull(recordedStartTime);

        var descriptor = CreateDescriptor(recordedStartTime.Value);

        var probe = WatcherProcessIdentity.Probe(descriptor);

        Assert.Equal(ProcessLiveness.Live, probe.Liveness);
    }

    [Fact]
    public void Rejects_a_materially_different_process_start_time()
    {
        var recordedStartTime = WatcherProcessIdentity.CurrentStartTimeUtcTicks();
        Assert.NotNull(recordedStartTime);

        var descriptor = CreateDescriptor(recordedStartTime.Value - (TimeSpan.TicksPerSecond * 2));

        var probe = WatcherProcessIdentity.Probe(descriptor);

        Assert.Equal(ProcessLiveness.Stale, probe.Liveness);
    }

    private static WatcherSessionDescriptor CreateDescriptor(long processStartTimeUtcTicks) =>
        CreateDescriptor(Guid.NewGuid(), processStartTimeUtcTicks);

    private static WatcherSessionDescriptor CreateDescriptor(
        Guid sessionId,
        long processStartTimeUtcTicks) =>
        new(
            WatcherSessionRegistry.DescriptorSchemaVersion,
            sessionId,
            Environment.ProcessId,
            processStartTimeUtcTicks,
            WatcherManagementProtocol.ForSession(sessionId, Path.GetTempPath()),
            "/tmp/input.csproj",
            "/tmp",
            "Release",
            "net10.0",
            "/tmp/output.json",
            "test",
            WatcherManagementProtocol.CurrentVersion);
}
