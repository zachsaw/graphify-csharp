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

    [Fact]
    public async Task Emits_interface_implementation_and_override_edges()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var implementation = Find(catalog, "ReferenceFixture.Production", "Contract", "Execute", "int");
        var contractMethod = Find(catalog, "ReferenceFixture.Production", "IContract", "Execute", "int");
        var baseMethod = Find(catalog, "ReferenceFixture.Production", "BaseContract", "Execute", "int");
        var contractType = Find(catalog, "ReferenceFixture.Production", "Contract");
        var contractInterface = Find(catalog, "ReferenceFixture.Production", "IContract");

        Assert.Contains(graph.Edges, edge => IsEdge(edge, implementation, contractMethod, GraphRelation.Implements));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, implementation, baseMethod, GraphRelation.Overrides));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, contractType, contractInterface, GraphRelation.Implements));
    }

    [Fact]
    public async Task Catalogs_namespaces_enum_members_delegates_and_local_functions()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);

        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Namespace
            && declaration.Identity.DisplayName == "ReferenceFixture.Production");
        Assert.Equal("enum", Find(catalog, "ReferenceFixture.Production", "BaseKind").Node.Properties["declaration_kind"]);
        Assert.Equal("enum_member", Find(catalog, "ReferenceFixture.Production", "BaseKind", "Selected").Node.Properties["declaration_kind"]);
        Assert.Equal("delegate", Find(catalog, "ReferenceFixture.Production", "Transformer").Node.Properties["declaration_kind"]);

        var localFunction = Find(catalog, "ReferenceFixture.Production", "GenericContract", "LocalFunction", "T");
        Assert.Equal(SymbolKind.Method, localFunction.Identity.Kind);
        Assert.NotEmpty(localFunction.Identity.ContainingMemberPath);
    }

    [Fact]
    public async Task Resolves_enum_generic_inheritance_and_local_function_relationships()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var enumMember = Find(catalog, "ReferenceFixture.Production", "DerivedKind", "Selected");
        var referencedEnumMember = Find(catalog, "ReferenceFixture.Production", "BaseKind", "Selected");
        var genericType = Find(catalog, "ReferenceFixture.Production", "GenericContract");
        var genericBase = Find(catalog, "ReferenceFixture.Production", "GenericBase");
        var genericInterface = Find(catalog, "ReferenceFixture.Production", "IGenericContract");
        var run = Find(catalog, "ReferenceFixture.Production", "GenericContract", "Run", "T");
        var localFunction = Find(catalog, "ReferenceFixture.Production", "GenericContract", "LocalFunction", "T");

        Assert.Contains(graph.Edges, edge => IsEdge(edge, enumMember, referencedEnumMember, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, genericType, genericBase, GraphRelation.Inherits));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, genericType, genericInterface, GraphRelation.Implements));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, run, localFunction, GraphRelation.Calls));
    }

    [Fact]
    public async Task Resolves_source_calls_across_project_compilations()
    {
        var root = RepositoryRoot();
        using var loaded = await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "Graphify.CSharp.sln"),
            root,
            configuration: "Release"));
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var main = FindProjectMember(catalog, "src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj", "Graphify.CSharp.Cli", "Program", SymbolKind.Method, "Main", "string[]");
        var loader = FindProjectMember(catalog, "src/Graphify.CSharp/Graphify.CSharp.csproj", "Graphify.CSharp.Roslyn", "RoslynWorkspaceLoader", SymbolKind.Method, "LoadAsync", "Graphify.CSharp.Roslyn.ProjectLoadRequest", "System.Threading.CancellationToken");
        var builder = FindProjectMember(catalog, "src/Graphify.CSharp/Graphify.CSharp.csproj", "Graphify.CSharp.Roslyn", "DeclarationCatalogBuilder", SymbolKind.Method, "BuildAsync", "Graphify.CSharp.Roslyn.LoadedSolution", "System.Threading.CancellationToken");
        var extractor = FindProjectMember(catalog, "src/Graphify.CSharp/Graphify.CSharp.csproj", "Graphify.CSharp.Roslyn", "SemanticReferenceExtractor", SymbolKind.Method, "ExtractAsync", "Graphify.CSharp.Roslyn.LoadedSolution", "Graphify.CSharp.Roslyn.DeclarationCatalog", "System.Threading.CancellationToken");
        var serializer = FindProjectMember(catalog, "src/Graphify.CSharp/Graphify.CSharp.csproj", "Graphify.CSharp.Graphify", "GraphifyJsonSerializer", SymbolKind.Method, "Serialize", "Graphify.CSharp.Domain.GraphSnapshot", "Graphify.CSharp.Graphify.GraphifySerializationOptions");

        Assert.Contains(graph.Edges, edge => IsEdge(edge, main, loader, GraphRelation.Calls));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, main, builder, GraphRelation.Calls));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, main, extractor, GraphRelation.Calls));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, main, serializer, GraphRelation.Calls));
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

    private static SymbolDeclaration FindProjectMember(
        DeclarationCatalog catalog,
        string projectPath,
        string namespaceName,
        string typeName,
        SymbolKind kind,
        string memberName,
        params string[] parameterTypes)
    {
        var matches = catalog.Declarations.Where(declaration =>
            declaration.Identity.Project.RelativePath == projectPath
            && declaration.Identity.Namespace == namespaceName
            && declaration.Identity.ContainingTypes.Length == 1
            && declaration.Identity.ContainingTypes[0].Name == typeName
            && declaration.Identity.Kind == kind
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
