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
            Assert.Equal(1, result.ExtractedProjectCount);
            Assert.Contains("ReferenceFixture.Production.AddedByWarmRefresh", result.Graph.Nodes.Select(node => node.Label));
            Assert.True(result.Generation!.PublishedGeneration >= 1);
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
            Assert.Equal(1, results.Sum(result => result.ExtractedProjectCount));
            Assert.Contains(results, result => result.ExtractedProjectCount == 0);
        }
        finally
        {
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
            Assert.True(result.ExtractedProjectCount > 0);
        }
        finally
        {
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
}
