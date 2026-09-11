using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class FileSystemChangeWatcherTests
{
    [Fact]
    public async Task Native_watcher_filters_obj_noise_and_delivers_a_relevant_source_event()
    {
        var root = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(root, "src", "Relevant.cs");
        FileSystemChangeWatcher? watcher = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            await File.WriteAllTextAsync(sourcePath, "class Relevant { }");
            var snapshot = WatcherInputSnapshot.CreateForTests(
                [sourcePath],
                [],
                [new KeyValuePair<string, IEnumerable<string>>(sourcePath, ["project=Fixture|tfm=net10.0"])],
                [root],
                Path.Combine(root, "graphify-out", "csharp.json"),
                Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json"));
            var captureObservations = new System.Collections.Concurrent.ConcurrentQueue<(FileChangeEvent Change, bool Accepted)>();
            var delivered = new System.Collections.Concurrent.ConcurrentQueue<FileChangeEvent>();
            var received = new TaskCompletionSource<FileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var noisePaths = new[]
            {
                Path.Combine(root, "obj", "Noise.cs"),
                Path.Combine(root, "obj", "Noise.props"),
                Path.Combine(root, "obj", "Noise.targets"),
            };
            watcher = new FileSystemChangeWatcher(
                new WatcherRoot(root, IncludeSubdirectories: true),
                change =>
                {
                    var accepted = snapshot.Classify(change).Accepted;
                    captureObservations.Enqueue((change, accepted));
                    return accepted;
                });
            watcher.PathChanged += change =>
            {
                delivered.Enqueue(change);
                if (string.Equals(
                    IncrementalPaths.CanonicalAbsolutePath(change.Path),
                    IncrementalPaths.CanonicalAbsolutePath(sourcePath),
                    IncrementalPaths.PathComparison))
                {
                    received.TrySetResult(change);
                }
            };

            watcher.Start();
            foreach (var noisePath in noisePaths)
            {
                await File.WriteAllTextAsync(noisePath, "class Noise { }");
            }
            await File.AppendAllTextAsync(sourcePath, "\nclass RelevantTwo { }");

            var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => noisePaths.All(noisePath => captureObservations.Any(observation =>
                    string.Equals(
                        IncrementalPaths.CanonicalAbsolutePath(observation.Change.Path),
                        IncrementalPaths.CanonicalAbsolutePath(noisePath),
                        IncrementalPaths.PathComparison))),
                TimeSpan.FromSeconds(5));

            Assert.Equal(sourcePath, IncrementalPaths.CanonicalAbsolutePath(change.Path));
            Assert.False(change.IsDirectory);
            Assert.All(
                captureObservations
                    .Where(observation => noisePaths.Any(noisePath =>
                        string.Equals(
                            IncrementalPaths.CanonicalAbsolutePath(observation.Change.Path),
                            IncrementalPaths.CanonicalAbsolutePath(noisePath),
                            IncrementalPaths.PathComparison))),
                observation => Assert.False(observation.Accepted));
            Assert.DoesNotContain(
                delivered,
                deliveredChange => IncrementalPaths.IsUnderDirectory(
                    deliveredChange.Path,
                    Path.Combine(root, "obj")));
        }
        finally
        {
            watcher?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Native_watcher_preserves_both_rename_endpoints()
    {
        var root = CreateTemporaryDirectory();
        var oldPath = Path.Combine(root, "src", "Old.cs");
        var newPath = Path.Combine(root, "src", "New.cs");
        FileSystemChangeWatcher? watcher = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
            await File.WriteAllTextAsync(oldPath, "class Renamed { }");
            var snapshot = WatcherInputSnapshot.CreateForTests(
                [oldPath],
                [],
                [new KeyValuePair<string, IEnumerable<string>>(oldPath, ["project=Fixture|tfm=net10.0"])],
                [root],
                Path.Combine(root, "graphify-out", "csharp.json"),
                Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json"));
            var received = new TaskCompletionSource<FileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher = new FileSystemChangeWatcher(
                new WatcherRoot(root, IncludeSubdirectories: true),
                change => change.Kind == FileChangeKind.Renamed && snapshot.Classify(change).Accepted);
            watcher.PathChanged += change => received.TrySetResult(change);
            watcher.Start();

            File.Move(oldPath, newPath);

            var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(FileChangeKind.Renamed, change.Kind);
            Assert.Equal(newPath, IncrementalPaths.CanonicalAbsolutePath(change.Path));
            Assert.Equal(oldPath, IncrementalPaths.CanonicalAbsolutePath(change.OldPath!));
        }
        finally
        {
            watcher?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Native_watcher_reports_a_directory_move_with_both_endpoints()
    {
        var root = CreateTemporaryDirectory();
        var oldDirectory = Path.Combine(root, "src", "old");
        var newDirectory = Path.Combine(root, "src", "new");
        var sourcePath = Path.Combine(oldDirectory, "Existing.cs");
        FileSystemChangeWatcher? watcher = null;
        try
        {
            Directory.CreateDirectory(oldDirectory);
            await File.WriteAllTextAsync(sourcePath, "class Existing { }");
            var snapshot = WatcherInputSnapshot.CreateForTests(
                [sourcePath],
                [],
                [new KeyValuePair<string, IEnumerable<string>>(sourcePath, ["project=Fixture|tfm=net10.0"])],
                [root],
                Path.Combine(root, "graphify-out", "csharp.json"),
                Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json"));
            var received = new TaskCompletionSource<FileChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher = new FileSystemChangeWatcher(
                new WatcherRoot(root, IncludeSubdirectories: true),
                change => change.Kind == FileChangeKind.Renamed && snapshot.Classify(change).Accepted);
            watcher.PathChanged += change =>
            {
                if (string.Equals(change.Path, newDirectory, IncrementalPaths.PathComparison)
                    && string.Equals(change.OldPath, oldDirectory, IncrementalPaths.PathComparison))
                {
                    received.TrySetResult(change);
                }
            };
            watcher.Start();

            Directory.Move(oldDirectory, newDirectory);

            var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(FileChangeKind.Renamed, change.Kind);
            Assert.True(change.IsDirectory);
            Assert.Equal(newDirectory, IncrementalPaths.CanonicalAbsolutePath(change.Path));
            Assert.Equal(oldDirectory, IncrementalPaths.CanonicalAbsolutePath(change.OldPath!));
        }
        finally
        {
            watcher?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Native_watcher_can_be_disposed_from_a_path_callback_after_dispatch_await()
    {
        var root = CreateTemporaryDirectory();
        FileSystemChangeWatcher? watcher = null;
        try
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher = new FileSystemChangeWatcher(new WatcherRoot(root, IncludeSubdirectories: true), _ => true);
            watcher.PathChanged += _ =>
            {
                watcher!.Dispose();
                completed.TrySetResult(true);
            };
            watcher.Start();

            await File.WriteAllTextAsync(Path.Combine(root, "CallbackDispose.cs"), "class CallbackDispose { }");

            Assert.True(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            watcher?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task External_disposal_waits_for_a_gated_dispatch_callback_then_completes()
    {
        var root = CreateTemporaryDirectory();
        FileSystemChangeWatcher? watcher = null;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            watcher = new FileSystemChangeWatcher(new WatcherRoot(root, IncludeSubdirectories: true), _ =>
            {
                entered.Set();
                release.Wait();
                return false;
            });
            watcher.Start();
            await File.WriteAllTextAsync(Path.Combine(root, "Gated.cs"), "class Gated { }");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            var disposal = Task.Run(watcher.Dispose);
            await Task.Delay(100);
            Assert.False(disposal.IsCompleted);
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            watcher?.Dispose();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Concurrent_disposal_is_idempotent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var watcher = new FileSystemChangeWatcher(
                new WatcherRoot(root, IncludeSubdirectories: true),
                _ => false);
            watcher.Start();

            var disposals = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(watcher.Dispose))
                .ToArray();
            await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-native-watcher-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected native watcher state was not reached.");
            }

            await Task.Delay(25);
        }
    }
}
