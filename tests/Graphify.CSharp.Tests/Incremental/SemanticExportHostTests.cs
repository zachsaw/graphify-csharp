using Graphify.CSharp.Cli;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class SemanticExportHostTests
{
    [Fact]
    public async Task Query_only_export_is_explicit_and_does_not_become_canonical_publication()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-export-tests",
            Guid.NewGuid().ToString("N"));
        var exportPath = Path.Combine(fixture.Root, "exports", "snapshot.json");
        IncrementalWatcherHost? host = null;
        try
        {
            host = new IncrementalWatcherHost(
                fixture.Request,
                outputPath: null,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var descriptor = await WaitForDescriptorAsync(new WatcherSessionRegistry(stateDirectory));
            var client = new SemanticQueryClient();

            var firstExport = await client.SendAsync(
                descriptor,
                new SemanticQuerySpec("export", OutputPath: exportPath),
                TimeSpan.FromSeconds(60));
            Assert.True(firstExport.Success, firstExport.Error?.Message);
            Assert.NotNull(firstExport.Export);
            Assert.Equal(Path.GetFullPath(exportPath), firstExport.Export!.OutputPath);
            Assert.True(File.Exists(exportPath));
            Assert.Null(descriptor.OutputPath);
            Assert.Equal(IncrementalHashing.Sha256File(exportPath), firstExport.Export.OutputDigest);

            var before = await File.ReadAllBytesAsync(exportPath);
            var inspection = await new WatcherManagementClient()
                .InspectAsync(descriptor, TimeSpan.FromSeconds(2));
            Assert.True(inspection.Success, inspection.Message);
            Assert.Null(inspection.Inspection!.OutputPath);
            Assert.Null(inspection.Inspection.PublishedGeneration);

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class AddedAfterExplicitExport { }\n");
            host.Session.ReportFileChanged(fixture.SourcePath);
            var current = await client.SendAsync(
                descriptor,
                new SemanticQuerySpec("symbols", Search: "AddedAfterExplicitExport", Limit: 10),
                TimeSpan.FromSeconds(60));
            Assert.True(current.Success, current.Error?.Message);
            Assert.Contains(
                current.Items,
                item => item.GetProperty("label").GetString()
                    == "ReferenceFixture.Production.AddedAfterExplicitExport");
            Assert.Equal(before, await File.ReadAllBytesAsync(exportPath));

            var secondExport = await client.SendAsync(
                descriptor,
                new SemanticQuerySpec("export", OutputPath: exportPath),
                TimeSpan.FromSeconds(60));
            Assert.True(secondExport.Success, secondExport.Error?.Message);
            Assert.NotEqual(firstExport.Export.OutputDigest, secondExport.Export!.OutputDigest);
            Assert.Equal(secondExport.Export.OutputDigest, IncrementalHashing.Sha256File(exportPath));
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }

            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task Export_releases_a_destination_only_after_a_cancelled_commit_finishes()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-export-tests",
            Guid.NewGuid().ToString("N"));
        var exportPath = Path.Combine(fixture.Root, "exports", "cancelled.json");
        var committer = new BlockingCommitter();
        IncrementalWatcherHost? host = null;
        try
        {
            host = new IncrementalWatcherHost(
                fixture.Request,
                outputPath: null,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                outputPublisher: new IncrementalOutputPublisher(committer: committer),
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var descriptor = await WaitForDescriptorAsync(new WatcherSessionRegistry(stateDirectory));

            using var cancellation = new CancellationTokenSource();
            var export = new SemanticQueryClient().SendAsync(
                descriptor,
                new SemanticQuerySpec("export", OutputPath: exportPath),
                TimeSpan.FromSeconds(60),
                cancellation.Token);
            await committer.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => export.WaitAsync(TimeSpan.FromSeconds(10)));

            var conflict = Assert.Throws<InvalidOperationException>(
                () => OutputDestinationLease.Acquire(exportPath));
            Assert.Contains("already owned", conflict.Message, StringComparison.Ordinal);

            committer.Release();
            await WaitForLeaseAvailabilityAsync(exportPath);
        }
        finally
        {
            committer.Release();
            if (host is not null)
            {
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }

            committer.Dispose();
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task Export_rejects_the_watcher_internal_state_directory()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-export-tests",
            Guid.NewGuid().ToString("N"));
        var internalPath = Path.Combine(
            Path.GetDirectoryName(fixture.OutputPath)!,
            ".graphify-csharp",
            "replacement.json");
        IncrementalWatcherHost? host = null;
        try
        {
            host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var descriptor = await WaitForDescriptorAsync(new WatcherSessionRegistry(stateDirectory));

            var response = await new SemanticQueryClient().SendAsync(
                descriptor,
                new SemanticQuerySpec("export", OutputPath: internalPath),
                TimeSpan.FromSeconds(60));

            Assert.False(response.Success);
            Assert.Equal("output_conflict", response.Error!.Code);
            Assert.Contains("internal state directory", response.Error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(internalPath));
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }

            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task Export_rejects_filesystem_aliases_to_inputs_and_state()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-export-tests",
            Guid.NewGuid().ToString("N"));
        var sourceAlias = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            $"{Path.GetFileName(fixture.Root)}-source-alias");
        var stateAlias = Path.Combine(
            Path.GetDirectoryName(stateDirectory)!,
            $"{Path.GetFileName(stateDirectory)}-alias");
        IncrementalWatcherHost? host = null;
        try
        {
            Directory.CreateDirectory(stateDirectory);
            try
            {
                Directory.CreateSymbolicLink(sourceAlias, fixture.Root);
                Directory.CreateSymbolicLink(stateAlias, stateDirectory);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(
                    $"The test environment does not allow directory aliases: {exception.Message}");
            }
            catch (PlatformNotSupportedException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(
                    $"The test environment does not support directory aliases: {exception.Message}");
            }

            Assert.True(
                IncrementalPaths.TryResolvePhysicalPath(
                    Path.Combine(stateAlias, "forged.json"),
                    out var resolvedStateDestination));
            Assert.True(IncrementalPaths.TryResolvePhysicalPath(stateDirectory, out var resolvedStateDirectory));
            Assert.Equal(
                IncrementalPaths.CanonicalAbsolutePath(Path.Combine(resolvedStateDirectory, "forged.json")),
                resolvedStateDestination);
            Assert.True(
                IncrementalPaths.IsPathOrUnder(resolvedStateDestination, resolvedStateDirectory),
                $"destination={resolvedStateDestination}; directory={resolvedStateDirectory}");

            host = new IncrementalWatcherHost(
                fixture.Request,
                outputPath: null,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var descriptor = await WaitForDescriptorAsync(new WatcherSessionRegistry(stateDirectory));
            var client = new SemanticQueryClient();
            var originalSource = await File.ReadAllBytesAsync(fixture.SourcePath);

            var sourceAliasResponse = await client.SendAsync(
                descriptor,
                new SemanticQuerySpec(
                    "export",
                    OutputPath: Path.Combine(sourceAlias, Path.GetFileName(fixture.SourcePath))),
                TimeSpan.FromSeconds(60));
            Assert.False(sourceAliasResponse.Success);
            Assert.Equal("output_conflict", sourceAliasResponse.Error!.Code);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(fixture.SourcePath));

            var stateAliasResponse = await client.SendAsync(
                descriptor,
                new SemanticQuerySpec(
                    "export",
                    OutputPath: Path.Combine(stateAlias, "forged.json")),
                TimeSpan.FromSeconds(60));
            Assert.False(stateAliasResponse.Success);
            Assert.Equal("output_conflict", stateAliasResponse.Error!.Code);
            Assert.False(File.Exists(Path.Combine(stateDirectory, "forged.json")));
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }

            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
            DeleteTemporaryDirectory(sourceAlias);
            DeleteTemporaryDirectory(stateAlias);
        }
    }

    [Fact]
    public async Task Export_rejects_a_differently_named_file_alias_to_an_input()
    {
        var fixture = await CreateFixtureAsync();
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-export-tests",
            Guid.NewGuid().ToString("N"));
        var physicalRoot = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            $"{Path.GetFileName(fixture.Root)}-physical-source");
        var physicalSource = Path.Combine(physicalRoot, "ActualSource.cs");
        var aliasedSource = Path.Combine(fixture.Root, "EvaluatedAlias.cs");
        IncrementalWatcherHost? host = null;
        try
        {
            Directory.CreateDirectory(physicalRoot);
            await File.WriteAllTextAsync(
                physicalSource,
                "namespace ReferenceFixture.Production; public sealed class DifferentlyNamedSource { }");
            try
            {
                File.CreateSymbolicLink(aliasedSource, physicalSource);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(
                    $"The test environment does not allow file aliases: {exception.Message}");
            }
            catch (PlatformNotSupportedException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(
                    $"The test environment does not support file aliases: {exception.Message}");
            }

            Assert.True(
                IncrementalPaths.TryResolvePhysicalPath(aliasedSource, out var resolvedAlias));
            Assert.True(
                IncrementalPaths.TryResolvePhysicalPath(physicalSource, out var resolvedTarget));
            Assert.Equal(resolvedTarget, resolvedAlias);
            Assert.NotEqual(
                Path.GetFileName(aliasedSource),
                Path.GetFileName(resolvedAlias));

            host = new IncrementalWatcherHost(
                fixture.Request,
                outputPath: null,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                managementOptions: new WatcherManagementOptions(stateDirectory, "test"));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var descriptor = await WaitForDescriptorAsync(new WatcherSessionRegistry(stateDirectory));
            var originalSource = await File.ReadAllBytesAsync(physicalSource);

            var response = await new SemanticQueryClient().SendAsync(
                descriptor,
                new SemanticQuerySpec("export", OutputPath: physicalSource),
                TimeSpan.FromSeconds(60));

            Assert.False(response.Success);
            Assert.Equal("output_conflict", response.Error!.Code);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(physicalSource));
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }

            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(stateDirectory);
            DeleteTemporaryDirectory(physicalRoot);
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
            "graphify-csharp-semantic-export-fixtures",
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
            Path.Combine(root, "ReferenceTypes.cs"),
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
        string SourcePath,
        string OutputPath,
        ProjectLoadRequest Request);

    private static async Task WaitForLeaseAvailabilityAsync(string outputPath)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var lease = OutputDestinationLease.Acquire(outputPath);
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(25);
            }
        }

        throw new TimeoutException("The export destination lease was not released after the commit completed.");
    }

    private sealed class BlockingCommitter : IAtomicCacheCommitter, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        public TaskCompletionSource<bool> CommitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Commit(string temporaryPath, string destinationPath)
        {
            CommitStarted.TrySetResult(true);
            _release.Wait();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
