using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class FileBasedAppTests
{
    [Fact]
    public async Task Loads_sdk_file_based_app_and_remaps_sources()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        Assert.Empty(catalog.Diagnostics);
        Assert.Empty(loaded.Diagnostics);
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "Run"
            && declaration.Identity.Namespace == "FileBasedFixture");

        var main = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Name == "<Main>$"));
        var run = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "Run"
            && declaration.Identity.Namespace == "FileBasedFixture"));
        Assert.Contains(graph.Edges, edge =>
            edge.SourceId == main.Node.Id
            && edge.TargetId == run.Node.Id
            && edge.Relation == GraphRelation.Calls);
        Assert.All(catalog.Declarations.SelectMany(declaration => declaration.Node.SourceLocations), location =>
            Assert.DoesNotContain("graphify-csharp-file-", location.FilePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task File_based_app_extraction_is_byte_deterministic()
    {
        using var firstLoaded = await LoadFixtureAsync();
        var firstCatalog = await new DeclarationCatalogBuilder().BuildAsync(firstLoaded);
        var firstGraph = await new SemanticReferenceExtractor().ExtractAsync(firstLoaded, firstCatalog);

        using var secondLoaded = await LoadFixtureAsync();
        var secondCatalog = await new DeclarationCatalogBuilder().BuildAsync(secondLoaded);
        var secondGraph = await new SemanticReferenceExtractor().ExtractAsync(secondLoaded, secondCatalog);

        var serializer = new GraphifyJsonSerializer();
        Assert.Equal(
            serializer.Serialize(firstGraph, new GraphifySerializationOptions(firstCatalog.Diagnostics)),
            serializer.Serialize(secondGraph, new GraphifySerializationOptions(secondCatalog.Diagnostics)));
    }

    [Fact]
    public async Task Loads_file_based_app_sdk_directives_and_project_references()
    {
        using var loaded = await LoadDirectiveFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);

        Assert.Empty(catalog.Diagnostics);
        Assert.Empty(loaded.Diagnostics);
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Namespace == "LanguageSurfaceFixture"
            && declaration.Identity.Kind == SymbolKind.Type
            && declaration.Identity.Name == "SurfaceConsumer");
    }

    private static async Task<LoadedSolution> LoadFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/FileBasedFixture/App.cs"),
            root,
            configuration: "Release",
            targetFramework: "net10.0"));
    }

    private static async Task<LoadedSolution> LoadDirectiveFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/FileBasedFixture/DirectiveProbe.cs"),
            root,
            configuration: "Release",
            targetFramework: "net10.0"));
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
}
