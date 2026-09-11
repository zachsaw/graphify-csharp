using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
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
        var compare = Find(catalog, "ReferenceFixture.Production", "GenericContract", "Compare", "T");
        var compareTypeParameter = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.TypeParameter
            && declaration.Identity.Name == "T"
            && Microsoft.CodeAnalysis.SymbolEqualityComparer.Default.Equals(
                declaration.Symbol.ContainingSymbol,
                compare.Symbol)));

        Assert.Contains(graph.Edges, edge => IsEdge(edge, enumMember, referencedEnumMember, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, genericType, genericBase, GraphRelation.Inherits));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, genericType, genericInterface, GraphRelation.Implements));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, run, localFunction, GraphRelation.Calls));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, compare, compareTypeParameter, GraphRelation.References));
    }

    [Fact]
    public async Task Catalogs_source_symbols_for_every_declaration_shape()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);

        Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Alias
            && declaration.Identity.Name == "ServiceAlias");
        Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Label
            && declaration.Identity.Name == "finished");
        Assert.True(catalog.Declarations.Count(declaration => declaration.Identity.Kind == SymbolKind.Label) >= 3);
        Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.RangeVariable
            && declaration.Identity.Name == "item");
        Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Local
            && declaration.Identity.Name == "localConstant"
            && declaration.Node.Properties["declaration_kind"] == "local_constant");
        Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Parameter
            && declaration.Identity.Name == "reference");
        Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Parameter
            && declaration.Identity.Name == "implicitParameter");
        Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.TypeParameter
            && declaration.Identity.Name == "U");
        Assert.Contains(catalog.Declarations, declaration => declaration.Node.Properties["declaration_kind"] == "record");
        Assert.Contains(catalog.Declarations, declaration => declaration.Node.Properties["declaration_kind"] == "record_struct");
        Assert.Contains(catalog.Declarations, declaration => declaration.Node.Properties["declaration_kind"] == "destructor");
        Assert.Contains(catalog.Declarations, declaration => declaration.Node.Properties["declaration_kind"] == "conversion");
        Assert.Contains(catalog.Declarations, declaration => declaration.Node.Properties["declaration_kind"] == "userdefinedoperator");
        Assert.Contains(catalog.Declarations, declaration => declaration.Node.Properties["declaration_kind"] == "eventadd");
        Assert.Contains(catalog.Declarations, declaration => declaration.Node.Properties["declaration_kind"] == "eventremove");
        Assert.DoesNotContain(catalog.Declarations, declaration =>
            declaration.Identity.Namespace == "System"
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "ValueTuple"));

        var fileLocalTypes = catalog.Declarations
            .Where(declaration => declaration.Identity.Namespace == "ReferenceFixture.AllDeclarations"
                && declaration.Identity.Kind == SymbolKind.Type
                && declaration.Identity.Name == "SameName")
            .ToArray();
        Assert.Equal(2, fileLocalTypes.Length);
        Assert.NotEqual(fileLocalTypes[0].Identity.CanonicalKey, fileLocalTypes[1].Identity.CanonicalKey);

        var fileLocalMethods = catalog.Declarations
            .Where(declaration => declaration.Identity.Namespace == "ReferenceFixture.AllDeclarations"
                && declaration.Identity.ContainingTypes.Any(type => type.Name == "SameName")
                && declaration.Identity.Kind == SymbolKind.Method)
            .ToArray();
        Assert.Equal(2, fileLocalMethods.Length);
        Assert.NotEqual(fileLocalMethods[0].Identity.CanonicalKey, fileLocalMethods[1].Identity.CanonicalKey);
    }

    [Fact]
    public async Task Resolves_references_to_scoped_declarations_and_aliases()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var execute = Find(catalog, "ReferenceFixture.AllDeclarations", "DeclarationHost", "Execute", "int", "int", "int", "int[]");
        var alias = FindScoped(catalog, SymbolKind.Alias, "ServiceAlias");
        var local = FindScoped(catalog, SymbolKind.Local, "first");
        var parameter = FindScoped(catalog, SymbolKind.Parameter, "reference");
        var implicitParameter = FindScoped(catalog, SymbolKind.Parameter, "implicitParameter");
        var label = FindScoped(catalog, SymbolKind.Label, "finished");
        var rangeVariable = FindScoped(catalog, SymbolKind.RangeVariable, "item");
        var typeParameter = FindScoped(catalog, SymbolKind.TypeParameter, "U");
        var constrainedType = Find(catalog, "ReferenceFixture.AllDeclarations", "RecordDeclaration");

        Assert.Contains(graph.Edges, edge => IsEdge(edge, execute, alias, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, execute, local, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, execute, parameter, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, execute, implicitParameter, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, execute, label, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, execute, rangeVariable, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, typeParameter, constrainedType, GraphRelation.References));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, alias, Find(catalog, "ReferenceFixture.Production", "Service"), GraphRelation.References));
    }

    [Fact]
    public async Task Resolves_source_calls_across_project_compilations()
    {
        var root = RepositoryRoot();
#if NET11_0_OR_GREATER
        const string targetFramework = "net11.0";
#else
        const string targetFramework = "net10.0";
#endif
        using var loaded = await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "Graphify.CSharp.sln"),
            root,
            configuration: "Release",
            targetFramework: targetFramework));
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var main = FindProjectMember(catalog, "src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj", "Graphify.CSharp.Cli", "Program", SymbolKind.Method, "RunAsync", "string[]", "System.Threading.CancellationToken");
        var refresh = FindProjectMember(catalog, "src/Graphify.CSharp/Graphify.CSharp.csproj", "Graphify.CSharp.Incremental", "IncrementalRefreshEngine", SymbolKind.Method, "RefreshAsync", "Graphify.CSharp.Roslyn.ProjectLoadRequest", "string", "bool", "System.Threading.CancellationToken");
        var resultGraph = FindProjectMember(catalog, "src/Graphify.CSharp/Graphify.CSharp.csproj", "Graphify.CSharp.Incremental", "IncrementalRefreshResult", SymbolKind.Method, "get_Graph");

        Assert.Contains(graph.Edges, edge => IsEdge(edge, main, refresh, GraphRelation.Calls));
        Assert.Contains(graph.Edges, edge => IsEdge(edge, main, resultGraph, GraphRelation.Calls));
    }

    [Fact]
    public async Task Coarse_parallel_batches_match_serial_extraction()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var serial = await new SemanticReferenceExtractor(
                new ExtractionParallelismOptions(
                    maxDegreeOfParallelism: 1,
                    minimumDocumentsPerBatch: 2))
            .ExtractAsync(loaded, catalog);
        var parallel = await new SemanticReferenceExtractor(
                new ExtractionParallelismOptions(
                    maxDegreeOfParallelism: 2,
                    targetBatchesPerWorker: 2,
                    minimumDocumentsPerBatch: 2))
            .ExtractAsync(loaded, catalog);

        Assert.Equal(
            new GraphifyJsonSerializer().Serialize(serial),
            new GraphifyJsonSerializer().Serialize(parallel));
    }

    [Fact]
    public async Task A_shared_extractor_can_run_concurrently()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var extractor = new SemanticReferenceExtractor();

        var graphs = await Task.WhenAll(
            extractor.ExtractAsync(loaded, catalog),
            extractor.ExtractAsync(loaded, catalog));

        var serializer = new GraphifyJsonSerializer();
        Assert.Equal(serializer.Serialize(graphs[0]), serializer.Serialize(graphs[1]));
        Assert.False(extractor.Diagnostics.IsDefault);
    }

    [Fact]
    public async Task Solution_wide_batches_match_serial_extraction()
    {
        var root = RepositoryRoot();
#if NET11_0_OR_GREATER
        const string targetFramework = "net11.0";
#else
        const string targetFramework = "net10.0";
#endif
        using var loaded = await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "Graphify.CSharp.sln"),
            root,
            configuration: "Release",
            targetFramework: targetFramework));
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var serial = await new SemanticReferenceExtractor(
                new ExtractionParallelismOptions(
                    maxDegreeOfParallelism: 1,
                    minimumDocumentsPerBatch: 2))
            .ExtractAsync(loaded, catalog);
        var parallel = await new SemanticReferenceExtractor(
                new ExtractionParallelismOptions(
                    maxDegreeOfParallelism: 2,
                    targetBatchesPerWorker: 2,
                    minimumDocumentsPerBatch: 2))
            .ExtractAsync(loaded, catalog);

        Assert.Equal(
            new GraphifyJsonSerializer().Serialize(serial),
            new GraphifyJsonSerializer().Serialize(parallel));
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

    private static SymbolDeclaration FindScoped(DeclarationCatalog catalog, SymbolKind kind, string name) =>
        Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == kind
            && declaration.Identity.Name == name));

    private static bool IsEdge(GraphEdge edge, SymbolDeclaration source, SymbolDeclaration target, GraphRelation relation) =>
        edge.SourceId == source.Node.Id && edge.TargetId == target.Node.Id && edge.Relation == relation;

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
