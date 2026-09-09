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

    private sealed class ThrowingCommitter : IAtomicCacheCommitter
    {
        public void Commit(string temporaryPath, string destinationPath) =>
            throw new IOException("Synthetic atomic commit failure.");
    }
}
