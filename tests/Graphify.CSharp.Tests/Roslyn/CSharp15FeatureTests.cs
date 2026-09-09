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

    [Fact]
    public async Task Extracts_references_for_the_other_csharp15_features()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        Assert.Empty(catalog.Diagnostics);

        var closedState = FindType(catalog, "ClosedState");
        Assert.Equal("true", closedState.Node.Properties["is_closed"]);
        var closed = FindType(catalog, "Closed");
        var open = FindType(catalog, "Open");
        var closedConsumer = FindMember(catalog, "ClosedHierarchyConsumer", "Describe");
        Assert.Contains(graph.Edges, edge => IsEdge(edge, closed, closedState, GraphRelation.Inherits));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, open, closedState, GraphRelation.Inherits));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, closedConsumer, closed, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, closedConsumer, open, GraphRelation.References));

        var indexer = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Namespace == "CSharp15Fixture"
            && declaration.Identity.Kind == SymbolKind.Property
            && declaration.Node.Properties["declaration_kind"] == "indexer"));
        var indexerConsumer = FindMember(catalog, "ExtensionIndexerConsumer", "Read");
        Assert.Contains(graph.Edges, edge => IsEdge(edge, indexerConsumer, indexer, GraphRelation.References));

        var collectionBuilder = FindMember(catalog, "BufferedValuesBuilder", "Create");
        var collectionConsumer = FindMember(catalog, "CollectionArgumentConsumer", "Create");
        Assert.Contains(graph.Edges, edge => IsEdge(edge, collectionConsumer, collectionBuilder, GraphRelation.Calls));
        var collectionConstructor = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Constructor
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "CapacityValues")
            && declaration.Identity.Parameters.Select(parameter => parameter.TypeName).SequenceEqual(["int"])));
        var constructorConsumer = FindMember(catalog, "CollectionArgumentConsumer", "CreateWithConstructor");
        Assert.Contains(graph.Edges, edge => IsEdge(edge, constructorConsumer, collectionConstructor, GraphRelation.Calls));

        var label = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Label
            && declaration.Identity.Name == "outer"));
        var labeledConsumer = FindMember(catalog, "LabeledJumpConsumer", "Scan");
        Assert.Contains(graph.Edges, edge => IsEdge(edge, labeledConsumer, label, GraphRelation.References));

        var nativeValue = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Type
            && declaration.Identity.Name == "NativeValue"
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "MemorySafetyConsumer")));
        var sizeOfConsumer = FindMember(catalog, "MemorySafetyConsumer", "SizeOfNativeValue");
        Assert.Contains(graph.Edges, edge => IsEdge(edge, sizeOfConsumer, nativeValue, GraphRelation.References));
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "GetProcessId");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Field
            && declaration.Identity.Name == "Value"
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "ExplicitLayoutValue"));
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
