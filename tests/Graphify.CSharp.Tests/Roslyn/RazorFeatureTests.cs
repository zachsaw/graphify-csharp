using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;
using System.Diagnostics;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class RazorFeatureTests
{
    [Fact]
    public async Task Extracts_Razor_declarations_calls_and_mapped_locations_deterministically()
    {
        var root = RepositoryRoot();
        var request = new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/RazorFixture/RazorFixture.csproj"),
            root,
            configuration: "Release",
            targetFramework: "net10.0");
        string? previous = null;
        for (var run = 0; run < 2; run++)
        {
            using var loaded = await new RoslynWorkspaceLoader().LoadAsync(request);
            Assert.Empty(loaded.Diagnostics);
            var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
            var extractor = new SemanticReferenceExtractor();
            var graph = await extractor.ExtractAsync(loaded, catalog);
            Assert.Empty(catalog.Diagnostics);
            Assert.Empty(extractor.Diagnostics);

            var component = Assert.Single(graph.Nodes.Where(node => node.Label == "RazorFixture.Counter"));
            Assert.Single(graph.Nodes.Where(node => node.Label == "RazorFixture.Status"));
            var load = Assert.Single(graph.Nodes.Where(node => node.Label == "RazorFixture.Counter.Load"));
            var service = Assert.Single(graph.Nodes.Where(node => node.Label == "RazorFixture.CounterService.Get(int)"));
            var otherOverload = Assert.Single(graph.Nodes.Where(node => node.Label == "RazorFixture.CounterService.Get(string)"));
            var codeBehind = Assert.Single(graph.Nodes.Where(node => node.Label == "RazorFixture.Counter.FromCodeBehind"));
            Assert.Contains(component.SourceLocations, location => location.FilePath.EndsWith("Counter.razor.cs", StringComparison.Ordinal));
            Assert.Contains(load.SourceLocations, location => location.FilePath.EndsWith("Counter.razor", StringComparison.Ordinal)
                && location.Line == 9);
            var call = Assert.Single(graph.Edges.Where(edge => edge.SourceId == load.Id
                && edge.TargetId == service.Id && edge.Relation == GraphRelation.Calls));
            Assert.Contains(call.SourceLocations, location => location.FilePath.EndsWith("Counter.razor", StringComparison.Ordinal)
                && location.Line == 12);
            Assert.DoesNotContain(graph.Edges, edge => edge.SourceId == load.Id && edge.TargetId == otherOverload.Id);
            Assert.Contains(graph.Edges, edge => edge.SourceId == codeBehind.Id && edge.TargetId == load.Id
                && edge.Relation == GraphRelation.Calls);
            Assert.Contains(graph.Edges, edge => edge.TargetId == load.Id && edge.Relation == GraphRelation.References);
            Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Local
                && declaration.Identity.Name == "count");

            var serialized = new GraphifyJsonSerializer().Serialize(graph);
            if (previous is not null)
            {
                Assert.Equal(previous, serialized);
            }
            previous = serialized;
        }
    }

    [Fact]
    public async Task Reports_generator_load_failures_in_export_diagnostics()
    {
        var root = await CopyFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "BrokenGenerator.dll"), "not an assembly");
            var projectPath = Path.Combine(root, "RazorFixture.csproj");
            await File.WriteAllTextAsync(projectPath, (await File.ReadAllTextAsync(projectPath)).Replace(
                "</Project>", "<ItemGroup><Analyzer Include=\"BrokenGenerator.dll\" /></ItemGroup></Project>", StringComparison.Ordinal));
            var output = Path.Combine(root, "graphify-out", "csharp.json");
            await new IncrementalRefreshEngine().RefreshAsync(
                new ProjectLoadRequest(projectPath, root, "Release", "net10.0"), output);
            var json = await File.ReadAllTextAsync(output);
            Assert.Contains("SourceGeneratorLoad:", json);
            Assert.Contains("BrokenGenerator.dll", json);
            Assert.Contains("Generated declarations and relationships may be missing", json);
            Assert.Contains("RazorFixture.Counter.Load", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_export_refreshes_Razor_edits_and_deletions_without_reusing_stale_generated_code()
    {
        var root = await CopyFixtureAsync();
        try
        {
            var output = Path.Combine(root, "graphify-out", "csharp.json");
            var request = new ProjectLoadRequest(Path.Combine(root, "RazorFixture.csproj"), root, "Release", "net10.0");
            Assert.Equal(0, await global::Graphify.CSharp.Cli.Program.Main(
            [
                "export", "--input", request.InputPath, "--root", root,
                "--configuration", "Release", "--target-framework", "net10.0", "--output", output,
            ]));
            var original = await File.ReadAllTextAsync(output);
            Assert.Contains("RazorFixture.Status.get_Total", original);

            var engine = new IncrementalRefreshEngine();
            var unchanged = await engine.RefreshAsync(request, output);
            Assert.Equal(1, unchanged.ReusedProjectCount);
            Assert.Equal(original, await File.ReadAllTextAsync(output));

            var path = Path.Combine(root, "Status.razor");
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("Get(2)", "Get(\"changed\")", StringComparison.Ordinal));
            var changed = await engine.RefreshAsync(request, output);
            Assert.Equal(1, changed.ExtractedProjectCount);
            var caller = Assert.Single(changed.Graph.Nodes.Where(node => node.Label == "RazorFixture.Status.get_Total"));
            var target = Assert.Single(changed.Graph.Nodes.Where(node => node.Label == "RazorFixture.CounterService.Get(string)"));
            Assert.Contains(changed.Graph.Edges, edge => edge.SourceId == caller.Id && edge.TargetId == target.Id && edge.Relation == GraphRelation.Calls);

            File.Delete(path);
            var deleted = await engine.RefreshAsync(request, output);
            Assert.DoesNotContain(deleted.Graph.Nodes, node => node.Label.StartsWith("RazorFixture.Status", StringComparison.Ordinal));

            await File.WriteAllTextAsync(Path.Combine(root, "Added.razor"), "@namespace RazorFixture\n<p>Added component</p>\n");
            var added = await engine.RefreshAsync(request, output);
            Assert.Contains(added.Graph.Nodes, node => node.Label == "RazorFixture.Added");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Queries_Razor_arguments_by_original_path_and_refreshes_generated_callers()
    {
        var root = await CopyFixtureAsync();
        try
        {
            var request = new ProjectLoadRequest(Path.Combine(root, "RazorFixture.csproj"), root, "Release", "net10.0");
            await using var session = new IncrementalIndexSession(request, outputPath: null);
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var symbols = await session.ExecuteSemanticQueryAsync(new SemanticQuerySpec(
                "symbols", Search: "CounterService.Get(int)", Filters: new SemanticQueryFilters(Kind: "method")));
            Assert.True(symbols.Success, symbols.Error?.Message);
            var targetId = Assert.Single(symbols.Items).GetProperty("id").GetString();
            var path = Path.Combine(root, "Counter.razor");
            var query = new SemanticQuerySpec("arguments", SymbolId: targetId, Filters: new SemanticQueryFilters(Path: path));
            var arguments = await session.ExecuteSemanticQueryAsync(query);
            Assert.True(arguments.Success, arguments.Error?.Message);
            var argument = Assert.Single(arguments.Items);
            Assert.Equal("count", argument.GetProperty("expression_preview").GetString());
            Assert.Equal("count", argument.GetProperty("formal_parameter_name").GetString());

            var servicePath = Path.Combine(root, "CounterService.cs");
            await File.WriteAllTextAsync(servicePath, (await File.ReadAllTextAsync(servicePath)).Replace("int count) => count", "int amount) => amount", StringComparison.Ordinal));
            session.ReportFileChanged(servicePath);
            var rebound = await session.ExecuteSemanticQueryAsync(query).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(rebound.Success, rebound.Error?.Message);
            Assert.Equal("amount", Assert.Single(rebound.Items).GetProperty("formal_parameter_name").GetString());

            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("Get(count)", "Get(\"changed\")", StringComparison.Ordinal));
            session.ReportFileChanged(path);
            var after = await session.ExecuteSemanticQueryAsync(query).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(after.Success, after.Error?.Message);
            Assert.Empty(after.Items);
            Assert.NotEqual(arguments.Snapshot!.Id, after.Snapshot!.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CopyFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-razor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "tests/Fixtures/RazorFixture")))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        }
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add("-p:NuGetAudit=false");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        return root;
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
}
