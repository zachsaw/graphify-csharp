#if NET11_0_OR_GREATER
using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class CSharp15FeatureTests
{
    [Fact]
    public async Task Catalogs_union_declarations_and_case_relationships_without_diagnostics()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        Assert.Empty(catalog.Diagnostics);

        var pet = FindType(catalog, "Pet");
        var result = FindType(catalog, "Result");
        var cat = FindType(catalog, "Cat");
        var dog = FindType(catalog, "Dog");
        var bird = FindType(catalog, "Bird");
        var resultTypeParameter = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.TypeParameter
            && declaration.Identity.Name == "T"
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "Result")));

        Assert.Equal("union", pet.Node.Properties["declaration_kind"]);
        Assert.Equal("union", result.Node.Properties["declaration_kind"]);
        Assert.Contains(graph.Edges, edge => IsEdge(edge, pet, cat, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, pet, dog, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, pet, bird, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, result, resultTypeParameter, GraphRelation.References));

        var namespaceDeclaration = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Namespace
            && declaration.Identity.Name == "CSharp15Fixture"));
        Assert.DoesNotContain(graph.Edges, edge =>
            edge.SourceId == namespaceDeclaration.Node.Id
            && (edge.TargetId == cat.Node.Id || edge.TargetId == dog.Node.Id || edge.TargetId == bird.Node.Id));
    }

    [Fact]
    public async Task Extracts_references_from_union_case_consumers_and_is_deterministic()
    {
        using var firstLoaded = await LoadFixtureAsync();
        var firstCatalog = await new DeclarationCatalogBuilder().BuildAsync(firstLoaded);
        var firstGraph = await new SemanticReferenceExtractor().ExtractAsync(firstLoaded, firstCatalog);

        using var secondLoaded = await LoadFixtureAsync();
        var secondCatalog = await new DeclarationCatalogBuilder().BuildAsync(secondLoaded);
        var secondGraph = await new SemanticReferenceExtractor().ExtractAsync(secondLoaded, secondCatalog);

        var firstDescribe = FindMember(firstCatalog, "UnionConsumer", "Describe");
        var firstCat = FindType(firstCatalog, "Cat");
        var firstDog = FindType(firstCatalog, "Dog");
        var firstBird = FindType(firstCatalog, "Bird");
        Assert.Contains(firstGraph.Edges, edge => IsEdge(edge, firstDescribe, firstCat, GraphRelation.References));
        Assert.Contains(firstGraph.Edges, edge => IsEdge(edge, firstDescribe, firstDog, GraphRelation.References));
        Assert.Contains(firstGraph.Edges, edge => IsEdge(edge, firstDescribe, firstBird, GraphRelation.References));

        var serializer = new GraphifyJsonSerializer();
        var options = new GraphifySerializationOptions(firstCatalog.Diagnostics);
        Assert.Equal(
            serializer.Serialize(firstGraph, options),
            serializer.Serialize(secondGraph, new GraphifySerializationOptions(secondCatalog.Diagnostics)));
    }

    private static SymbolDeclaration FindType(DeclarationCatalog catalog, string name) =>
        Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Type
            && declaration.Identity.Namespace == "CSharp15Fixture"
            && declaration.Identity.ContainingTypes.Length == 0
            && declaration.Identity.Name == name));

    private static SymbolDeclaration FindMember(DeclarationCatalog catalog, string containingType, string name) =>
        Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Namespace == "CSharp15Fixture"
            && declaration.Identity.ContainingTypes.Any(type => type.Name == containingType)
            && declaration.Identity.Name == name));

    private static bool IsEdge(GraphEdge edge, SymbolDeclaration source, SymbolDeclaration target, GraphRelation relation) =>
        edge.SourceId == source.Node.Id && edge.TargetId == target.Node.Id && edge.Relation == relation;

    private static async Task<LoadedSolution> LoadFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/CSharp15Fixture/CSharp15Fixture.csproj"),
            root,
            configuration: "Release",
            targetFramework: "net11.0"));
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
#endif
