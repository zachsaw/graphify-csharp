using Graphify.CSharp.Cli;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class SemanticQueryHostTests
{
    [Fact]
    public async Task Query_only_host_publishes_semantic_capability_and_queries_wait_for_startup()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-host-tests",
            Guid.NewGuid().ToString("N"));
        var loader = new BlockingProjectLoader(new RoslynWorkspaceLoader());
        IncrementalWatcherHost? host = null;
        try
        {
            host = new IncrementalWatcherHost(
                fixture.Request,
                outputPath: null,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            var start = host.StartAsync();
            var registry = new WatcherSessionRegistry(stateDirectory);
            var descriptor = await WaitForDescriptorAsync(registry);
            await loader.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(descriptor.OutputPath);
            Assert.NotNull(descriptor.SemanticEndpoint);
            Assert.Equal(SemanticQueryProtocol.CurrentVersion, descriptor.SemanticProtocolVersion);

            var inspection = await new WatcherManagementClient()
                .InspectAsync(descriptor, TimeSpan.FromSeconds(2));
            Assert.True(inspection.Success, inspection.Message);
            Assert.Null(inspection.Inspection!.OutputPath);
            Assert.Null(inspection.Inspection.PublishedGeneration);
            Assert.False(inspection.Inspection.Ready);

            var query = new SemanticQueryClient().SendAsync(
                descriptor,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "Called",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 10),
                TimeSpan.FromSeconds(60));
            Assert.False(query.IsCompleted);

            loader.Release();
            await start.WaitAsync(TimeSpan.FromSeconds(60));
            var response = await query.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(response.Success, response.Error?.Message);
            Assert.Equal("instance", response.Mode);
            Assert.Equal(descriptor.SessionId, response.SessionId);
            Assert.Equal(2, response.Items.Count);
            Assert.False(File.Exists(fixture.OutputPath));
            Assert.False(File.Exists(IncrementalCachePath.ForOutput(fixture.OutputPath)));
        }
        finally
        {
            loader.Release();
            if (host is not null)
            {
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }

            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_cold_semantic_work_wakes_shared_recovery_for_the_next_request(bool export)
    {
        var fixture = await CreateFixtureAsync();
        var importPath = Path.Combine(fixture.Root, "build", "Custom.props");
        Directory.CreateDirectory(Path.GetDirectoryName(importPath)!);
        await File.WriteAllTextAsync(
            fixture.Request.InputPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <Import Project="build/Custom.props" />
            </Project>
            """);
        await File.WriteAllTextAsync(importPath, "<Project />");

        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-host-tests",
            Guid.NewGuid().ToString("N"));
        var loader = new CancelledColdLoadLoader(new RoslynWorkspaceLoader());
        var watcherFactory = new NoopWatcherFactory();
        IncrementalWatcherHost? host = null;
        try
        {
            host = new IncrementalWatcherHost(
                fixture.Request,
                outputPath: null,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: new CorruptFirstDiscoveryLoader(
                    loader,
                    importPath),
                watcherFactory: watcherFactory,
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(host.Session.InputSnapshot.InputDiscoveryComplete);
            var descriptor = await WaitForDescriptorAsync(new WatcherSessionRegistry(stateDirectory));
            var outputPath = Path.Combine(fixture.Root, "exports", "cancelled.json");

            loader.ArmNextLoad();
            using var cancellation = new CancellationTokenSource();
            var cancelled = new SemanticQueryClient().SendAsync(
                descriptor,
                export
                    ? new SemanticQuerySpec("export", OutputPath: outputPath)
                    : new SemanticQuerySpec("symbols", Search: "Called", Limit: 10),
                TimeSpan.FromSeconds(60),
                cancellation.Token);
            await loader.ColdLoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(60));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelled.WaitAsync(TimeSpan.FromSeconds(10)));

            // No file event or backup tick is produced after cancellation. The
            // host must have scheduled its own shared recovery before B can
            // pass the healthy-boundary wait.
            SemanticQueryResponse response;
            try
            {
                response = await new SemanticQueryClient().SendAsync(
                    descriptor,
                    new SemanticQuerySpec("symbols", Search: "Called", Limit: 10),
                    TimeSpan.FromMinutes(3));
            }
            catch (Exception exception)
            {
                var inspection = host.GetInspectionSnapshot();
                throw new Xunit.Sdk.XunitException(
                    $"The post-cancellation query did not complete: {exception.Message}; "
                    + $"lifecycle={inspection.LifecycleState}; ready={inspection.Ready}; "
                    + $"session_status={host.Session.Status}; bootstrap={host.Session.InputSnapshot.IsBootstrap}; "
                    + $"input_complete={host.Session.InputSnapshot.InputDiscoveryComplete}; "
                    + $"loads={loader.LoadCount}; events={host.Session.EventGeneration}/{host.Session.Generation.IndexedGeneration}");
            }
            Assert.True(response.Success, response.Error?.Message);
            Assert.Contains(
                response.Items,
                item => item.GetProperty("label").GetString() is { } label
                    && label.StartsWith(
                        "ReferenceFixture.Production.Service.Called(",
                        StringComparison.Ordinal));
            Assert.True(host.IsReady);
            if (export)
            {
                Assert.False(File.Exists(outputPath));
            }
        }
        finally
        {
            loader.Release();
            if (host is not null)
            {
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }

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

        throw new TimeoutException("The watcher did not publish a semantic descriptor.");
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var repositoryRoot = RepositoryRoot();
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-host-fixtures",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceRoot = Path.Combine(repositoryRoot, "tests", "Fixtures", "ReferenceFixture");
        foreach (var sourcePath in Directory.GetFiles(sourceRoot, "*.cs"))
        {
            File.Copy(sourcePath, Path.Combine(root, Path.GetFileName(sourcePath)));
        }

        var projectPath = Path.Combine(root, "ReferenceFixture.csproj");
        File.Copy(Path.Combine(sourceRoot, "ReferenceFixture.csproj"), projectPath);
        return new Fixture(
            root,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, "Release", "net10.0"));
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

    private sealed record Fixture(
        string Root,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed class BlockingProjectLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingProjectLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource<bool> LoadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LoadedSolution> LoadAsync(
            ProjectLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            LoadEntered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return await _inner.LoadAsync(request, cancellationToken);
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class CorruptFirstDiscoveryLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly string _importPath;
        private int _corrupted;

        public CorruptFirstDiscoveryLoader(IProjectLoader inner, string importPath)
        {
            _inner = inner;
            _importPath = importPath;
        }

        public async Task<LoadedSolution> LoadAsync(
            ProjectLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (Interlocked.Exchange(ref _corrupted, 1) == 0)
            {
                await File.WriteAllTextAsync(_importPath, "<Project><PropertyGroup>", cancellationToken);
            }

            return loaded;
        }
    }

    private sealed class CancelledColdLoadLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;
        private int _gateNextLoad;

        public CancelledColdLoadLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource<bool> ColdLoadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount => Volatile.Read(ref _loadCount);

        public void ArmNextLoad() => Interlocked.Exchange(ref _gateNextLoad, 1);

        public async Task<LoadedSolution> LoadAsync(
            ProjectLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (Interlocked.Exchange(ref _gateNextLoad, 0) == 0)
            {
                return loaded;
            }

            ColdLoadEntered.TrySetResult(true);
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return loaded;
            }
            catch
            {
                loaded.Dispose();
                throw;
            }
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class NoopWatcherFactory : IFileChangeWatcherFactory
    {
        public IFileChangeWatcher Create(
            WatcherRoot root,
            Func<FileChangeEvent, FileChangeEvent?> shouldCapture) =>
            new NoopWatcher(root.CanonicalPath);
    }

    private sealed class NoopWatcher : IFileChangeWatcher
    {
        public NoopWatcher(string root)
        {
            Root = root;
        }

        public event Action<FileChangeEvent>? PathChanged
        {
            add { }
            remove { }
        }

        public event Action<Exception>? Failed
        {
            add { }
            remove { }
        }

        public string Root { get; }

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }
}
