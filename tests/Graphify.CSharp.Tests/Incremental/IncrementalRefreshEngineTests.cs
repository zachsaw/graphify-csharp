using System.Diagnostics;
using System.Text.Json;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class IncrementalRefreshEngineTests
{
    [Fact]
    public async Task Reuses_a_clean_solution_and_reconstructs_the_same_graph_as_full_extraction()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var outputPath = Path.Combine(fixture.Root, "graphify-out", "csharp.json");
            var engine = new IncrementalRefreshEngine();

            var first = await engine.RefreshAsync(fixture.Request, outputPath);
            var second = await engine.RefreshAsync(fixture.Request, outputPath);

            Assert.Equal(2, first.ExtractedProjectCount);
            Assert.Equal(0, first.ReusedProjectCount);
            Assert.Equal(0, second.ExtractedProjectCount);
            Assert.Equal(2, second.ReusedProjectCount);
            Assert.Equal(first.OutputDigest, second.OutputDigest);

            using var output = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(first.Graph.Nodes.Length, output.RootElement.GetProperty("nodes").GetArrayLength());
            Assert.Equal(first.Graph.Edges.Length, output.RootElement.GetProperty("edges").GetArrayLength());

            using var loaded = await new RoslynWorkspaceLoader().LoadAsync(fixture.Request);
            var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
            var fullExtractor = new SemanticReferenceExtractor();
            var fullGraph = await fullExtractor.ExtractAsync(loaded, catalog);
            Assert.Equal(
                fullGraph.Nodes.Select(node => node.Id),
                first.Graph.Nodes.Select(node => node.Id));
            Assert.Equal(
                fullGraph.Edges.Select(edge => edge.DeduplicationKey),
                first.Graph.Edges.Select(edge => edge.DeduplicationKey));
            Assert.Equal(
                new GraphifyJsonSerializer().Serialize(
                    fullGraph,
                    new GraphifySerializationOptions(
                        loaded.Diagnostics
                            .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
                            .Concat(catalog.Diagnostics)
                            .Concat(fullExtractor.Diagnostics))),
                await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_changed_project_invalidates_reverse_project_references()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var outputPath = Path.Combine(fixture.Root, "graphify-out", "csharp.json");
            var engine = new IncrementalRefreshEngine();
            await engine.RefreshAsync(fixture.Request, outputPath);

            await File.AppendAllTextAsync(fixture.LibrarySourcePath, "\n// changed\n");
            var refreshed = await engine.RefreshAsync(fixture.Request, outputPath);

            Assert.Equal(2, refreshed.ExtractedProjectCount);
            Assert.Equal(0, refreshed.ReusedProjectCount);
            Assert.Contains("Library.Api.Get", refreshed.Graph.Nodes.Select(node => node.Label));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_changed_evaluated_import_invalidates_persisted_project_reuse()
    {
        var fixture = await CreateImportedConfigurationFixtureAsync();
        try
        {
            var outputPath = Path.Combine(fixture.Root, "graphify-out", "csharp.json");
            var engine = new IncrementalRefreshEngine();
            var first = await engine.RefreshAsync(fixture.Request, outputPath);

            await File.WriteAllTextAsync(
                fixture.ImportPath,
                "<Project><PropertyGroup><DefineConstants>$(DefineConstants);ENABLED_BY_IMPORT</DefineConstants></PropertyGroup></Project>");
            var refreshed = await engine.RefreshAsync(fixture.Request, outputPath);

            Assert.Equal(1, first.ExtractedProjectCount);
            Assert.Equal(1, refreshed.ExtractedProjectCount);
            Assert.Equal(0, refreshed.ReusedProjectCount);
            Assert.Contains(
                "ImportedConfigurationFixture.EnabledByImport",
                refreshed.Graph.Nodes.Select(node => node.Label));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Added_and_deleted_sources_invalidate_the_owning_project()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var outputPath = Path.Combine(fixture.Root, "graphify-out", "csharp.json");
            var engine = new IncrementalRefreshEngine();
            await engine.RefreshAsync(fixture.Request, outputPath);

            var addedSourcePath = Path.Combine(fixture.AppDirectory, "Added.cs");
            await File.WriteAllTextAsync(
                addedSourcePath,
                "namespace App; public static class Added { public static int Value => 42; }\n");
            var withAdded = await engine.RefreshAsync(fixture.Request, outputPath);
            Assert.Equal(1, withAdded.ExtractedProjectCount);
            Assert.Equal(1, withAdded.ReusedProjectCount);
            Assert.Contains("App.Added", withAdded.Graph.Nodes.Select(node => node.Label));

            File.Delete(addedSourcePath);
            var withDeleted = await engine.RefreshAsync(fixture.Request, outputPath);
            Assert.Equal(1, withDeleted.ExtractedProjectCount);
            Assert.Equal(1, withDeleted.ReusedProjectCount);
            Assert.DoesNotContain("App.Added", withDeleted.Graph.Nodes.Select(node => node.Label));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_complete_output()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var outputPath = Path.Combine(fixture.Root, "graphify-out", "csharp.json");
            var engine = new IncrementalRefreshEngine();
            await engine.RefreshAsync(fixture.Request, outputPath);
            var before = await File.ReadAllTextAsync(outputPath);

            await File.AppendAllTextAsync(fixture.AppSourcePath, "\n// changed\n");
            var failingEngine = new IncrementalRefreshEngine(
                outputPublisher: new IncrementalOutputPublisher(committer: new ThrowingCommitter()));
            await Assert.ThrowsAsync<IOException>(() => failingEngine.RefreshAsync(fixture.Request, outputPath));

            Assert.Equal(before, await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Removing_an_unreferenced_solution_project_publishes_the_reduced_graph()
    {
        var fixture = await CreateIndependentSolutionFixtureAsync();
        try
        {
            var outputPath = Path.Combine(fixture.Root, "graphify-out", "csharp.json");
            var engine = new IncrementalRefreshEngine();
            var first = await engine.RefreshAsync(fixture.Request, outputPath, rebuild: true);

            Assert.Contains("B.OnlyInB", first.Graph.Nodes.Select(node => node.Label));
            Assert.Contains("B.OnlyInB", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                fixture.SolutionPath,
                "<Solution><Project Path=\"A/A.csproj\" /></Solution>");
            var removed = await engine.RefreshAsync(fixture.Request, outputPath);

            Assert.DoesNotContain("B.OnlyInB", removed.Graph.Nodes.Select(node => node.Label));
            Assert.True(removed.OutputRepublished);
            Assert.DoesNotContain("B.OnlyInB", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
            Assert.Equal(removed.OutputDigest, IncrementalHashing.Sha256File(outputPath));

            var cache = await new IncrementalCacheStore().LoadAsync(
                IncrementalCachePath.ForOutput(outputPath),
                new RefreshRequestIdentity(
                    fixture.Request.InputPath,
                    fixture.Request.RepositoryRoot,
                    fixture.Request.Configuration,
                    fixture.Request.TargetFramework));
            Assert.NotNull(cache.State);
            Assert.DoesNotContain(cache.State!.Manifest, entry => entry.Project.Key.Contains("B/B.csproj", StringComparison.Ordinal));

            var clean = await engine.RefreshAsync(fixture.Request, outputPath);
            Assert.DoesNotContain("B.OnlyInB", clean.Graph.Nodes.Select(node => node.Label));
            Assert.DoesNotContain("B.OnlyInB", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
            Assert.False(clean.OutputRepublished);
            Assert.Equal(0, clean.ExtractedProjectCount);
            Assert.Equal(1, clean.ReusedProjectCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-refresh-tests", Guid.NewGuid().ToString("N"));
        var appDirectory = Path.Combine(root, "App");
        var libraryDirectory = Path.Combine(root, "Library");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(libraryDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(libraryDirectory, "Library.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        var librarySourcePath = Path.Combine(libraryDirectory, "Api.cs");
        await File.WriteAllTextAsync(
            librarySourcePath,
            "namespace Library; public static class Api { public static int Get() => 1; }\n");

        await File.WriteAllTextAsync(
            Path.Combine(appDirectory, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Library/Library.csproj" />
              </ItemGroup>
            </Project>
            """);
        var appSourcePath = Path.Combine(appDirectory, "Consumer.cs");
        await File.WriteAllTextAsync(
            appSourcePath,
            "namespace App; public static class Consumer { public static int Run() => Library.Api.Get(); }\n");

        return new Fixture(
            root,
            appDirectory,
            librarySourcePath,
            appSourcePath,
            new ProjectLoadRequest(
                Path.Combine(appDirectory, "App.csproj"),
                root,
                configuration: "Release",
                targetFramework: "net10.0"));
    }

    private static async Task<ImportedConfigurationFixture> CreateImportedConfigurationFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-refresh-import-tests", Guid.NewGuid().ToString("N"));
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
            new ProjectLoadRequest(projectPath, root, configuration: "Release", targetFramework: "net10.0"));
    }

    private static async Task<IndependentSolutionFixture> CreateIndependentSolutionFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-refresh-solution-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var projectAPath = Path.Combine(root, "A", "A.csproj");
            var projectBPath = Path.Combine(root, "B", "B.csproj");
            var solutionPath = Path.Combine(root, "Independent.slnx");
            Directory.CreateDirectory(Path.GetDirectoryName(projectAPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(projectBPath)!);

            const string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
            await File.WriteAllTextAsync(projectAPath, project);
            await File.WriteAllTextAsync(projectBPath, project);
            await File.WriteAllTextAsync(
                Path.Combine(root, "A", "Code.cs"),
                "namespace A; public sealed class OnlyInA { }\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "B", "Code.cs"),
                "namespace B; public sealed class OnlyInB { }\n");
            await File.WriteAllTextAsync(
                solutionPath,
                "<Solution><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.csproj\" /></Solution>");

            var restoreInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            restoreInfo.ArgumentList.Add("restore");
            restoreInfo.ArgumentList.Add(solutionPath);
            restoreInfo.ArgumentList.Add("--nologo");
            restoreInfo.ArgumentList.Add("--disable-build-servers");
            RemoveInheritedMsBuildEnvironment(restoreInfo);

            using var restore = Process.Start(restoreInfo);
            if (restore is null)
            {
                throw new InvalidOperationException("Could not start dotnet restore for the independent solution fixture.");
            }

            var standardOutput = restore.StandardOutput.ReadToEndAsync();
            var standardError = restore.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await restore.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!restore.HasExited)
                {
                    restore.Kill(entireProcessTree: true);
                }

                await restore.WaitForExitAsync();
                throw new TimeoutException(
                    $"dotnet restore timed out for the independent solution fixture after 60 seconds. " +
                    $"stdout: {await standardOutput}\nstderr: {await standardError}");
            }

            var output = await standardOutput;
            var error = await standardError;
            if (restore.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"dotnet restore failed for the independent solution fixture with exit code {restore.ExitCode}. " +
                    $"stdout: {output}\nstderr: {error}");
            }

            return new IndependentSolutionFixture(
                root,
                solutionPath,
                new ProjectLoadRequest(
                    solutionPath,
                    root,
                    configuration: "Release",
                    targetFramework: "net10.0"));
        }
        catch
        {
            DeleteTemporaryDirectory(root);
            throw;
        }
    }

    private static void RemoveInheritedMsBuildEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (key.Equals("MSBUILD_EXE_PATH", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MSBuildSDKsPath", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MSBuildExtensionsPath", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
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
        string AppDirectory,
        string LibrarySourcePath,
        string AppSourcePath,
        ProjectLoadRequest Request);

    private sealed record ImportedConfigurationFixture(
        string Root,
        string ImportPath,
        ProjectLoadRequest Request);

    private sealed record IndependentSolutionFixture(
        string Root,
        string SolutionPath,
        ProjectLoadRequest Request);

    private sealed class ThrowingCommitter : IAtomicCacheCommitter
    {
        public void Commit(string temporaryPath, string destinationPath) =>
            throw new IOException("Synthetic atomic commit failure.");
    }
}
