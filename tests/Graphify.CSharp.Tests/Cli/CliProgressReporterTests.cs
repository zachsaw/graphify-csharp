using System.Text;
using Graphify.CSharp.Cli;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Cli;

public sealed class CliProgressReporterTests
{
    [Fact]
    public async Task Slow_writer_cannot_hold_shutdown_forever()
    {
        await using var observation = new IndexingObservation();
        var writer = new BlockingWriter();
        await using var reporter = new CliProgressReporter(observation, writer);

        reporter.Start();
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = reporter.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(dispose, completed);
        await dispose;

        writer.ReleaseFirst.TrySetResult(true);
        await writer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Writer_failure_does_not_fail_reporter_shutdown()
    {
        await using var observation = new IndexingObservation();
        var writer = new ThrowingWriter();
        await using var reporter = new CliProgressReporter(observation, writer);

        reporter.Start();
        await writer.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await reporter.DisposeAsync();
    }

    [Fact]
    public async Task Slow_update_write_does_not_block_observation_or_shutdown()
    {
        await using var observation = new IndexingObservation();
        var writer = new BlockingWriter();
        await using var reporter = new CliProgressReporter(observation, writer);

        reporter.Start();
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writer.ReleaseFirst.TrySetResult(true);

        using var operation = observation.BeginOperation("refresh");
        operation.SetStage("loading_projects");
        await writer.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        operation.SetStage("cataloging");
        var dispose = reporter.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(dispose, completed);
        await dispose;

        writer.ReleaseSecond.TrySetResult(true);
        await writer.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Completion_summary_wins_over_a_stale_queued_update()
    {
        await using var observation = new IndexingObservation();
        var writer = new RecordingWriter();
        var updateCaptured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = 0;
        using var operation = observation.BeginOperation("refresh");
        operation.SetStage("loading_projects");
        await using var reporter = new CliProgressReporter(
            observation,
            writer,
            beforeUpdateQueue: () =>
            {
                if (Interlocked.Exchange(ref callbackEntered, 1) == 0)
                {
                    updateCaptured.TrySetResult(true);
                    releaseUpdate.Task.GetAwaiter().GetResult();
                }
            });

        reporter.Start();
        await updateCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reporter.WriteCompletionSummary("Completed");
        releaseUpdate.TrySetResult(true);
        await WaitUntilAsync(() => writer.Text.Contains("Completed;", StringComparison.Ordinal));
        await reporter.DisposeAsync();

        var output = writer.Text;
        var startingIndex = output.IndexOf("Starting;", StringComparison.Ordinal);
        var summaryIndex = output.IndexOf("Completed;", StringComparison.Ordinal);
        var staleUpdateIndex = output.IndexOf("loading_projects", StringComparison.Ordinal);
        Assert.True(startingIndex >= 0);
        Assert.True(summaryIndex > startingIndex);
        Assert.True(staleUpdateIndex > summaryIndex);
        Assert.Equal(1, CountOccurrences(output, "Completed;"));
    }

    [Fact]
    public async Task Completion_before_the_renderer_turn_preserves_starting_notice_order()
    {
        await using var observation = new IndexingObservation();
        var writer = new RecordingWriter();
        await using var reporter = new CliProgressReporter(observation, writer);

        reporter.Start();
        reporter.WriteCompletionSummary("Completed");
        await reporter.DisposeAsync();

        var output = writer.Text;
        var startingIndex = output.IndexOf("Starting;", StringComparison.Ordinal);
        var summaryIndex = output.IndexOf("Completed;", StringComparison.Ordinal);
        Assert.True(startingIndex >= 0);
        Assert.True(summaryIndex > startingIndex);
        Assert.Equal(1, CountOccurrences(output, "Completed;"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected progress message was not rendered before the test deadline.");
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
    }

    private sealed class BlockingWriter : TextWriter
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseSecond { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _writeCount;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            var write = Interlocked.Increment(ref _writeCount);
            (write == 1 ? Entered : SecondEntered).TrySetResult(true);
            try
            {
                (write == 1 ? ReleaseFirst : ReleaseSecond).Task.GetAwaiter().GetResult();
            }
            finally
            {
                Exited.TrySetResult(true);
            }
        }
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public TaskCompletionSource<bool> Attempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            Attempted.TrySetResult(true);
            throw new InvalidOperationException("writer failed");
        }
    }

    private sealed class RecordingWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _output.ToString();
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _output.AppendLine(value);
            }
        }
    }
}
