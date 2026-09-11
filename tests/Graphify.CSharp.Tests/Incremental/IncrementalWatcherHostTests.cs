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
            var initialWatcherCount = factory.CreateCount;
            var initialScanCount = scanner.ScanCount;
            var initialEventGeneration = host.Session.EventGeneration;
            var before = await File.ReadAllTextAsync(fixture.OutputPath);
            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class BackupDetectedChange { }\n");

            await WaitUntilAsync(
                () => scanner.ScanCount > initialScanCount
                    && host.Session.EventGeneration > initialEventGeneration,
                TimeSpan.FromSeconds(10));
            Assert.Equal(before, await File.ReadAllTextAsync(fixture.OutputPath));

            var result = await host.RefreshAsync();

            Assert.Contains("ReferenceFixture.Production.BackupDetectedChange", result.Graph.Nodes.Select(node => node.Label));
            Assert.NotEqual(before, await File.ReadAllTextAsync(fixture.OutputPath));
            Assert.Equal(initialWatcherCount, factory.CreateCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Watcher_host_drops_build_output_noise_before_it_reaches_the_session()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            var initialLoadCount = loader.LoadCount;
            var initialEventGeneration = host.Session.EventGeneration;
            var objDirectory = Path.Combine(fixture.Root, "obj", "noise");
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Created,
                objDirectory,
                IsDirectory: true));
            foreach (var extension in new[] { ".cs", ".props", ".targets" })
            {
                factory.Current.TriggerChange(new FileChangeEvent(
                    FileChangeKind.Created,
                    Path.Combine(objDirectory, $"Noise0000{extension}"),
                    IsDirectory: false));
            }

            Assert.Equal(initialLoadCount, loader.LoadCount);
            Assert.Equal(initialEventGeneration, host.Session.EventGeneration);
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
            var initialLoadCount = loader.LoadCount;
            factory.Current.TriggerFailure(new IOException("synthetic overflow"));

            await WaitUntilAsync(
                () => factory.CreateCount > 0 && loader.LoadCount > initialLoadCount && host.IsReady,
                TimeSpan.FromSeconds(10));

            Assert.True(host.IsReady);
            Assert.True(factory.Current.IsStarted);
            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Disposing_while_watcher_starts_does_not_leave_an_unowned_watcher()
    {
        var fixture = await CreateFixtureAsync();
        var factory = new BlockingWatcherFactory();
        var host = new IncrementalWatcherHost(
            fixture.Request,
            fixture.OutputPath,
            new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
            watcherFactory: factory);
        try
        {
            var start = Task.Run(() => host.StartAsync());
            await factory.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var dispose = Task.Run(() => host.DisposeAsync().AsTask());
            Assert.False(dispose.IsCompleted);

            factory.ReleaseStart();
            try
            {
                await start;
            }
            catch (OperationCanceledException)
            {
                // Disposal may cancel the cold start after the watcher has
                // been published. The ownership invariant is what matters.
            }

            await dispose;
            Assert.True(factory.Current.IsDisposed);
        }
        finally
        {
            factory.ReleaseStart();
            await host.DisposeAsync();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cancelled_startup_keeps_destination_owned_until_the_session_stops(int blockedCommitNumber)
    {
        var fixture = await CreateFixtureAsync();
        var committer = new BlockingCommitter(blockedCommitNumber);
        var host = new IncrementalWatcherHost(
            fixture.Request,
            fixture.OutputPath,
            new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
            cacheStore: new IncrementalCacheStore(committer),
            outputPublisher: new IncrementalOutputPublisher(committer: committer),
            watcherFactory: new FakeWatcherFactory());
        var start = host.StartAsync();
        try
        {
            await committer.BlockedCommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));

            var dispose = host.DisposeAsync().AsTask();
            Assert.False(dispose.IsCompleted);
            Assert.Throws<InvalidOperationException>(
                () => OutputDestinationLease.Acquire(fixture.OutputPath));

            committer.ReleaseBlockedCommit();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await start);
            await dispose;

            using var successor = OutputDestinationLease.Acquire(fixture.OutputPath);
            Assert.True(OutputDestinationLease.IsHeld(successor.Path));
        }
        finally
        {
            committer.ReleaseBlockedCommit();
            await host.DisposeAsync();
            committer.Dispose();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Concurrent_host_disposals_share_one_completion()
    {
        var fixture = await CreateFixtureAsync();
        var host = new IncrementalWatcherHost(
            fixture.Request,
            fixture.OutputPath,
            new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
            watcherFactory: new FakeWatcherFactory());
        try
        {
            await host.StartAsync();

            var firstDispose = host.DisposeAsync().AsTask();
            var secondDispose = host.DisposeAsync().AsTask();

            Assert.Same(firstDispose, secondDispose);
            await Task.WhenAll(firstDispose, secondDispose);
            Assert.False(host.IsReady);
        }
        finally
        {
            await host.DisposeAsync();
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
            var initialLoadCount = loader.LoadCount;
            var initialScanCount = scanner.ScanCount;
            scanner.FailNext();

            await WaitUntilAsync(
                () => factory.CreateCount > 0 && loader.LoadCount > initialLoadCount && host.IsReady,
                TimeSpan.FromSeconds(10));

            Assert.True(host.IsReady);
            Assert.True(scanner.ScanCount > initialScanCount);
            Assert.True(loader.LoadCount > initialLoadCount);
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
            var initialLoadCount = loader.LoadCount;
            loader.FailNext();
            factory.Current.TriggerPath(fixture.ProjectPath);

            await WaitUntilAsync(
                () => factory.CreateCount > 0 && loader.LoadCount > initialLoadCount && host.IsReady,
                TimeSpan.FromSeconds(10));

            Assert.True(host.IsReady);
            Assert.True(loader.LoadCount > initialLoadCount);
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
            Assert.Equal(identity.Digest, response.RequestDigest);
            Assert.Equal(
                IncrementalRefreshControlChannel.OutputPathIdentity(fixture.OutputPath),
                response.OutputPathIdentity);
            Assert.Equal(0, response.ExtractedProjectCount);
            Assert.NotNull(response.NodeCount);
            Assert.True(response.NodeCount!.Value > 0);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Different_output_path_falls_back_instead_of_using_the_watcher()
    {
        var fixture = await CreateFixtureAsync();
        var alternateOutputPath = Path.Combine(fixture.Root, "graphify-out", "alternate.json");
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1)),
                watcherFactory: new FakeWatcherFactory());
            await host.StartAsync();
            var canonicalBefore = await File.ReadAllTextAsync(fixture.OutputPath);

            var exitCode = await global::Graphify.CSharp.Cli.Program.Main(
            [
                "--input", fixture.ProjectPath,
                "--root", fixture.Root,
                "--configuration", fixture.Request.Configuration,
                "--target-framework", fixture.Request.TargetFramework!,
                "--output", alternateOutputPath,
            ]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(alternateOutputPath));
            Assert.Equal(canonicalBefore, await File.ReadAllTextAsync(fixture.OutputPath));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_different_configuration_cannot_publish_to_an_active_watchers_destination()
    {
        var fixture = await CreateFixtureAsync();
        var configurationSourcePath = Path.Combine(fixture.Root, "ConfigurationSpecific.cs");
        await File.WriteAllTextAsync(
            configurationSourcePath,
            """
            #if DEBUG
            namespace OutputOwnershipFixture;
            public sealed class DebugOnly { }
            #else
            namespace OutputOwnershipFixture;
            public sealed class ReleaseOnly { }
            #endif
            """);

        var debugRequest = new ProjectLoadRequest(
            fixture.ProjectPath,
            fixture.Root,
            configuration: "Debug",
            targetFramework: fixture.Request.TargetFramework);
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                watcherFactory: new FakeWatcherFactory());
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var outputBefore = await File.ReadAllTextAsync(fixture.OutputPath);
            var cachePath = IncrementalCachePath.ForOutput(fixture.OutputPath);
            var cacheBefore = await File.ReadAllTextAsync(cachePath);
            Assert.Contains("OutputOwnershipFixture.ReleaseOnly", outputBefore, StringComparison.Ordinal);
            Assert.True(OutputDestinationLease.IsHeld(host.OutputLeasePath));

            var foregroundExitCode = await global::Graphify.CSharp.Cli.Program.Main(
            [
                "--input", debugRequest.InputPath,
                "--root", debugRequest.RepositoryRoot,
                "--configuration", debugRequest.Configuration,
                "--target-framework", debugRequest.TargetFramework!,
                "--output", fixture.OutputPath,
            ]);

            Assert.Equal(1, foregroundExitCode);
            Assert.Equal(outputBefore, await File.ReadAllTextAsync(fixture.OutputPath));
            Assert.Equal(cacheBefore, await File.ReadAllTextAsync(cachePath));
            Assert.DoesNotContain("OutputOwnershipFixture.DebugOnly", await File.ReadAllTextAsync(fixture.OutputPath), StringComparison.Ordinal);

            await using var conflictingHost = new IncrementalWatcherHost(
                debugRequest,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                watcherFactory: new FakeWatcherFactory());
            await Assert.ThrowsAsync<InvalidOperationException>(() => conflictingHost.StartAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task File_based_watcher_lifecycle_is_bounded_and_never_watches_conversion_artifacts()
    {
        var repositoryRoot = RepositoryRoot();
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-file-based-watcher-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "csharp.json");
        var request = new ProjectLoadRequest(
            Path.Combine(repositoryRoot, "tests", "Fixtures", "FileBasedFixture", "App.cs"),
            repositoryRoot,
            configuration: "Release",
            targetFramework: "net10.0");
        var factory = new FakeWatcherFactory();
        var loader = new CountingLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var host = new IncrementalWatcherHost(
                request,
                outputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(File.Exists(outputPath));
            Assert.Contains("FileBasedFixture", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
            Assert.DoesNotContain(
                factory.CreatedRoots,
                root => root.CanonicalPath.Contains("graphify-csharp-file-", StringComparison.Ordinal));

            var refresh = await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var rebuild = await host.RefreshAsync(rebuild: true).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.NotEmpty(refresh.Graph.Nodes);
            Assert.NotEmpty(rebuild.Graph.Nodes);
            Assert.True(loader.LoadCount >= 2);
        }
        finally
        {
            DeleteTemporaryDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task Evaluated_obj_source_is_incremental_but_membership_changes_use_msbuild_reload()
    {
        var fixture = await CreateMembershipFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            var initialLoadCount = loader.LoadCount;
            var initial = await File.ReadAllTextAsync(fixture.OutputPath);
            Assert.Contains("MembershipFixture.Included", initial, StringComparison.Ordinal);
            Assert.DoesNotContain("MembershipFixture.Excluded", initial, StringComparison.Ordinal);

            var beforeNoise = host.Session.EventGeneration;
            factory.Current.TriggerPath(fixture.NoisePath);
            Assert.Equal(beforeNoise, host.Session.EventGeneration);

            await File.AppendAllTextAsync(fixture.IncludedPath, "\npublic sealed class IncludedChange { }\n");
            factory.Current.TriggerPath(fixture.IncludedPath);
            var warmResult = await host.RefreshAsync();
            Assert.Contains("MembershipFixture.IncludedChange", warmResult.Graph.Nodes.Select(node => node.Label));
            Assert.Equal(initialLoadCount, loader.LoadCount);

            await File.WriteAllTextAsync(
                fixture.ProjectPath,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="obj/Included.cs" />
                    <Compile Include="obj/NewlyIncluded.cs" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(fixture.NewlyIncludedPath, "namespace MembershipFixture; public sealed class NewlyIncluded { }");
            factory.Current.TriggerPath(fixture.ProjectPath);
            var reloadedResult = await host.RefreshAsync();
            Assert.Contains("MembershipFixture.NewlyIncluded", reloadedResult.Graph.Nodes.Select(node => node.Label));
            Assert.True(loader.LoadCount >= 2);

            await File.WriteAllTextAsync(
                fixture.ProjectPath,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="obj/NewlyIncluded.cs" />
                  </ItemGroup>
                </Project>
                """);
            factory.Current.TriggerPath(fixture.ProjectPath);
            var excludedResult = await host.RefreshAsync();
            Assert.DoesNotContain("MembershipFixture.Included", excludedResult.Graph.Nodes.Select(node => node.Label));
            Assert.DoesNotContain("MembershipFixture.IncludedChange", excludedResult.Graph.Nodes.Select(node => node.Label));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Theory]
    [InlineData("obj")]
    [InlineData("bin")]
    public async Task Directory_lifecycle_events_for_explicit_excluded_directory_inputs_are_not_dropped(
        string directoryName)
    {
        var fixture = await CreateExcludedDirectoryLifecycleFixtureAsync(directoryName);
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var initialLoadCount = loader.LoadCount;
            Assert.Contains(
                "DirectoryLifecycleFixture.ExplicitBeforeMove",
                await File.ReadAllTextAsync(fixture.OutputPath),
                StringComparison.Ordinal);

            Directory.Move(fixture.InputDirectory, fixture.MovedDirectory);
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Deleted,
                fixture.InputDirectory,
                IsDirectory: true));

            var normalRefresh = await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.DoesNotContain(
                "DirectoryLifecycleFixture.ExplicitBeforeMove",
                normalRefresh.Graph.Nodes.Select(node => node.Label));
            Assert.True(loader.LoadCount > initialLoadCount);

            var coldRefresh = await host.RefreshAsync(rebuild: true).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.DoesNotContain(
                "DirectoryLifecycleFixture.ExplicitBeforeMove",
                coldRefresh.Graph.Nodes.Select(node => node.Label));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.MovedDirectory);
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task An_extensionless_file_named_bin_is_discovered_before_backup_polling()
    {
        var fixture = await CreateReservedFileFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var initialLoadCount = loader.LoadCount;
            await File.WriteAllTextAsync(
                fixture.SourcePath,
                "namespace ReservedFileFixture; public sealed class AddedWithReservedFileName { }\n");
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Created,
                fixture.SourcePath,
                IsDirectory: false));

            var result = await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Contains(
                "ReservedFileFixture.AddedWithReservedFileName",
                result.Graph.Nodes.Select(node => node.Label));
            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_created_excluded_directory_containing_a_known_dependency_is_reconciled_before_backup_polling()
    {
        var fixture = await CreateDependencyDirectoryFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(host.Session.InputSnapshot.IsKnownDependency(fixture.DependencyPath));
            var initialLoadCount = loader.LoadCount;

            Directory.CreateDirectory(Path.GetDirectoryName(fixture.StagedPath)!);
            await File.WriteAllTextAsync(fixture.StagedPath, "new generator input");
            Directory.Move(fixture.StagedDirectory, fixture.DependencyDirectory);
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Created,
                fixture.DependencyDirectory,
                IsDirectory: true));

            await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.StagedDirectory);
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task An_explicit_additional_project_assets_name_is_not_treated_as_restore_metadata()
    {
        var fixture = await CreateDependencyDirectoryFixtureAsync("obj", "project.assets.json");
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(host.Session.InputSnapshot.IsKnownDependency(fixture.DependencyPath));
            var initialLoadCount = loader.LoadCount;

            Directory.CreateDirectory(Path.GetDirectoryName(fixture.StagedPath)!);
            await File.WriteAllTextAsync(fixture.StagedPath, "explicit additional input");
            Directory.Move(fixture.StagedDirectory, fixture.DependencyDirectory);
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Created,
                fixture.DependencyDirectory,
                IsDirectory: true));

            await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.StagedDirectory);
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_project_explicit_dependency_wins_over_infrastructure_in_both_project_orders(
        bool reverseProjects)
    {
        var fixture = await CreateCrossProjectProvenanceFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader(), reverseProjects);
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(90));

            Assert.True(host.Session.InputSnapshot.InputDiscoveryComplete);
            Assert.True(host.Session.InputSnapshot.IsKnownDependency(fixture.DependencyPath));
            Assert.DoesNotContain(
                host.Session.InputSnapshot.KnownSourcePaths,
                path => string.Equals(
                    path,
                    fixture.DependencyPath,
                    IncrementalPaths.PathComparison));

            var initialLoadCount = loader.LoadCount;
            Directory.CreateDirectory(fixture.StagedDirectory);
            await File.WriteAllTextAsync(fixture.StagedPath, "explicit additional input");
            Directory.Move(fixture.StagedDirectory, fixture.DependencyDirectory);

            // Deliver only the directory Created event, as the OS watcher can
            // do when a populated directory is moved into the watched tree.
            // FakeWatcher honors the host's actual capture predicate.
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Created,
                fixture.DependencyDirectory,
                IsDirectory: true));

            await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(90));

            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.StagedDirectory);
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Broad_generated_input_documents_do_not_fail_startup_refresh_or_rebuild()
    {
        var fixture = await CreateBroadGeneratedFixtureAsync();
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                watcherFactory: new FakeWatcherFactory());

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var normal = await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var rebuild = await host.RefreshAsync(rebuild: true).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.NotNull(normal.Graph);
            Assert.NotNull(rebuild.Graph);
            Assert.True(normal.Graph.Nodes.Length > 0);
            Assert.True(rebuild.Graph.Nodes.Length > 0);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Structural_source_changes_follow_default_globs_compile_remove_and_disabled_defaults()
    {
        var fixture = await CreateGlobMembershipFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            Assert.Contains("GlobMembershipFixture.Initial", (await File.ReadAllTextAsync(fixture.OutputPath)), StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                fixture.AddedPath,
                "namespace GlobMembershipFixture; public sealed class DefaultAdded { }");
            factory.Current.TriggerPath(fixture.AddedPath);
            var defaultGlobResult = await host.RefreshAsync();
            Assert.Contains("GlobMembershipFixture.DefaultAdded", defaultGlobResult.Graph.Nodes.Select(node => node.Label));

            await File.WriteAllTextAsync(
                fixture.ProjectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Remove="DefaultAdded.cs" />
                  </ItemGroup>
                </Project>
                """);
            factory.Current.TriggerPath(fixture.ProjectPath);
            var removedResult = await host.RefreshAsync();
            Assert.DoesNotContain("GlobMembershipFixture.DefaultAdded", removedResult.Graph.Nodes.Select(node => node.Label));

            await File.WriteAllTextAsync(
                fixture.ProjectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Initial.cs" />
                  </ItemGroup>
                </Project>
                """);
            factory.Current.TriggerPath(fixture.ProjectPath);
            await host.RefreshAsync();

            await File.WriteAllTextAsync(
                fixture.DisabledAddedPath,
                "namespace GlobMembershipFixture; public sealed class DisabledDefaultAdded { }");
            factory.Current.TriggerPath(fixture.DisabledAddedPath);
            var disabledDefaultResult = await host.RefreshAsync();

            Assert.Contains("GlobMembershipFixture.Initial", disabledDefaultResult.Graph.Nodes.Select(node => node.Label));
            Assert.DoesNotContain(
                "GlobMembershipFixture.DisabledDefaultAdded",
                disabledDefaultResult.Graph.Nodes.Select(node => node.Label));
            Assert.True(loader.LoadCount >= 4);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Directory_delete_and_move_out_of_scope_reconcile_all_affected_sources()
    {
        var fixture = await CreateDirectoryMembershipFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            var initial = await File.ReadAllTextAsync(fixture.OutputPath);
            Assert.Contains("DirectoryMembershipFixture.DeleteMe", initial, StringComparison.Ordinal);
            Assert.Contains("DirectoryMembershipFixture.MoveMe", initial, StringComparison.Ordinal);

            Directory.Delete(fixture.DeleteDirectory, recursive: true);
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Deleted,
                fixture.DeleteDirectory));
            var afterDelete = await host.RefreshAsync();

            Assert.DoesNotContain(
                "DirectoryMembershipFixture.DeleteMe",
                afterDelete.Graph.Nodes.Select(node => node.Label));
            Assert.Contains(
                "DirectoryMembershipFixture.MoveMe",
                afterDelete.Graph.Nodes.Select(node => node.Label));

            Directory.CreateDirectory(Path.Combine(fixture.Root, "obj"));
            var excludedDirectory = Path.Combine(fixture.Root, "obj", "moved");
            Directory.Move(fixture.MoveDirectory, excludedDirectory);
            factory.Current.TriggerChange(new FileChangeEvent(
                FileChangeKind.Renamed,
                excludedDirectory,
                fixture.MoveDirectory));
            var afterMove = await host.RefreshAsync();

            Assert.DoesNotContain(
                "DirectoryMembershipFixture.MoveMe",
                afterMove.Graph.Nodes.Select(node => node.Label));
            Assert.True(loader.LoadCount >= 3);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Linked_source_outside_repository_gets_new_watch_coverage_before_refreshing()
    {
        var fixture = await CreateExternalFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            var initialLoadCount = loader.LoadCount;

            Assert.Contains(
                factory.CreatedRoots,
                root => string.Equals(
                    root.CanonicalPath,
                    Path.GetDirectoryName(fixture.LinkedPath),
                    IncrementalPaths.PathComparison));
            Assert.Contains("ExternalFixture.Linked", (await File.ReadAllTextAsync(fixture.OutputPath)), StringComparison.Ordinal);
            Assert.True(loader.LoadCount >= 2);

            await File.AppendAllTextAsync(fixture.LinkedPath, "\npublic sealed class LinkedChange { }\n");
            var externalWatcher = factory.Watchers.Last(watcher =>
                string.Equals(
                    watcher.Root,
                    Path.GetDirectoryName(fixture.LinkedPath),
                    IncrementalPaths.PathComparison));
            externalWatcher.TriggerPath(fixture.LinkedPath);
            var result = await host.RefreshAsync();

            Assert.Contains("ExternalFixture.LinkedChange", result.Graph.Nodes.Select(node => node.Label));
            Assert.Equal(initialLoadCount, loader.LoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(fixture.ExternalRoot);
        }
    }

    [Fact]
    public async Task Evaluated_imports_are_watched_and_invalidate_the_warm_workspace()
    {
        var fixture = await CreateImportedConfigurationFixtureAsync();
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            var watcherFactory = new FakeWatcherFactory();
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: watcherFactory);

            await host.StartAsync();
            var initialLoadCount = loader.LoadCount;
            Assert.Contains(
                host.Session.InputSnapshot.KnownDependencyPaths,
                path => string.Equals(path, fixture.ImportPath, IncrementalPaths.PathComparison));
            Assert.DoesNotContain(
                "ImportedConfigurationFixture.EnabledByImport",
                (await File.ReadAllTextAsync(fixture.OutputPath)),
                StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                fixture.ImportPath,
                "<Project><PropertyGroup><DefineConstants>$(DefineConstants);ENABLED_BY_IMPORT</DefineConstants></PropertyGroup></Project>");
            var importWatcher = watcherFactory.Watchers.Last(watcher =>
                string.Equals(watcher.Root, fixture.Root, IncrementalPaths.PathComparison));
            importWatcher.TriggerPath(fixture.ImportPath);
            var result = await host.RefreshAsync();

            Assert.Contains(
                "ImportedConfigurationFixture.EnabledByImport",
                result.Graph.Nodes.Select(node => node.Label));
            Assert.Equal(initialLoadCount + 1, loader.LoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_dependency_change_during_the_post_load_scan_is_reconciled_before_baseline_acceptance()
    {
        var fixture = await CreateImportedConfigurationFixtureAsync();
        var scanner = new MutatingInventoryScanner(
            () => File.WriteAllTextAsync(
                fixture.ImportPath,
                "<Project><PropertyGroup><DefineConstants>$(DefineConstants);ENABLED_BY_IMPORT</DefineConstants></PropertyGroup></Project>"));
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                inventoryScanner: scanner,
                watcherFactory: new FakeWatcherFactory());

            // The scanner mutates the imported dependency immediately before
            // returning the first post-load inventory. No watcher event is
            // injected; the before/after inventory comparison is the safety
            // mechanism being exercised.
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Contains(
                "ImportedConfigurationFixture.EnabledByImport",
                (await File.ReadAllTextAsync(fixture.OutputPath)),
                StringComparison.Ordinal);
            Assert.True(loader.LoadCount >= 2);
            Assert.True(scanner.ScanCount >= 3);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_newly_discovered_external_dependency_is_not_accepted_as_a_clean_baseline()
    {
        var fixture = await CreateExternalImportedConfigurationFixtureAsync();
        var scanner = new MutatingInventoryScanner(
            () => File.WriteAllTextAsync(
                fixture.ImportPath,
                "<Project><PropertyGroup><DefineConstants>$(DefineConstants);ENABLED_BY_IMPORT</DefineConstants></PropertyGroup></Project>"));
        try
        {
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                inventoryScanner: scanner,
                watcherFactory: new FakeWatcherFactory());

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Contains(
                "ExternalImportedConfigurationFixture.EnabledByImport",
                (await File.ReadAllTextAsync(fixture.OutputPath)),
                StringComparison.Ordinal);
            Assert.True(loader.LoadCount >= 2);
            Assert.True(scanner.ScanCount >= 3);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(fixture.ExternalRoot);
        }
    }

    [Fact]
    public async Task A_foreground_refresh_waits_for_trust_recovery_and_returns_the_recovered_graph()
    {
        var fixture = await CreateFixtureAsync();
        IncrementalWatcherHost? host = null;
        var loader = new ForegroundTrustLoader(
            new RoslynWorkspaceLoader(),
            () =>
            {
                File.AppendAllText(
                    fixture.SourcePath,
                    "\npublic sealed class ForegroundRecovered { }\n");
                host!.Session.ReportWatcherFailure("synthetic loss during foreground refresh");
            });
        try
        {
            host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                watcherFactory: new FakeWatcherFactory());
            await using (host)
            {
                await host.StartAsync();
                loader.EnableForegroundLoss();
                var refresh = host.RefreshAsync(rebuild: true);

                await loader.RecoveryLoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.False(refresh.IsCompleted);
                Assert.False(host.IsReady);

                loader.ReleaseRecovery();
                var result = await refresh.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Contains(
                    "ReferenceFixture.Production.ForegroundRecovered",
                    result.Graph.Nodes.Select(node => node.Label));
                Assert.Contains(
                    "ReferenceFixture.Production.ForegroundRecovered",
                    await File.ReadAllTextAsync(fixture.OutputPath),
                    StringComparison.Ordinal);
                Assert.True(host.IsReady);
            }
        }
        finally
        {
            loader.ReleaseRecovery();
            if (host is not null)
            {
                await host.DisposeAsync();
            }

            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Automatic_recovery_updates_memory_without_publishing_until_refresh()
    {
        var fixture = await CreateFixtureAsync();
        var factory = new FakeWatcherFactory();
        var loader = new PostCompilationGateLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var before = await File.ReadAllBytesAsync(fixture.OutputPath);

            // A project-file event starts a cold background load. Hold that
            // load after Roslyn has captured its inputs, while the host is
            // still using the conservative transition snapshot.
            loader.ArmNextLoad();
            await File.AppendAllTextAsync(fixture.ProjectPath, "\n");
            factory.Current.TriggerPath(fixture.ProjectPath);
            await loader.GatedLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(60));

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class ChangedDuringRecovery { }\n");
            factory.Current.TriggerPath(fixture.SourcePath);
            await WaitUntilAsync(() => !host.IsReady, TimeSpan.FromSeconds(10));

            loader.ReleaseGatedLoad();
            await WaitUntilAsync(
                () => host.IsReady && loader.LoadCount >= 3,
                TimeSpan.FromSeconds(60));

            // Recovery may update the in-memory graph, but it must not write
            // the public document. Only the explicit refresh below may do so.
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.OutputPath));

            var refreshed = await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Contains(
                "ReferenceFixture.Production.ChangedDuringRecovery",
                refreshed.Graph.Nodes.Select(node => node.Label));
            Assert.True(refreshed.OutputRepublished);
            Assert.Contains(
                "ReferenceFixture.Production.ChangedDuringRecovery",
                await File.ReadAllTextAsync(fixture.OutputPath),
                StringComparison.Ordinal);
        }
        finally
        {
            loader.ReleaseGatedLoad();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Automatic_recovery_inventory_catchup_is_nonpublishing_until_refresh()
    {
        var fixture = await CreateFixtureAsync();
        var factory = new FakeWatcherFactory();
        var scanner = new PostScanMutatingInventoryScanner();
        var loader = new CountingLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                inventoryScanner: scanner,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var initialLoadCount = loader.LoadCount;
            var before = await File.ReadAllBytesAsync(fixture.OutputPath);
            scanner.ArmNextScan(
                () => File.AppendAllTextAsync(
                    fixture.SourcePath,
                    "\npublic sealed class ChangedDuringInventoryCatchup { }\n"));

            factory.Current.TriggerFailure(new IOException("synthetic recovery request"));
            await WaitUntilAsync(
                () => scanner.MutationApplied
                    && host.IsReady
                    && loader.LoadCount >= initialLoadCount + 2,
                TimeSpan.FromSeconds(60));

            // The recovery scan reports the edit after the cold load. That
            // catch-up must restore trusted in-memory state without publishing
            // the public document before a caller explicitly refreshes it.
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.OutputPath));

            var refreshed = await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Contains(
                "ReferenceFixture.Production.ChangedDuringInventoryCatchup",
                refreshed.Graph.Nodes.Select(node => node.Label));
            Assert.True(refreshed.OutputRepublished);
            Assert.Contains(
                "ReferenceFixture.Production.ChangedDuringInventoryCatchup",
                await File.ReadAllTextAsync(fixture.OutputPath),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_new_arbitrary_extension_compile_glob_file_is_seen_by_a_live_watcher()
    {
        var fixture = await CreateArbitraryGlobFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            var initialLoadCount = loader.LoadCount;
            await File.WriteAllTextAsync(
                fixture.AddedSourcePath,
                "namespace ArbitraryGlobFixture; public sealed class AddedSource { }");
            factory.Watchers.Last(watcher =>
                    string.Equals(watcher.Root, fixture.Root, IncrementalPaths.PathComparison))
                .TriggerPath(fixture.AddedSourcePath);

            var result = await host.RefreshAsync();

            Assert.Contains(
                "ArbitraryGlobFixture.AddedSource",
                result.Graph.Nodes.Select(node => node.Label));
            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_new_arbitrary_extension_additional_glob_file_is_seen_by_the_backup_inventory()
    {
        var fixture = await CreateArbitraryGlobFixtureAsync();
        try
        {
            var scanner = new CountingInventoryScanner();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromMilliseconds(50),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                inventoryScanner: scanner,
                watcherFactory: new FakeWatcherFactory());

            await host.StartAsync();
            var initialLoadCount = loader.LoadCount;
            await File.WriteAllTextAsync(fixture.AddedAdditionalPath, "added additional input");

            await WaitUntilAsync(
                () => scanner.ScanCount >= 2 && host.Session.EventGeneration >= 1,
                TimeSpan.FromSeconds(10));
            await WaitUntilAsync(
                () => loader.LoadCount > initialLoadCount,
                TimeSpan.FromSeconds(10));
            var result = await host.RefreshAsync();

            Assert.NotEmpty(result.Graph.Nodes);
            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Backup_inventory_reaches_an_explicit_glob_beneath_an_excluded_parent()
    {
        await AssertBackupInventoryDiscoversNewGlobSourceAsync(
            "graphify-csharp-explicit-obj-glob-tests",
            "ExplicitObjGlobFixture",
            "obj/generated",
            "    <Compile Include=\"obj/generated/**/*.csharp\" />");
    }

    [Fact]
    public async Task Backup_inventory_honors_a_broad_custom_glob_inside_an_excluded_directory()
    {
        await AssertBackupInventoryDiscoversNewGlobSourceAsync(
            "graphify-csharp-broad-custom-glob-tests",
            "BroadCustomGlobFixture",
            "obj/generated",
            "    <Compile Include=\"**/*.csharp\" />");
    }

    [Fact]
    public async Task Backup_inventory_preserves_wildcard_provenance_through_an_item_vector()
    {
        await AssertBackupInventoryDiscoversNewGlobSourceAsync(
            "graphify-csharp-item-vector-glob-tests",
            "ItemVectorGlobFixture",
            "src",
            """
                <CustomSources Include="src/**/*.csharp" />
                <Compile Include="@(CustomSources)" />
            """);
    }

    [Fact]
    public async Task Backup_inventory_does_not_prune_a_parent_for_a_narrow_glob_exclusion()
    {
        await AssertBackupInventoryDiscoversNewGlobSourceAsync(
            "graphify-csharp-partial-exclusion-glob-tests",
            "PartialExclusionGlobFixture",
            "obj/generated",
            "    <Compile Include=\"obj/generated/**/*.csharp\" Exclude=\"obj/generated/ignored/**/*.csharp\" />",
            excludedSourceDirectory: "obj/generated/ignored");
    }

    [Theory]
    [InlineData("obj/*", "obj/generated")]
    [InlineData("obj/*", "obj/generated.v1/nested")]
    [InlineData("obj/Release/*", "obj/Release/generated")]
    [InlineData("obj/Release/*", "obj/Release/net10.0/nested")]
    public async Task Backup_inventory_keeps_traversing_for_nonrecursive_exclusions(
        string exclusionPattern,
        string sourceDirectory)
    {
        await AssertBackupInventoryDiscoversNewGlobSourceAsync(
            $"graphify-csharp-nonrecursive-exclusion-glob-tests-{sourceDirectory.Replace('/', '-')}",
            "NonrecursiveExclusionGlobFixture",
            sourceDirectory,
            $"    <Compile Include=\"obj/**/*.csharp\" Exclude=\"{exclusionPattern}\" />");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_inventory_keeps_both_identical_include_patterns_when_exclusions_differ(
        bool permissiveRuleFirst)
    {
        var itemMarkup = permissiveRuleFirst
            ? """
                  <Compile Include="**/*.csharp" />
                  <Compile Include="**/*.csharp" Exclude="obj/**" />
              """
            : """
                  <Compile Include="**/*.csharp" Exclude="obj/**" />
                  <Compile Include="**/*.csharp" />
              """;
        await AssertBackupInventoryDiscoversNewGlobSourceAsync(
            $"graphify-csharp-duplicate-include-glob-tests-{permissiveRuleFirst}",
            "DuplicateIncludeGlobFixture",
            "obj/generated",
            itemMarkup);
    }

    [Fact]
    public async Task Incomplete_auxiliary_discovery_forces_a_cold_refresh_and_emits_a_diagnostic()
    {
        var fixture = await CreateImportedConfigurationFixtureAsync();
        try
        {
            var loader = new FlakyDiscoveryLoader(new RoslynWorkspaceLoader(), fixture.ImportPath);
            var factory = new FakeWatcherFactory();
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(host.Session.InputSnapshot.InputDiscoveryComplete);
            Assert.NotEmpty(host.Session.InputSnapshot.InputDiscoveryDiagnostics);
            Assert.Contains(
                "Watcher input discovery was incomplete",
                await File.ReadAllTextAsync(fixture.OutputPath),
                StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                fixture.ImportPath,
                "<Project><PropertyGroup><DefineConstants>$(DefineConstants);ENABLED_BY_IMPORT</DefineConstants></PropertyGroup></Project>");
            var loadCountBeforeRefresh = loader.LoadCount;
            var result = await host.RefreshAsync();

            Assert.Contains(
                "ImportedConfigurationFixture.EnabledByImport",
                result.Graph.Nodes.Select(node => node.Label));
            Assert.True(loader.LoadCount > loadCountBeforeRefresh);
            Assert.True(
                host.Session.InputSnapshot.InputDiscoveryComplete,
                string.Join(" | ", host.Session.InputSnapshot.InputDiscoveryDiagnostics));

            await File.WriteAllTextAsync(
                Path.Combine(fixture.Root, "Conditional.cs"),
                "namespace ImportedConfigurationFixture; public sealed class EnabledByImport { } public sealed class WarmAfterRecovery { }\n");
            factory.Watchers.Last(watcher =>
                    string.Equals(watcher.Root, fixture.Root, IncrementalPaths.PathComparison))
                .TriggerPath(Path.Combine(fixture.Root, "Conditional.cs"));
            var warmLoadCount = loader.LoadCount;
            var warmResult = await host.RefreshAsync();

            Assert.Contains(
                "ImportedConfigurationFixture.WarmAfterRecovery",
                warmResult.Graph.Nodes.Select(node => node.Label));
            Assert.Equal(warmLoadCount, loader.LoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_missing_external_watch_root_is_recovered_after_the_root_is_recreated()
    {
        var fixture = await CreateExternalFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromMilliseconds(50),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            var initialLoadCount = loader.LoadCount;
            DeleteTemporaryDirectory(fixture.ExternalRoot);

            await WaitUntilAsync(() => !host.IsReady, TimeSpan.FromSeconds(60));
            Directory.CreateDirectory(fixture.ExternalRoot);
            await File.WriteAllTextAsync(
                fixture.LinkedPath,
                "namespace ExternalFixture; public sealed class RecreatedLinked { }");

            await WaitUntilAsync(
                () => host.IsReady && loader.LoadCount > initialLoadCount,
                TimeSpan.FromSeconds(60));
            await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var output = await File.ReadAllTextAsync(fixture.OutputPath);
            Assert.Contains("ExternalFixture.RecreatedLinked", output, StringComparison.Ordinal);
            Assert.True(factory.CreateCount >= 4);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(fixture.ExternalRoot);
        }
    }

    [Fact]
    public async Task Recovery_reloads_after_an_external_link_is_deleted_and_removed_from_the_project()
    {
        var fixture = await CreateExternalFixtureAsync();
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
            var initialCreatedRootCount = factory.CreatedRoots.Count;
            var externalDirectory = Path.GetDirectoryName(fixture.LinkedPath)!;
            await File.WriteAllTextAsync(
                fixture.Request.InputPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            DeleteTemporaryDirectory(fixture.ExternalRoot);
            var externalWatcher = factory.Watchers.Last(watcher =>
                string.Equals(watcher.Root, externalDirectory, IncrementalPaths.PathComparison));
            externalWatcher.TriggerFailure(new IOException("synthetic deleted external root"));

            await WaitUntilAsync(
                () => host.IsReady && loader.LoadCount >= 2,
                TimeSpan.FromSeconds(20));

            await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var output = await File.ReadAllTextAsync(fixture.OutputPath);
            Assert.DoesNotContain("ExternalFixture.Linked", output, StringComparison.Ordinal);
            Assert.DoesNotContain(
                factory.CreatedRoots.Skip(initialCreatedRootCount),
                root => string.Equals(root.CanonicalPath, externalDirectory, IncrementalPaths.PathComparison));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(fixture.ExternalRoot);
        }
    }

    [Fact]
    public async Task A_second_delivery_loss_during_recovery_keeps_recovery_pending()
    {
        var fixture = await CreateFixtureAsync();
        var loader = new GatedRecoveryLoader(new RoslynWorkspaceLoader());
        try
        {
            var factory = new FakeWatcherFactory();
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromHours(1),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                watcherFactory: factory);
            await host.StartAsync();
            var baselineLoadCount = loader.LoadCount;
            var baselineTrustVersion = host.Session.EventTrustVersion;
            var before = await File.ReadAllTextAsync(fixture.OutputPath);
            loader.ArmNextRecovery();
            factory.Current.TriggerFailure(new IOException("synthetic first delivery loss"));

            await loader.RecoveryLoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(baselineLoadCount + 1, loader.LoadCount);

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class ChangedDuringSecondLoss { }\n");
            host.Session.ReportWatcherFailure("synthetic second delivery loss");
            Assert.False(host.IsReady);
            Assert.Equal(baselineTrustVersion + 2, host.Session.EventTrustVersion);

            var refresh = host.RefreshAsync();
            Assert.False(refresh.IsCompleted);
            Assert.Equal(before, await File.ReadAllTextAsync(fixture.OutputPath));

            loader.ReleaseRecovery();
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.True(loader.LoadCount >= baselineLoadCount + 2);
            Assert.True(host.IsReady);
            Assert.True(host.Session.IsEventTrustValid(host.Session.EventTrustVersion));
            Assert.Contains(
                "ReferenceFixture.Production.ChangedDuringSecondLoss",
                result.Graph.Nodes.Select(node => node.Label));
        }
        finally
        {
            loader.ReleaseRecovery();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task An_external_additional_input_gets_exact_backup_and_live_watch_coverage()
    {
        var fixture = await CreateExternalAdditionalFileFixtureAsync();
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(backupScanInterval: TimeSpan.FromHours(1)),
                projectLoader: loader,
                watcherFactory: factory);

            await host.StartAsync();
            Assert.Contains(
                factory.CreatedRoots,
                root => string.Equals(
                    root.CanonicalPath,
                    Path.GetDirectoryName(fixture.AdditionalPath),
                    IncrementalPaths.PathComparison)
                    && !root.IncludeSubdirectories);
            Assert.Contains(
                host.Session.InputSnapshot.KnownDependencyPaths,
                path => string.Equals(path, fixture.AdditionalPath, IncrementalPaths.PathComparison));
            // The external input is discovered after the bootstrap inventory.
            // The post-load reconciliation deliberately performs one cold
            // pass so that it cannot become a stale clean baseline.
            var initialLoadCount = loader.LoadCount;
            Assert.True(initialLoadCount >= 3);

            await File.WriteAllTextAsync(fixture.AdditionalPath, "changed additional input");
            var externalWatcher = factory.Watchers.Last(watcher =>
                string.Equals(
                    watcher.Root,
                    Path.GetDirectoryName(fixture.AdditionalPath),
                    IncrementalPaths.PathComparison));
            externalWatcher.TriggerPath(fixture.AdditionalPath);
            await host.RefreshAsync();

            Assert.True(loader.LoadCount > initialLoadCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
            DeleteTemporaryDirectory(fixture.ExternalRoot);
        }
    }

    [Fact]
    public async Task A_backup_scan_from_an_old_snapshot_cannot_overwrite_the_new_inventory_baseline()
    {
        var fixture = await CreateFixtureAsync();
        var scanner = new StaleScanInventoryScanner(Path.Combine(fixture.Root, "stale.cs"));
        try
        {
            var factory = new FakeWatcherFactory();
            var loader = new CountingLoader(new RoslynWorkspaceLoader());
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromMilliseconds(10),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: loader,
                inventoryScanner: scanner,
                watcherFactory: factory);

            await host.StartAsync();
            var oldInputSnapshot = host.Session.InputSnapshot;
            await scanner.BackupScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            factory.Current.TriggerPath(fixture.ProjectPath);
            await WaitUntilAsync(
                () => !ReferenceEquals(oldInputSnapshot, host.Session.InputSnapshot)
                    && !host.Session.InputSnapshot.IsBootstrap,
                TimeSpan.FromSeconds(10));
            var generationAfterReload = host.Session.EventGeneration;

            scanner.ReleaseBackupScan();
            await Task.Delay(100);

            Assert.Equal(generationAfterReload, host.Session.EventGeneration);
            Assert.True(loader.LoadCount >= 2);
        }
        finally
        {
            scanner.ReleaseBackupScan();
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

    private static async Task<MembershipFixture> CreateMembershipFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-membership-tests", Guid.NewGuid().ToString("N"));
        var obj = Path.Combine(root, "obj");
        Directory.CreateDirectory(obj);
        var projectPath = Path.Combine(root, "MembershipFixture.csproj");
        var includedPath = Path.Combine(obj, "Included.cs");
        var noisePath = Path.Combine(obj, "Noise.cs");
        var newlyIncludedPath = Path.Combine(obj, "NewlyIncluded.cs");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="obj/Included.cs" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(includedPath, "namespace MembershipFixture; public sealed class Included { }");
        await File.WriteAllTextAsync(noisePath, "namespace MembershipFixture; public sealed class Noise { }");
        return new MembershipFixture(
            root,
            projectPath,
            includedPath,
            noisePath,
            newlyIncludedPath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<ExcludedDirectoryLifecycleFixture> CreateExcludedDirectoryLifecycleFixtureAsync(
        string directoryName)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-directory-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(root, directoryName);
        var movedDirectory = Path.Combine(
            Path.GetDirectoryName(root)!,
            $"{Path.GetFileName(root)}-moved");
        Directory.CreateDirectory(inputDirectory);
        var projectPath = Path.Combine(root, "DirectoryLifecycleFixture.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{directoryName}/Explicit.cs" Condition="Exists('{directoryName}/Explicit.cs')" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(inputDirectory, "Explicit.cs"),
            "namespace DirectoryLifecycleFixture; public sealed class ExplicitBeforeMove { }\n");
        return new ExcludedDirectoryLifecycleFixture(
            root,
            inputDirectory,
            movedDirectory,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<ReservedFileFixture> CreateReservedFileFixtureAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-reserved-file-tests",
            Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "obj", "bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var projectPath = Path.Combine(root, "ReservedFileFixture.csproj");
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
                <Compile Include="obj/b*" Exclude="obj/bin/**" />
              </ItemGroup>
            </Project>
            """);
        return new ReservedFileFixture(
            root,
            sourcePath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<DependencyDirectoryFixture> CreateDependencyDirectoryFixtureAsync(
        string directoryName = "bin",
        string fileName = "input.data")
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-dependency-directory-tests",
            Guid.NewGuid().ToString("N"));
        var dependencyDirectory = Path.Combine(root, "deps", directoryName);
        var stagedDirectory = Path.Combine(root, "staged");
        var dependencyPath = Path.Combine(dependencyDirectory, fileName);
        var stagedPath = Path.Combine(stagedDirectory, fileName);
        Directory.CreateDirectory(Path.Combine(root, "deps"));
        var projectPath = Path.Combine(root, "DependencyDirectoryFixture.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <DefaultItemExcludes>$(DefaultItemExcludes);deps/{directoryName}/**</DefaultItemExcludes>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <AdditionalFiles Include="deps/{directoryName}/{fileName}" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Source.cs"),
            "namespace DependencyDirectoryFixture; public sealed class Source { }\n");
        return new DependencyDirectoryFixture(
            root,
            dependencyDirectory,
            stagedDirectory,
            stagedPath,
            dependencyPath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<CrossProjectProvenanceFixture> CreateCrossProjectProvenanceFixtureAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-cross-project-provenance-tests",
            Guid.NewGuid().ToString("N"));
        var projectADirectory = Path.Combine(root, "A");
        var projectBDirectory = Path.Combine(root, "B");
        Directory.CreateDirectory(projectADirectory);
        Directory.CreateDirectory(projectBDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(root, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)intermediates/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
                <DefaultItemExcludes>$(DefaultItemExcludes);**/obj/**</DefaultItemExcludes>
              </PropertyGroup>
            </Project>
            """);
        var projectAPath = Path.Combine(projectADirectory, "A.csproj");
        await File.WriteAllTextAsync(
            projectAPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../B/B.csproj" />
                <AdditionalFiles Include="../B/obj/project.assets.json" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(projectBDirectory, "B.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                <GenerateMSBuildEditorConfigFile>false</GenerateMSBuildEditorConfigFile>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(projectADirectory, "A.cs"),
            "namespace CrossProjectProvenanceFixture; public sealed class A { }\n");
        await File.WriteAllTextAsync(
            Path.Combine(projectBDirectory, "B.cs"),
            "namespace CrossProjectProvenanceFixture; public sealed class B { }\n");

        await RestoreProjectAsync(projectAPath, root);

        var dependencyDirectory = Path.Combine(projectBDirectory, "obj");
        return new CrossProjectProvenanceFixture(
            root,
            dependencyDirectory,
            Path.Combine(root, "staged"),
            Path.Combine(root, "staged", "project.assets.json"),
            Path.Combine(dependencyDirectory, "project.assets.json"),
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(
                projectAPath,
                root,
                configuration: "Release",
                targetFramework: "net10.0"));
    }

    private static async Task RestoreProjectAsync(string projectPath, string workingDirectory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet restore for the cross-project watcher fixture.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var output = (await standardOutput) + Environment.NewLine + await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"dotnet restore failed for '{projectPath}' (exit code {process.ExitCode}).\n{output}");
    }

    private static async Task<BroadGeneratedFixture> CreateBroadGeneratedFixtureAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-broad-generated-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "BroadGeneratedFixture.csproj");
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
                <Compile Include="obj/**/*" Exclude="obj/bin/**" />
              </ItemGroup>
            </Project>
            """);
        return new BroadGeneratedFixture(
            root,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<GlobMembershipFixture> CreateGlobMembershipFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-glob-membership-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "GlobMembershipFixture.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Initial.cs"),
            "namespace GlobMembershipFixture; public sealed class Initial { }");
        return new GlobMembershipFixture(
            root,
            projectPath,
            Path.Combine(root, "DefaultAdded.cs"),
            Path.Combine(root, "DisabledDefaultAdded.cs"),
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<DirectoryMembershipFixture> CreateDirectoryMembershipFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-directory-membership-tests", Guid.NewGuid().ToString("N"));
        var deleteDirectory = Path.Combine(root, "delete-me");
        var moveDirectory = Path.Combine(root, "move-me");
        Directory.CreateDirectory(deleteDirectory);
        Directory.CreateDirectory(moveDirectory);
        var projectPath = Path.Combine(root, "DirectoryMembershipFixture.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(deleteDirectory, "DeleteMe.cs"),
            "namespace DirectoryMembershipFixture; public sealed class DeleteMe { }");
        await File.WriteAllTextAsync(
            Path.Combine(moveDirectory, "MoveMe.cs"),
            "namespace DirectoryMembershipFixture; public sealed class MoveMe { }");
        return new DirectoryMembershipFixture(
            root,
            deleteDirectory,
            moveDirectory,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<ExternalFixture> CreateExternalFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-linked-tests", Guid.NewGuid().ToString("N"));
        var externalRoot = Path.Combine(Path.GetTempPath(), "graphify-csharp-linked-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(externalRoot);
        var projectPath = Path.Combine(root, "ExternalFixture.csproj");
        var linkedPath = Path.Combine(externalRoot, "Linked.cs");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{linkedPath}" Link="Linked.cs" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(linkedPath, "namespace ExternalFixture; public sealed class Linked { }");
        return new ExternalFixture(
            root,
            externalRoot,
            linkedPath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<ImportedConfigurationFixture> CreateImportedConfigurationFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-import-tests", Guid.NewGuid().ToString("N"));
        var buildDirectory = Path.Combine(root, "build");
        Directory.CreateDirectory(buildDirectory);
        var projectPath = Path.Combine(root, "ImportedConfigurationFixture.csproj");
        var importPath = Path.Combine(buildDirectory, "Custom.props");
        await File.WriteAllTextAsync(
            projectPath,
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
        await File.WriteAllTextAsync(
            Path.Combine(root, "Conditional.cs"),
            """
            #if ENABLED_BY_IMPORT
            namespace ImportedConfigurationFixture;
            public sealed class EnabledByImport { }
            #endif
            """);
        return new ImportedConfigurationFixture(
            root,
            importPath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<ExternalImportedConfigurationFixture> CreateExternalImportedConfigurationFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-external-import-tests", Guid.NewGuid().ToString("N"));
        var externalRoot = Path.Combine(Path.GetTempPath(), "graphify-csharp-external-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(externalRoot);
        var projectPath = Path.Combine(root, "ExternalImportedConfigurationFixture.csproj");
        var importPath = Path.Combine(externalRoot, "Custom.props");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <Import Project="{importPath}" />
            </Project>
            """);
        await File.WriteAllTextAsync(importPath, "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(root, "Conditional.cs"),
            """
            #if ENABLED_BY_IMPORT
            namespace ExternalImportedConfigurationFixture;
            public sealed class EnabledByImport { }
            #endif
            """);
        return new ExternalImportedConfigurationFixture(
            root,
            externalRoot,
            importPath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<ArbitraryGlobFixture> CreateArbitraryGlobFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-arbitrary-glob-tests", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "src");
        var additionalDirectory = Path.Combine(root, "additional");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(additionalDirectory);
        var projectPath = Path.Combine(root, "ArbitraryGlobFixture.csproj");
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
                <Compile Include="src/**/*.csharp" />
                <AdditionalFiles Include="additional/**/*.data" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "Initial.csharp"),
            "namespace ArbitraryGlobFixture; public sealed class Initial { }");
        await File.WriteAllTextAsync(
            Path.Combine(additionalDirectory, "Initial.data"),
            "initial additional input");
        return new ArbitraryGlobFixture(
            root,
            projectPath,
            Path.Combine(sourceDirectory, "Added.csharp"),
            Path.Combine(additionalDirectory, "Added.data"),
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task AssertBackupInventoryDiscoversNewGlobSourceAsync(
        string fixtureName,
        string namespaceName,
        string sourceDirectory,
        string itemMarkup,
        string? excludedSourceDirectory = null)
    {
        var fixture = await CreateExplicitGlobFixtureAsync(
            fixtureName,
            namespaceName,
            sourceDirectory,
            itemMarkup,
            excludedSourceDirectory);
        try
        {
            var scanner = new CountingInventoryScanner();
            await using var host = new IncrementalWatcherHost(
                fixture.Request,
                fixture.OutputPath,
                new IncrementalWatcherOptions(
                    backupScanInterval: TimeSpan.FromMilliseconds(50),
                    recoveryRetryDelay: TimeSpan.FromMilliseconds(25)),
                projectLoader: new RoslynWorkspaceLoader(),
                inventoryScanner: scanner,
                watcherFactory: new FakeWatcherFactory());

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(
                host.Session.InputSnapshot.InputDiscoveryComplete,
                string.Join(" | ", host.Session.InputSnapshot.InputDiscoveryDiagnostics));
            Assert.Contains(
                $"{namespaceName}.Existing",
                await File.ReadAllTextAsync(fixture.OutputPath),
                StringComparison.Ordinal);
            if (excludedSourceDirectory is not null)
            {
                Assert.True(
                    File.Exists(Path.Combine(fixture.Root, excludedSourceDirectory, "Excluded.csharp")));
                Assert.DoesNotContain(
                    $"{namespaceName}.Excluded",
                    await File.ReadAllTextAsync(fixture.OutputPath),
                    StringComparison.Ordinal);
            }
            var initialScanCount = scanner.ScanCount;
            var initialEventGeneration = host.Session.EventGeneration;

            await File.WriteAllTextAsync(
                fixture.AddedPath,
                $"namespace {namespaceName}; public sealed class Added {{ }}\n");

            await WaitUntilAsync(
                () => scanner.ScanCount > initialScanCount
                    && host.Session.EventGeneration > initialEventGeneration,
                TimeSpan.FromSeconds(10));

            var result = await host.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Contains(
                $"{namespaceName}.Added",
                result.Graph.Nodes.Select(node => node.Label));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    private static async Task<ExplicitGlobFixture> CreateExplicitGlobFixtureAsync(
        string fixtureName,
        string namespaceName,
        string sourceDirectory,
        string itemMarkup,
        string? excludedSourceDirectory = null)
    {
        var root = Path.Combine(Path.GetTempPath(), fixtureName, Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, sourceDirectory);
        Directory.CreateDirectory(sourceRoot);
        var projectPath = Path.Combine(root, fixtureName + ".csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
            {itemMarkup}
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Existing.csharp"),
            $"namespace {namespaceName}; public sealed class Existing {{ }}\n");
        if (excludedSourceDirectory is not null)
        {
            var excludedRoot = Path.Combine(root, excludedSourceDirectory);
            Directory.CreateDirectory(excludedRoot);
            await File.WriteAllTextAsync(
                Path.Combine(excludedRoot, "Excluded.csharp"),
                $"namespace {namespaceName}; public sealed class Excluded {{ }}\n");
        }
        var addedPath = Path.Combine(sourceRoot, "Added.csharp");
        return new ExplicitGlobFixture(
            root,
            addedPath,
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<ExternalAdditionalFileFixture> CreateExternalAdditionalFileFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-additional-tests", Guid.NewGuid().ToString("N"));
        var externalRoot = Path.Combine(Path.GetTempPath(), "graphify-csharp-additional-external", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(externalRoot);
        var projectPath = Path.Combine(root, "ExternalAdditionalFixture.csproj");
        var additionalPath = Path.Combine(externalRoot, "generator.data");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <AdditionalFiles Include="{additionalPath}" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(additionalPath, "initial additional input");
        await File.WriteAllTextAsync(
            Path.Combine(root, "Source.cs"),
            "namespace ExternalAdditionalFixture; public sealed class Source { }");
        return new ExternalAdditionalFileFixture(
            root,
            externalRoot,
            additionalPath,
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

    private sealed record MembershipFixture(
        string Root,
        string ProjectPath,
        string IncludedPath,
        string NoisePath,
        string NewlyIncludedPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ExcludedDirectoryLifecycleFixture(
        string Root,
        string InputDirectory,
        string MovedDirectory,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ReservedFileFixture(
        string Root,
        string SourcePath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record DependencyDirectoryFixture(
        string Root,
        string DependencyDirectory,
        string StagedDirectory,
        string StagedPath,
        string DependencyPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record CrossProjectProvenanceFixture(
        string Root,
        string DependencyDirectory,
        string StagedDirectory,
        string StagedPath,
        string DependencyPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record BroadGeneratedFixture(
        string Root,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record GlobMembershipFixture(
        string Root,
        string ProjectPath,
        string AddedPath,
        string DisabledAddedPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ArbitraryGlobFixture(
        string Root,
        string ProjectPath,
        string AddedSourcePath,
        string AddedAdditionalPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ExplicitGlobFixture(
        string Root,
        string AddedPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record DirectoryMembershipFixture(
        string Root,
        string DeleteDirectory,
        string MoveDirectory,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ExternalFixture(
        string Root,
        string ExternalRoot,
        string LinkedPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ImportedConfigurationFixture(
        string Root,
        string ImportPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ExternalImportedConfigurationFixture(
        string Root,
        string ExternalRoot,
        string ImportPath,
        string OutputPath,
        ProjectLoadRequest Request);

    private sealed record ExternalAdditionalFileFixture(
        string Root,
        string ExternalRoot,
        string AdditionalPath,
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
            CancellationToken cancellationToken = default,
            WatcherInputSnapshot? inputSnapshot = null)
        {
            ScanCount++;
            return await _inner.ScanAsync(roots, repositoryRoot, includeContentHashes, cancellationToken, inputSnapshot);
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
            CancellationToken cancellationToken = default,
            WatcherInputSnapshot? inputSnapshot = null)
        {
            ScanCount++;
            if (Interlocked.Exchange(ref _failNext, 0) != 0)
            {
                throw new InvalidDataException("synthetic backup inventory failure");
            }

            return await _inner.ScanAsync(roots, repositoryRoot, includeContentHashes, cancellationToken, inputSnapshot);
        }
    }

    private sealed class StaleScanInventoryScanner : IFileInventoryScanner
    {
        private readonly string _stalePath;
        private readonly FileInventoryScanner _inner = new();
        private int _scanCount;
        private int _released;

        public StaleScanInventoryScanner(string stalePath)
        {
            _stalePath = stalePath;
        }

        public TaskCompletionSource<bool> BackupScanEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<bool> ReleaseSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseBackupScan()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                ReleaseSignal.TrySetResult(true);
            }
        }

        public async Task<FileInventorySnapshot> ScanAsync(
            IReadOnlyList<string> roots,
            string repositoryRoot,
            bool includeContentHashes = false,
            CancellationToken cancellationToken = default,
            WatcherInputSnapshot? inputSnapshot = null)
        {
            var scanCount = Interlocked.Increment(ref _scanCount);
            // The startup post-load reconciliation now performs an extra
            // authoritative scan for newly discovered inputs before the
            // backup loop starts.
            if (scanCount == 4)
            {
                BackupScanEntered.TrySetResult(true);
                await ReleaseSignal.Task.WaitAsync(cancellationToken);
                return new FileInventorySnapshot(
                [
                    new FileInventoryEntry(
                        _stalePath,
                        new SourceFingerprint("stale.cs", true, 1, 1)),
                ]);
            }

            return await _inner.ScanAsync(roots, repositoryRoot, includeContentHashes, cancellationToken, inputSnapshot);
        }
    }

    private sealed class MutatingInventoryScanner : IFileInventoryScanner
    {
        private readonly FileInventoryScanner _inner = new();
        private readonly Func<Task> _mutation;
        private int _scanCount;
        private int _mutationApplied;

        public MutatingInventoryScanner(Func<Task> mutation)
        {
            _mutation = mutation;
        }

        public int ScanCount => Volatile.Read(ref _scanCount);

        public async Task<FileInventorySnapshot> ScanAsync(
            IReadOnlyList<string> roots,
            string repositoryRoot,
            bool includeContentHashes = false,
            CancellationToken cancellationToken = default,
            WatcherInputSnapshot? inputSnapshot = null)
        {
            var scanCount = Interlocked.Increment(ref _scanCount);
            if (scanCount == 2 && Interlocked.Exchange(ref _mutationApplied, 1) == 0)
            {
                await _mutation().WaitAsync(cancellationToken);
            }

            return await _inner.ScanAsync(roots, repositoryRoot, includeContentHashes, cancellationToken, inputSnapshot);
        }
    }

    private sealed class PostScanMutatingInventoryScanner : IFileInventoryScanner
    {
        private readonly FileInventoryScanner _inner = new();
        private Func<Task>? _mutation;
        private int _scanCount;
        private int _mutationApplied;

        public int ScanCount => Volatile.Read(ref _scanCount);

        public bool MutationApplied => Volatile.Read(ref _mutationApplied) != 0;

        public void ArmNextScan(Func<Task> mutation)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            _mutation = mutation;
        }

        public async Task<FileInventorySnapshot> ScanAsync(
            IReadOnlyList<string> roots,
            string repositoryRoot,
            bool includeContentHashes = false,
            CancellationToken cancellationToken = default,
            WatcherInputSnapshot? inputSnapshot = null)
        {
            Interlocked.Increment(ref _scanCount);
            var snapshot = await _inner.ScanAsync(
                    roots,
                    repositoryRoot,
                    includeContentHashes,
                    cancellationToken,
                    inputSnapshot)
                .ConfigureAwait(false);
            var mutation = Interlocked.Exchange(ref _mutation, null);
            if (mutation is not null)
            {
                await mutation().WaitAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _mutationApplied, 1);
            }

            return snapshot;
        }
    }

    private sealed class FakeWatcherFactory : IFileChangeWatcherFactory
    {
        public int CreateCount { get; private set; }

        public FakeWatcher Current { get; private set; } = null!;

        public List<WatcherRoot> CreatedRoots { get; } = [];

        public List<FakeWatcher> Watchers { get; } = [];

        public IFileChangeWatcher Create(WatcherRoot root, Func<FileChangeEvent, bool> shouldCapture)
        {
            CreateCount++;
            Current = new FakeWatcher(root.CanonicalPath, shouldCapture);
            CreatedRoots.Add(root);
            Watchers.Add(Current);
            return Current;
        }
    }

    private sealed class BlockingWatcherFactory : IFileChangeWatcherFactory
    {
        public BlockingWatcher Current { get; private set; } = null!;

        public TaskCompletionSource<bool> StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseStart() => Current?.ReleaseStart();

        public IFileChangeWatcher Create(WatcherRoot root, Func<FileChangeEvent, bool> shouldCapture)
        {
            Current = new BlockingWatcher(root.CanonicalPath, StartEntered);
            return Current;
        }
    }

    private sealed class BlockingWatcher : IFileChangeWatcher
    {
        private readonly TaskCompletionSource<bool> _startEntered;
        private readonly TaskCompletionSource<bool> _releaseStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public BlockingWatcher(string root, TaskCompletionSource<bool> startEntered)
        {
            Root = root;
            _startEntered = startEntered;
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

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Start()
        {
            _startEntered.TrySetResult(true);
            _releaseStart.Task.GetAwaiter().GetResult();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
        }

        public void ReleaseStart() => _releaseStart.TrySetResult(true);

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }

    private sealed class FakeWatcher : IFileChangeWatcher
    {
        private readonly Func<FileChangeEvent, bool> _shouldCapture;
        private bool _disposed;

        public FakeWatcher(string root, Func<FileChangeEvent, bool> shouldCapture)
        {
            Root = root;
            _shouldCapture = shouldCapture;
        }

        public event Action<FileChangeEvent>? PathChanged;

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
            PathChanged?.Invoke(new FileChangeEvent(FileChangeKind.Changed, path));
        }

        public void TriggerChange(FileChangeEvent change)
        {
            if (_shouldCapture(change))
            {
                PathChanged?.Invoke(change);
            }
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
        private readonly bool _reverseProjects;

        public CountingLoader(IProjectLoader inner, bool reverseProjects = false)
        {
            _inner = inner;
            _reverseProjects = reverseProjects;
        }

        public int LoadCount { get; private set; }

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (_reverseProjects)
            {
                loaded.ReplaceProjects(loaded.Projects.Reverse());
            }

            return loaded;
        }
    }

    private sealed class PostCompilationGateLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TaskCompletionSource<bool> _releaseGatedLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;
        private int _gateNextLoad;

        public PostCompilationGateLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public int LoadCount => Volatile.Read(ref _loadCount);

        public TaskCompletionSource<bool> GatedLoadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ArmNextLoad() => Interlocked.Exchange(ref _gateNextLoad, 1);

        public async Task<LoadedSolution> LoadAsync(
            ProjectLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            var loadCount = Interlocked.Increment(ref _loadCount);
            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (Interlocked.Exchange(ref _gateNextLoad, 0) != 0)
            {
                GatedLoadCompleted.TrySetResult(true);
                await _releaseGatedLoad.Task.WaitAsync(cancellationToken);
            }

            return loaded;
        }

        public void ReleaseGatedLoad() => _releaseGatedLoad.TrySetResult(true);
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

    private sealed class ForegroundTrustLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly Action _onForegroundLoadCaptured;
        private readonly TaskCompletionSource<bool> _releaseRecovery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;
        private int _injectForegroundLoss;
        private int _foregroundLossTriggered;
        private int _recoveryGateEntered;

        public ForegroundTrustLoader(IProjectLoader inner, Action onForegroundLoadCaptured)
        {
            _inner = inner;
            _onForegroundLoadCaptured = onForegroundLoadCaptured;
        }

        public TaskCompletionSource<bool> RecoveryLoadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount => Volatile.Read(ref _loadCount);

        public void EnableForegroundLoss() => Interlocked.Exchange(ref _injectForegroundLoss, 1);

        public void ReleaseRecovery() => _releaseRecovery.TrySetResult(true);

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            var loadCount = Interlocked.Increment(ref _loadCount);
            if (Volatile.Read(ref _foregroundLossTriggered) != 0
                && Interlocked.Exchange(ref _recoveryGateEntered, 1) == 0)
            {
                RecoveryLoadEntered.TrySetResult(true);
                await _releaseRecovery.Task.WaitAsync(cancellationToken);
            }

            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (Volatile.Read(ref _injectForegroundLoss) != 0
                && Interlocked.Exchange(ref _foregroundLossTriggered, 1) == 0)
            {
                _onForegroundLoadCaptured();
            }

            return loaded;
        }
    }

    private sealed class FlakyDiscoveryLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly string _importPath;
        private int _loadCount;
        private int _corrupted;

        public FlakyDiscoveryLoader(IProjectLoader inner, string importPath)
        {
            _inner = inner;
            _importPath = importPath;
        }

        public int LoadCount => Volatile.Read(ref _loadCount);

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (Interlocked.Exchange(ref _corrupted, 1) == 0)
            {
                await File.WriteAllTextAsync(_importPath, "<Project><PropertyGroup>", cancellationToken);
            }

            return loaded;
        }
    }

    private sealed class BlockingCommitter : IAtomicCacheCommitter, IDisposable
    {
        private readonly int _blockedCommitNumber;
        private readonly TaskCompletionSource<bool> _releaseBlockedCommit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _commitCount;

        public BlockingCommitter(int blockedCommitNumber)
        {
            if (blockedCommitNumber is < 1 or > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(blockedCommitNumber));
            }

            _blockedCommitNumber = blockedCommitNumber;
        }

        public TaskCompletionSource<bool> BlockedCommitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Commit(string temporaryPath, string destinationPath)
        {
            if (Interlocked.Increment(ref _commitCount) == _blockedCommitNumber)
            {
                BlockedCommitStarted.TrySetResult(true);
                _releaseBlockedCommit.Task.GetAwaiter().GetResult();
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }

        public void ReleaseBlockedCommit() => _releaseBlockedCommit.TrySetResult(true);

        public void Dispose() => ReleaseBlockedCommit();
    }

    private sealed class GatedRecoveryLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TaskCompletionSource<bool> _releaseRecovery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;
        private int _armNextRecovery;

        public GatedRecoveryLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public int LoadCount => Volatile.Read(ref _loadCount);

        public TaskCompletionSource<bool> RecoveryLoadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ArmNextRecovery() => Interlocked.Exchange(ref _armNextRecovery, 1);

        public async Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
            var loaded = await _inner.LoadAsync(request, cancellationToken);
            if (Interlocked.Exchange(ref _armNextRecovery, 0) != 0)
            {
                RecoveryLoadEntered.TrySetResult(true);
                try
                {
                    await _releaseRecovery.Task.WaitAsync(cancellationToken);
                }
                catch
                {
                    loaded.Dispose();
                    throw;
                }
            }

            return loaded;
        }

        public void ReleaseRecovery() => _releaseRecovery.TrySetResult(true);
    }
}
