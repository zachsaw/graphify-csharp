using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class IncrementalWatcherHostTests
{
    [Fact]
    public async Task Backup_scan_finds_a_missed_change_without_publishing_until_refresh()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var scanner = new CountingInventoryScanner();
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromMilliseconds(50),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                inventoryScanner: scanner,
                watcherFactory: factory);

            await host.StartAsync();
            var before = await File.ReadAllTextAsync(fixture.OutputPath);
            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class BackupDetectedChange { }\n");

            await WaitUntilAsync(
                () => scanner.ScanCount >= 2 && host.Session.EventGeneration >= 1,
                TimeSpan.FromSeconds(10));
            Assert.Equal(before, await File.ReadAllTextAsync(fixture.OutputPath));

            var result = await host.RefreshAsync();

            Assert.Contains("ReferenceFixture.Production.BackupDetectedChange", result.Graph.Nodes.Select(node => node.Label));
            Assert.NotEqual(before, await File.ReadAllTextAsync(fixture.OutputPath));
            Assert.Equal(1, factory.CreateCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Watcher_failure_recreates_the_watcher_and_cold_reconciles()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            factory.Current.TriggerFailure(new IOException("synthetic overflow"));

            await WaitUntilAsync(
                () => factory.CreateCount >= 2 && loader.LoadCount >= 2 && host.IsReady,
                TimeSpan.FromSeconds(10));

            Assert.True(host.IsReady);
            Assert.True(factory.Current.IsStarted);
            Assert.Equal(2, loader.LoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Failed_backup_scan_invalidates_the_session_and_recovers()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var scanner = new FailNextInventoryScanner();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromMilliseconds(50),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                inventoryScanner: scanner,
                watcherFactory: factory);

            await host.StartAsync();
            scanner.FailNext();

            await WaitUntilAsync(
                () => factory.CreateCount >= 2 && loader.LoadCount >= 2 && host.IsReady,
                TimeSpan.FromSeconds(10));

            Assert.True(host.IsReady);
            Assert.True(scanner.ScanCount >= 3);
            Assert.Equal(2, loader.LoadCount);
            Assert.True(factory.Current.IsStarted);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Failed_background_index_invalidates_the_session_and_recovers()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new FailNextLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            loader.FailNext();
            factory.Current.TriggerPath(fixture.ProjectPath);

            await WaitUntilAsync(
                () => factory.CreateCount >= 2 && loader.LoadCount >= 3 && host.IsReady,
                TimeSpan.FromSeconds(10));

            Assert.True(host.IsReady);
            Assert.Equal(3, loader.LoadCount);
            Assert.True(factory.Current.IsStarted);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Local_refresh_client_uses_the_warm_watcher()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1)),
                watcherFactory: new FakeWatcherFactory());
            await host.StartAsync();

            var identity = new RefreshRequestIdentity(
                fixture.Request.InputPath,
                fixture.Request.RepositoryRoot,
                fixture.Request.Configuration,
                fixture.Request.TargetFramework);
            var response = await new IncrementalRefreshControlClient()
                .TryRefreshAsync(identity, fixture.OutputPath, rebuild: false);

            Assert.NotNull(response);
            Assert.True(response!.Success);
            Assert.Equal(0, response.ExtractedProjectCount);
            Assert.NotNull(response.NodeCount);
            Assert.True(response.NodeCount!.Value > 0);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var repositoryRoot = RepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-watcher-tests", Guid.NewGuid().ToString("N"));
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
            projectPath,
            Path.Combine(root, "ReferenceTypes.cs"),
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PLAN.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected watcher state was not reached.");
            }

            await Task.Delay(25);
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record Fixture(
        string Root,
        string ProjectPath,
        string SourcePath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed class CountingInventoryScanner : IFileInventoryScanner
    {
        private readonly FileInventoryScanner _inner = new();

        public int ScanCount { get; private set; }

        public async Task<FileInventorySnapshot> ScanAsync(
            IReadOnlyList<string> roots,
            string repositoryRoot,
            bool includeContentHashes = false,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            return await _inner.ScanAsync(roots, repositoryRoot, includeContentHashes, cancellationToken);
        }
    }

    private sealed class FailNextInventoryScanner : IFileInventoryScanner
    {
        private readonly FileInventoryScanner _inner = new();
        private int _failNext;

        public int ScanCount { get; private set; }

        public void FailNext() => Interlocked.Exchange(ref _failNext, 1);

        public async Task<FileInventorySnapshot> ScanAsync(
            IReadOnlyList<string> roots,
            string repositoryRoot,
            bool includeContentHashes = false,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            if (Interlocked.Exchange(ref _failNext, 0) != 0)
            {
                throw new InvalidDataException("synthetic backup inventory failure");
            }

            return await _inner.ScanAsync(roots, repositoryRoot, includeContentHashes, cancellationToken);
        }
    }

    private sealed class FakeWatcherFactory : IFileChangeWatcherFactory
    {
        public int CreateCount { get; private set; }

        public FakeWatcher Current { get; private set; } = null!;

        public IFileChangeWatcher Create(string root)
        {
            CreateCount++;
            Current = new FakeWatcher(root);
            return Current;
        }
    }

    private sealed class FakeWatcher : IFileChangeWatcher
    {
        private bool _disposed;

        public FakeWatcher(string root)
        {
            Root = root;
        }

        public event Action<string>? PathChanged;

        public event Action<Exception>? Failed;

        public string Root { get; }

        public bool IsStarted { get; private set; }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IsStarted = true;
        }

        public void TriggerFailure(Exception exception)
        {
            Failed?.Invoke(exception);
        }

        public void TriggerPath(string path)
        {
            PathChanged?.Invoke(path);
        }

        public void Dispose()
        {
            _disposed = true;
            IsStarted = false;
        }
    }

    private sealed class CountingLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;

        public CountingLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public int LoadCount { get; private set; }

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return await _inner.LoadAsync(request, cancellationToken);
        }
    }

    private sealed class FailNextLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private int _failNext;

        public FailNextLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public int LoadCount { get; private set; }

        public void FailNext() => Interlocked.Exchange(ref _failNext, 1);

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (Interlocked.Exchange(ref _failNext, 0) != 0)
            {
                throw new InvalidDataException("synthetic background indexing failure");
            }

            return await _inner.LoadAsync(request, cancellationToken);
        }
    }
}
