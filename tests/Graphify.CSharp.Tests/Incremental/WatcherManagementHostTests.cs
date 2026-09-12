using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class WatcherManagementHostTests
{
    [Fact]
    public async Task Registers_before_roslyn_startup_and_inspects_a_blocked_session()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(Path.GetDirectoryName(fixture.Root)!, $"state-{Guid.NewGuid():N}");
        var loader = new BlockingProjectLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            var start = host.StartAsync();
            var registry = new WatcherSessionRegistry(stateDirectory);
            var descriptor = await WaitForDescriptorAsync(registry);
            var response = await new WatcherManagementClient().InspectAsync(
                descriptor,
                TimeSpan.FromSeconds(2));

            Assert.True(response.Success);
            Assert.Equal("starting", response.Inspection!.LifecycleState);
            Assert.False(response.Inspection.Ready);
            Assert.True(loader.LoadEntered.Task.IsCompleted);

            loader.Release();
            await start.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(host.IsReady);
        }
        finally
        {
            loader.Release();
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task Remote_stop_during_startup_waits_for_owner_cleanup_and_does_not_self_deadlock()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(Path.GetDirectoryName(fixture.Root)!, $"state-{Guid.NewGuid():N}");
        var loader = new BlockingProjectLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                managementOptions: new WatcherManagementOptions(
                    stateDirectory,
                    "test",
                    stopTimeout: TimeSpan.FromSeconds(10)));
            var start = host.StartAsync();
            var registry = new WatcherSessionRegistry(stateDirectory);
            var descriptor = await WaitForDescriptorAsync(registry);
            var stop = new WatcherManagementClient().StopAsync(
                descriptor,
                TimeSpan.FromSeconds(10));
            await host.WaitForStopRequestedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);

            loader.Release();
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            var stopResponse = await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(stopResponse.Success);
            Assert.Equal("stopped", stopResponse.Inspection!.LifecycleState);
            Assert.False(stopResponse.Inspection.Ready);

            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                // The host was intentionally stopped while startup was waiting.
            }
            catch (ObjectDisposedException)
            {
                // The host was intentionally stopped while startup was waiting.
            }
        }
        finally
        {
            loader.Release();
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task Direct_host_disposal_completes_work_barrier_and_removes_descriptor()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(Path.GetDirectoryName(fixture.Root)!, $"state-{Guid.NewGuid():N}");
        try
        {
            var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var registry = new WatcherSessionRegistry(stateDirectory);
            var descriptor = await WaitForDescriptorAsync(registry);

            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            await host.WaitForWorkStoppedAsync(CancellationToken.None);

            Assert.DoesNotContain(
                registry.ReadAll(),
                record => record.Descriptor?.SessionId == descriptor.SessionId);
            Assert.False(WatcherLease.IsHeld(host.LeasePath));
            Assert.False(OutputDestinationLease.IsHeld(host.OutputLeasePath));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
        }
    }

    private static async Task<WatcherSessionDescriptor> WaitForDescriptorAsync(
        WatcherSessionRegistry registry)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var descriptor = registry.ReadAll()
                .Where(record => record.Descriptor is not null)
                .Select(record => record.Descriptor!)
                .SingleOrDefault();
            if (descriptor is not null)
            {
                return descriptor;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The watcher did not publish a management descriptor.");
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-management-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceRoot = Path.Combine(RepositoryRoot(), "tests", "Fixtures", "ReferenceFixture");
        foreach (var sourcePath in Directory.GetFiles(sourceRoot, "*.cs"))
        {
            File.Copy(sourcePath, Path.Combine(root, Path.GetFileName(sourcePath)));
        }

        var projectPath = Path.Combine(root, "ReferenceFixture.csproj");
        File.Copy(Path.Combine(sourceRoot, "ReferenceFixture.csproj"), projectPath);
        return new Fixture(
            root,
            new ProjectLoadRequest(projectPath, root, "Release", "net10.0"),
            Path.Combine(root, "graphify-out", "csharp.json"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Graphify.CSharp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Fixture(string Root, ProjectLoadRequest Request, string OutputPath);

    private sealed class BlockingProjectLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;

        public BlockingProjectLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource<bool> LoadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<bool> ReleaseSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LoadedSolution> LoadAsync(
            ProjectLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            LoadEntered.TrySetResult(true);
            await ReleaseSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.LoadAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public void Release() => ReleaseSignal.TrySetResult(true);
    }
}
