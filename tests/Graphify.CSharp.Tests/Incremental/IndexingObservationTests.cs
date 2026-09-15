using System.Diagnostics;
using System.Text.Json;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class IndexingObservationTests
{
    [Fact]
    public async Task Uses_monotonic_time_and_keeps_stage_durations()
    {
        var clock = new FakeClock();
        await using var observation = new IndexingObservation(clock: clock);
        using var operation = observation.BeginOperation("startup");

        operation.SetStage("loading_projects");
        clock.Advance(TimeSpan.FromSeconds(2));
        operation.ReportWork(2, 10, "projects", "Product.csproj");
        var loading = observation.Snapshot().Activity;
        Assert.Equal(2, loading!.Completed);
        Assert.Equal(10, loading.Total);
        Assert.Equal("projects", loading.Unit);
        Assert.Equal("Product.csproj", loading.Detail);
        Assert.Equal(0, loading.LastWorkAgeMilliseconds);

        operation.SetStage("compiling");
        clock.Advance(TimeSpan.FromSeconds(3));

        var active = observation.Snapshot().Activity;
        Assert.NotNull(active);
        Assert.Equal("compiling", active.Stage);
        Assert.Equal(5000, active.OperationElapsedMilliseconds);
        Assert.Equal(3000, active.StageElapsedMilliseconds);
        Assert.Null(active.Completed);
        Assert.Null(active.Total);
        Assert.Null(active.Unit);
        Assert.Null(active.LastWorkAgeMilliseconds);

        operation.Complete();
        var completed = observation.Snapshot();
        Assert.Null(completed.Activity);
        Assert.Equal(5000, completed.LastOperation!.ElapsedMilliseconds);
        Assert.Equal(2000, completed.LastOperation.StageDurationsMilliseconds["loading_projects"]);
        Assert.Equal(3000, completed.LastOperation.StageDurationsMilliseconds["compiling"]);
    }

    [Fact]
    public async Task Late_callbacks_cannot_update_a_completed_or_replaced_operation()
    {
        var clock = new FakeClock();
        await using var observation = new IndexingObservation(clock: clock);
        using var first = observation.BeginOperation("startup");
        first.SetStage("loading_projects");
        first.Complete();

        using var second = observation.BeginOperation("refresh");
        second.SetStage("reconciling_changes");
        first.SetStage("extracting_references");
        first.ReportWork(99, 100, "documents");

        var snapshot = observation.Snapshot();
        Assert.Equal("refresh", snapshot.Activity!.OperationKind);
        Assert.Equal("reconciling_changes", snapshot.Activity.Stage);
        Assert.Null(snapshot.Activity.Completed);
    }

    [Fact]
    public async Task Late_callbacks_from_a_previous_phase_cannot_change_the_current_phase()
    {
        await using var observation = new IndexingObservation(clock: new FakeClock());
        using var operation = observation.BeginOperation("startup");

        using var firstPhase = operation.BeginPhase("cataloging");
        using var currentPhase = operation.BeginPhase("extracting_references");
        firstPhase!.SetStage("loading_projects", "late MSBuild callback");
        firstPhase.ReportWork(99, 100, "documents");

        var activity = observation.Snapshot().Activity!;
        Assert.Equal("extracting_references", activity.Stage);
        Assert.Null(activity.Detail);
        Assert.Null(activity.Completed);
    }

    [Fact]
    public async Task Phase_callbacks_accept_real_execution_order_and_keep_counts_monotonic()
    {
        var clock = new FakeClock();
        await using var observation = new IndexingObservation(clock: clock);
        using var operation = observation.BeginOperation("refresh");
        using var phase = operation.BeginPhase("extracting_relationships");

        phase!.SetStage("extracting_references");
        phase.ReportWork(0, 10, "documents");
        clock.Advance(TimeSpan.FromSeconds(1));
        phase.ReportWork(5, 10, "documents");
        clock.Advance(TimeSpan.FromSeconds(1));
        phase.ReportWork(3, 10, "documents");

        var activity = observation.Snapshot().Activity!;
        Assert.Equal("extracting_references", activity.Stage);
        Assert.Equal(5, activity.Completed);
        Assert.Equal(1000, activity.LastWorkAgeMilliseconds);
    }

    [Fact]
    public async Task Host_startup_pending_is_visible_and_failure_is_not_reported_as_success()
    {
        await using var observation = new IndexingObservation(clock: new FakeClock());
        observation.SetStartupPending(true, "final inventory");

        var pending = observation.Snapshot();
        Assert.True(pending.StartupPending);
        Assert.Equal("final inventory", pending.StartupDetail);

        observation.CompleteStartup("failed", "final inventory failed");
        var failed = observation.Snapshot();
        Assert.False(failed.StartupPending);
        Assert.Equal("startup_failed", failed.LastFailure!.Kind);
        Assert.Contains("final inventory failed", failed.LastFailure.Message);
    }

    [Fact]
    public async Task Recent_history_is_bounded_and_latest_failure_is_preserved()
    {
        var clock = new FakeClock();
        await using var observation = new IndexingObservation(clock: clock);
        using var operation = observation.BeginOperation("refresh");
        for (var index = 0; index < 100; index++)
        {
            operation.SetStage($"stage_{index}");
        }

        operation.Complete("failed", "the final failure");
        var snapshot = observation.Snapshot();
        Assert.Equal(32, snapshot.RecentEvents.Count);
        Assert.True(snapshot.RecentEventsOmitted > 0);
        Assert.Equal("operation_failed", snapshot.LastFailure!.Kind);
        Assert.Contains("the final failure", snapshot.LastFailure.Message);
    }

    [Fact]
    public async Task Unicode_and_control_messages_remain_valid_json_and_are_bounded()
    {
        var observation = new IndexingObservation(clock: new FakeClock());
        await using (observation)
        {
            observation.RecordEvent(
                "diagnostic",
                "before\n\u0001 " + string.Concat(Enumerable.Repeat("🙂", 400)));

            var snapshot = observation.Snapshot();
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
            var message = document.RootElement
                .GetProperty("recent_events")[0]
                .GetProperty("message")
                .GetString();

            Assert.NotNull(message);
            Assert.True(message!.Length <= 512);
            Assert.Equal(1, snapshot.MessagesTruncated);
        }
    }

    [Fact]
    public async Task Resource_sampling_publishes_a_cached_sample()
    {
        var sampler = new FakeResourceSampler();
        await using var observation = new IndexingObservation(sampler);
        observation.StartResourceSampling(TimeSpan.FromMilliseconds(1));

        await WaitUntilAsync(() => observation.Snapshot().Resources is not null);

        Assert.Equal(1234, observation.Snapshot().Resources!.WorkingSetBytes);
    }

    [Fact]
    public async Task Resource_sampling_failure_is_recorded_without_breaking_observation()
    {
        var sampler = new FailingResourceSampler();
        await using var observation = new IndexingObservation(sampler);
        observation.StartResourceSampling(TimeSpan.FromHours(1));

        await WaitUntilAsync(
            () => observation.Snapshot().RecentEvents.Any(
                item => item.Kind == "resource_sample_unavailable"));

        var snapshot = observation.Snapshot();
        Assert.Null(snapshot.Resources);
        Assert.Contains(snapshot.RecentEvents, item => item.Kind == "resource_sample_unavailable");
    }

    [Fact]
    public async Task Disposal_clears_active_work_and_rejects_late_callbacks()
    {
        var observation = new IndexingObservation(clock: new FakeClock());
        var operation = observation.BeginOperation("refresh");
        operation.SetStage("loading_projects");

        await observation.DisposeAsync();
        operation.SetStage("extracting_references");
        operation.ReportWork(1, 1, "documents");
        operation.Complete();

        var snapshot = observation.Snapshot();
        Assert.Null(snapshot.Activity);
        Assert.Null(snapshot.LastOperation);
    }

    [Fact]
    public async Task Disposal_does_not_wait_for_a_synchronous_resource_provider()
    {
        var sampler = new BlockingResourceSampler();
        var observation = new IndexingObservation(sampler, new FakeClock());
        observation.StartResourceSampling(TimeSpan.FromHours(1));
        await sampler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await observation.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        sampler.Release();
        await sampler.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.True(condition(), "The condition was not observed before the test deadline.");
    }

    private sealed class FakeClock : IObservationClock
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

        public long Timestamp => _timestamp;

        public DateTimeOffset UtcNow => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _timestamp += checked((long)(duration.TotalSeconds * Stopwatch.Frequency));
            _utcNow += duration;
        }
    }

    private sealed class FakeResourceSampler : IProcessResourceSampler
    {
        public ObservationResourceSnapshot Capture() => new(
            DateTimeOffset.UtcNow,
            1234,
            2345,
            3456,
            4567,
            5678,
            6789,
            1,
            12);
    }

    private sealed class BlockingResourceSampler : IProcessResourceSampler
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObservationResourceSnapshot Capture()
        {
            Entered.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
            Finished.TrySetResult(true);
            return new ObservationResourceSnapshot(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null);
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class FailingResourceSampler : IProcessResourceSampler
    {
        public ObservationResourceSnapshot Capture() =>
            throw new InvalidOperationException("resource provider unavailable");
    }
}
