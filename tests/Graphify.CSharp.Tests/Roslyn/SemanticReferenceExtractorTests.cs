using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class SemanticReferenceExtractorTests
{
    [Fact]
    public async Task Emits_resolved_call_and_reference_edges_with_source_locations()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var productionCaller = Find(catalog, "ReferenceFixture.Production", "ProductionCaller", "Run");
        var service = Find(catalog, "ReferenceFixture.Production", "Service", "Called", "int");
        var methodGroup = Find(catalog, "ReferenceFixture.Production", "Service", "MethodGroup", "int");
        var property = Find(catalog, "ReferenceFixture.Production", "Service", "Property");
        var field = Find(catalog, "ReferenceFixture.Production", "Service", "Field");
        var referencedType = Find(catalog, "ReferenceFixture.Production", "ReferencedType");


        Assert.Contains(graph.Edges, edge => IsEdge(edge, productionCaller, service, GraphRelation.Calls));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, productionCaller, methodGroup, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, productionCaller, property, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, productionCaller, field, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, productionCaller, referencedType, GraphRelation.References));
        Assert.All(graph.Edges, edge => Assert.Equal(EvidenceKind.Extracted, edge.Evidence));
        Assert.All(graph.Edges, edge => Assert.NotEmpty(edge.SourceLocations));
    }

    [Fact]
    public async Task Resolves_the_overload_called_from_the_test_namespace()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var testCaller = Find(catalog, "ReferenceFixture.Tests", "TestCaller", "Run");
        var stringOverload = Find(catalog, "ReferenceFixture.Production", "Service", "Called", "string");

        Assert.Contains(graph.Edges, edge => IsEdge(edge, testCaller, stringOverload, GraphRelation.Calls));
    }

    [Fact]
    public async Task Leaves_a_declared_but_unreferenced_member_without_inbound_edges()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);
        var unused = Find(catalog, "ReferenceFixture.Production", "Service", "Unused");

        Assert.DoesNotContain(graph.Edges, edge => edge.TargetId == unused.Node.Id);
    }

    private static async Task<LoadedSolution> LoadFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj"),
            root,
            targetFramework: "net10.0"));
    }

    private static SymbolDeclaration Find(DeclarationCatalog catalog, string namespaceName, string typeName, string memberName, params string[] parameterTypes)
    {
        var matches = catalog.Declarations.Where(declaration =>
            declaration.Identity.Namespace == namespaceName
            && declaration.Identity.ContainingTypes.Length == 1
            && declaration.Identity.ContainingTypes[0].Name == typeName
            && declaration.Identity.Name == memberName
            && declaration.Identity.Parameters.Select(parameter => parameter.TypeName).SequenceEqual(parameterTypes));
        return Assert.Single(matches);
    }

    private static SymbolDeclaration Find(DeclarationCatalog catalog, string namespaceName, string typeName)
    {
        var matches = catalog.Declarations.Where(declaration =>
            declaration.Identity.Namespace == namespaceName
            && declaration.Identity.ContainingTypes.Length == 0
            && declaration.Identity.Name == typeName
            && declaration.Identity.Kind == global::Graphify.CSharp.Domain.SymbolKind.Type);
        return Assert.Single(matches);
    }

    private static bool IsEdge(GraphEdge edge, SymbolDeclaration source, SymbolDeclaration target, GraphRelation relation) =>
        edge.SourceId == source.Node.Id && edge.TargetId == target.Node.Id && edge.Relation == relation;

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
