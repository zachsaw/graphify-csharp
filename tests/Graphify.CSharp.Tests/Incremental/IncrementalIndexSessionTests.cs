using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class IncrementalIndexSessionTests
{
    [Fact]
    public async Task Refresh_submitted_during_startup_waits_for_one_workspace_and_warm_clean_refresh_is_cheap()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader(), TimeSpan.FromMilliseconds(250));
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath, loader);

            var start = session.StartAsync();
            var refresh = session.RefreshAsync();
            var first = await refresh;
            await start;
            var second = await session.RefreshAsync();

            Assert.Equal(1, loader.LoadCount);
            Assert.NotEmpty(first.Graph.Nodes);
            Assert.Equal(first.Generation!.PublishedGeneration, second.Generation!.PublishedGeneration);
            Assert.Equal(0, second.ExtractedProjectCount);
            Assert.False(second.OutputRepublished);
            Assert.Equal(IncrementalSessionStatus.Ready, session.Status);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Refresh_after_startup_failure_fails_instead_of_waiting()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                fixture.OutputPath,
                new FailingLoader());

            await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_source_event_refreshes_the_project_without_loading_a_second_workspace()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath, loader);
            await session.StartAsync();

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class AddedByWarmRefresh { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            var result = await session.RefreshAsync();

            Assert.Equal(1, loader.LoadCount);
            // The background indexer may have processed the event before the
            // foreground publication barrier. The observable contract is the
            // published graph, not which worker performed the extraction.
            Assert.Contains("ReferenceFixture.Production.AddedByWarmRefresh", result.Graph.Nodes.Select(node => node.Label));
            Assert.True(result.OutputRepublished);
            Assert.True(result.Generation!.PublishedGeneration >= 1);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_large_multi_file_project_refreshes_through_coarse_batches()
    {
        var fixture = await CreateLargeFixtureAsync();
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath, loader);
            await session.StartAsync();

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic static class AddedAfterBatchRefresh { public static int Value => 42; }\n");
            session.ReportFileChanged(fixture.SourcePath);
            var result = await session.RefreshAsync();

            Assert.Equal(1, loader.LoadCount);
            Assert.Contains(
                "BatchFixture.AddedAfterBatchRefresh",
                result.Graph.Nodes.Select(node => node.Label));
            Assert.Contains(
                result.Graph.Nodes,
                node => node.Properties.TryGetValue("declaration_kind", out var kind)
                    && kind == "class"
                    && node.Label == "BatchFixture.Type063");
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task An_evaluated_source_with_a_non_cs_extension_uses_the_warm_document_path()
    {
        var fixture = await CreateArbitrarySourceFixtureAsync();
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath, loader);
            await session.StartAsync();

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class AddedFromArbitraryExtension { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            var result = await session.RefreshAsync();

            Assert.Contains(
                "ArbitrarySourceFixture.AddedFromArbitraryExtension",
                result.Graph.Nodes.Select(node => node.Label));
            Assert.Equal(1, loader.LoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Multiple_refresh_callers_coalesce_on_one_published_generation()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath);
            await session.StartAsync();

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class CoalescedChange { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            var firstTask = session.RefreshAsync();
            var secondTask = session.RefreshAsync();
            var results = await Task.WhenAll(firstTask, secondTask);

            Assert.Equal(results[0].Generation!.PublishedGeneration, results[1].Generation!.PublishedGeneration);
            // Either foreground request may encounter a contribution already
            // prepared by the background indexer; both callers must still see
            // the same complete graph and only one publication.
            Assert.All(
                results,
                result => Assert.Contains(
                    "ReferenceFixture.Production.CoalescedChange",
                    result.Graph.Nodes.Select(node => node.Label)));
            Assert.Equal(1, results.Count(result => result.OutputRepublished));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Disposing_session_cancels_active_refresh_and_shares_disposal_completion()
    {
        var fixture = await CreateFixtureAsync();
        var committer = new BlockingCommitter();
        var session = new IncrementalIndexSession(
            fixture.Request,
            fixture.OutputPath,
            outputPublisher: new IncrementalOutputPublisher(committer: committer));
        try
        {
            await session.StartAsync();

            var refresh = session.RebuildAsync();
            await committer.SecondCommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));

            var firstDispose = session.DisposeAsync().AsTask();
            var secondDispose = session.DisposeAsync().AsTask();

            Assert.Same(firstDispose, secondDispose);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => refresh.WaitAsync(TimeSpan.FromSeconds(5)));

            committer.ReleaseSecondCommit();
            await firstDispose;
        }
        finally
        {
            committer.ReleaseSecondCommit();
            await session.DisposeAsync();
            committer.Dispose();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_project_file_event_takes_the_cold_reload_path()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath, loader);
            await session.StartAsync();

            session.ReportFileChanged(fixture.ProjectPath);
            var result = await session.RefreshAsync();

            Assert.Equal(2, loader.LoadCount);
            Assert.NotEmpty(result.Graph.Nodes);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_source_change_during_a_cold_reload_is_included_in_the_final_load()
    {
        var fixture = await CreateFixtureAsync();
        var loader = new BlockingReloadLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath, loader);
            await session.StartAsync();

            session.ReportFileChanged(fixture.ProjectPath);
            var refresh = session.RefreshAsync();
            await loader.ReloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class ChangedDuringReload { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            loader.ReleaseReload();

            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("ReferenceFixture.Production.ChangedDuringReload", result.Graph.Nodes.Select(node => node.Label));
            var loadCountAfterRefresh = loader.LoadCount;
            Assert.True(loadCountAfterRefresh >= 2);

            var followUp = await session.RefreshAsync();
            Assert.Contains("ReferenceFixture.Production.ChangedDuringReload", followUp.Graph.Nodes.Select(node => node.Label));
            // The transition snapshot deliberately classifies an event that
            // arrived during a cold load as requiring cold reconciliation. It
            // may therefore be consumed by this refresh or by one bounded
            // follow-up load, depending on when the worker drains the event.
            Assert.InRange(loader.LoadCount, loadCountAfterRefresh, loadCountAfterRefresh + 1);
        }
        finally
        {
            loader.ReleaseReload();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_change_after_roslyn_load_but_before_snapshot_publication_is_not_lost()
    {
        var fixture = await CreateFixtureAsync();
        var loader = new PostCompilationGateLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var session = new IncrementalIndexSession(fixture.Request, fixture.OutputPath, loader);
            await session.StartAsync();

            session.ReportFileChanged(fixture.ProjectPath);
            var refresh = session.RefreshAsync();
            await loader.SecondLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The second loader has already captured the Roslyn compilation,
            // but the session has not yet published its evaluated snapshot.
            // The transition snapshot must retain this event and force one
            // more authoritative reload after the gated load completes.
            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class ChangedAfterRoslynCapture { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            loader.ReleaseSecondLoad();

            await refresh.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await session.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains(
                "ReferenceFixture.Production.ChangedAfterRoslynCapture",
                result.Graph.Nodes.Select(node => node.Label));
            Assert.Equal(3, loader.LoadCount);
        }
        finally
        {
            loader.ReleaseSecondLoad();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_failed_warm_publish_keeps_the_previous_complete_output()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var committer = new FailOnSecondCommitter();
            var publisher = new IncrementalOutputPublisher(committer: committer);
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                fixture.OutputPath,
                outputPublisher: publisher);
            await session.StartAsync();
            var before = await File.ReadAllTextAsync(fixture.OutputPath);

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class FailedChange { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            await Assert.ThrowsAsync<IOException>(() => session.RefreshAsync());

            Assert.Equal(before, await File.ReadAllTextAsync(fixture.OutputPath));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_cache_save_failure_after_output_publication_is_reported_and_retried()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var cacheCommitter = new FailOnSecondCommitter();
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                fixture.OutputPath,
                cacheStore: new IncrementalCacheStore(cacheCommitter));
            await session.StartAsync();

            var cachePath = IncrementalCachePath.ForOutput(fixture.OutputPath);
            var beforeOutput = await File.ReadAllTextAsync(fixture.OutputPath);
            var beforeCache = await File.ReadAllTextAsync(cachePath);
            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class PublishedBeforeCacheFailure { }\n");
            session.ReportFileChanged(fixture.SourcePath);

            var failure = await Assert.ThrowsAsync<IOException>(() => session.RefreshAsync());

            Assert.Contains("Synthetic warm publish failure", failure.Message);
            var afterFailedOutput = await File.ReadAllTextAsync(fixture.OutputPath);
            Assert.NotEqual(beforeOutput, afterFailedOutput);
            Assert.Contains("PublishedBeforeCacheFailure", afterFailedOutput);
            Assert.Equal(beforeCache, await File.ReadAllTextAsync(cachePath));

            var retry = await session.RefreshAsync();

            Assert.Contains(
                "ReferenceFixture.Production.PublishedBeforeCacheFailure",
                retry.Graph.Nodes.Select(node => node.Label));
            Assert.NotEqual(beforeCache, await File.ReadAllTextAsync(cachePath));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_full_event_queue_marks_delivery_untrusted_without_blocking_the_callback()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var trustLost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loader = new CountingLoader(new RoslynWorkspaceLoader(), TimeSpan.FromSeconds(1));
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                fixture.OutputPath,
                loader,
                trustLostCallback: reason => trustLost.TrySetResult(reason));
            _ = session.StartAsync();

            for (var index = 0; index < 5000; index++)
            {
                session.ReportFileChanged(fixture.SourcePath);
            }

            var reason = await trustLost.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("queue", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var repositoryRoot = RepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-session-tests", Guid.NewGuid().ToString("N"));
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

    private static async Task<Fixture> CreateLargeFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-batch-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
#if NET11_0_OR_GREATER
        const string targetFramework = "net11.0";
#else
        const string targetFramework = "net10.0";
#endif
        var projectPath = Path.Combine(root, "BatchFixture.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        string? firstSourcePath = null;
        for (var index = 0; index < 64; index++)
        {
            var sourcePath = Path.Combine(root, $"Type{index:D3}.cs");
            await File.WriteAllTextAsync(
                sourcePath,
                $"namespace BatchFixture; public static class Type{index:D3} {{ public static int Value => {index}; }}\n");
            firstSourcePath ??= sourcePath;
        }

        return new Fixture(
            root,
            projectPath,
            firstSourcePath!,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: targetFramework));
    }

    private static async Task<Fixture> CreateArbitrarySourceFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-arbitrary-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "ArbitrarySourceFixture.csproj");
        var sourcePath = Path.Combine(root, "Generated.source");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Generated.source" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            sourcePath,
            "namespace ArbitrarySourceFixture; public sealed class Initial { }\n");
        return new Fixture(
            root,
            projectPath,
            sourcePath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
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

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
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

    private sealed class CountingLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TimeSpan _delay;

        public CountingLoader(IProjectLoader inner, TimeSpan? delay = null)
        {
            _inner = inner;
            _delay = delay ?? TimeSpan.Zero;
        }

        public int LoadCount { get; private set; }

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return await _inner.LoadAsync(request, cancellationToken);
        }
    }

    private sealed class BlockingReloadLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TaskCompletionSource<bool> _releaseReload =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;

        public BlockingReloadLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public int LoadCount => Volatile.Read(ref _loadCount);

        public TaskCompletionSource<bool> ReloadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            var loadCount = Interlocked.Increment(ref _loadCount);
            if (loadCount == 2)
            {
                ReloadEntered.TrySetResult(true);
                await _releaseReload.Task.WaitAsync(cancellationToken);
            }

            return await _inner.LoadAsync(request, cancellationToken);
        }

        public void ReleaseReload() => _releaseReload.TrySetResult(true);
    }

    private sealed class PostCompilationGateLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TaskCompletionSource<bool> _releaseSecondLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;

        public PostCompilationGateLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public int LoadCount => Volatile.Read(ref _loadCount);

        public TaskCompletionSource<bool> SecondLoadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            var loadCount = Interlocked.Increment(ref _loadCount);
            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (loadCount == 2)
            {
                SecondLoadCompleted.TrySetResult(true);
                await _releaseSecondLoad.Task.WaitAsync(cancellationToken);
            }

            return loaded;
        }

        public void ReleaseSecondLoad() => _releaseSecondLoad.TrySetResult(true);
    }

    private sealed class FailingLoader : IProjectLoader
    {
        public Task<LoadedSolution> LoadAsync(
            ProjectLoadRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<LoadedSolution>(
                new InvalidOperationException("Synthetic startup failure."));
    }

    private sealed class FailOnSecondCommitter : IAtomicCacheCommitter
    {
        private int _commitCount;

        public void Commit(string temporaryPath, string destinationPath)
        {
            if (Interlocked.Increment(ref _commitCount) == 2)
            {
                throw new IOException("Synthetic warm publish failure.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
    }

    private sealed class BlockingCommitter : IAtomicCacheCommitter, IDisposable
    {
        private readonly ManualResetEventSlim _releaseSecondCommit = new(false);
        private int _commitCount;

        public TaskCompletionSource<bool> SecondCommitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Commit(string temporaryPath, string destinationPath)
        {
            if (Interlocked.Increment(ref _commitCount) == 2)
            {
                SecondCommitStarted.TrySetResult(true);
                _releaseSecondCommit.Wait();
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }

        public void ReleaseSecondCommit() => _releaseSecondCommit.Set();

        public void Dispose() => _releaseSecondCommit.Dispose();
    }
}
