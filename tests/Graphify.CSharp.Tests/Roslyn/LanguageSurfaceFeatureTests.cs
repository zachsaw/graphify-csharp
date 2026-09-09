using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class LanguageSurfaceFeatureTests
{
    [Fact]
    public async Task Extracts_compiler_selected_language_surface_members()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        Assert.Empty(catalog.Diagnostics);

        AssertCalls(graph, catalog, "Operators", "PatternValue", "op_Addition");
        AssertCalls(graph, catalog, "Operators", "PatternValue", "op_Increment");
        AssertCalls(graph, catalog, "Operators", "PatternValue", "op_LogicalNot");
        AssertCalls(graph, catalog, "Operators", "PatternValue", "op_Implicit");
        AssertCalls(graph, catalog, "Operators", "PatternValue", "op_Explicit");
        AssertCalls(graph, catalog, "Operators", "PatternValue", "op_Equality");

        AssertCalls(graph, catalog, "Deconstruction", "PatternValue", "Deconstruct");
        AssertCalls(graph, catalog, "Patterns", "PatternValue", "Deconstruct");
        AssertReferences(graph, catalog, "Patterns", "PatternValue", "Length");
        AssertReferences(graph, catalog, "Patterns", "PatternValue", "this[]", "System.Index");

        AssertCalls(graph, catalog, "Enumeration", "SurfaceEnumerable", "GetEnumerator");
        AssertCalls(graph, catalog, "Enumeration", "SurfaceEnumerator", "MoveNext");
        AssertReferences(graph, catalog, "Enumeration", "SurfaceEnumerator", "Current");
        AssertCalls(graph, catalog, "Enumeration", "SurfaceEnumerator", "Dispose");
        AssertCalls(graph, catalog, "AsyncEnumeration", "AsyncSurfaceEnumerable", "GetAsyncEnumerator");
        AssertCalls(graph, catalog, "AsyncEnumeration", "AsyncSurfaceEnumerator", "MoveNextAsync");
        AssertReferences(graph, catalog, "AsyncEnumeration", "AsyncSurfaceEnumerator", "Current");
        AssertCalls(graph, catalog, "AsyncEnumeration", "AsyncSurfaceEnumerator", "DisposeAsync");
        AssertCalls(graph, catalog, "AsyncEnumeration", "AsyncMoveNextAwaitable", "GetAwaiter");
        AssertReferences(graph, catalog, "AsyncEnumeration", "AsyncMoveNextAwaiter", "IsCompleted");
        AssertCalls(graph, catalog, "AsyncEnumeration", "AsyncMoveNextAwaiter", "OnCompleted");
        AssertCalls(graph, catalog, "AsyncEnumeration", "AsyncMoveNextAwaiter", "GetResult");

        AssertCalls(graph, catalog, "Initializers", "SurfaceCollection", "Add");
        AssertReferences(graph, catalog, "Ranges", "PatternValue", "this[]", "System.Index");
        AssertReferences(graph, catalog, "Ranges", "PatternValue", "this[]", "System.Range");
        AssertCalls(graph, catalog, "Accessors", "PatternValue", "get_Mutable");
        AssertCalls(graph, catalog, "Accessors", "PatternValue", "set_Mutable");
        AssertCalls(graph, catalog, "Accessors", "PatternValue", "add_Changed");
        AssertCalls(graph, catalog, "Accessors", "PatternValue", "remove_Changed");
        AssertCalls(graph, catalog, "Using", "SurfaceResource", "Dispose");
        AssertCalls(graph, catalog, "UsingDeclaration", "SurfaceResource", "Dispose");

        AssertCalls(graph, catalog, "Awaiting", "Awaitable", "GetAwaiter");
        AssertReferences(graph, catalog, "Awaiting", "SurfaceAwaiter", "IsCompleted");
        AssertCalls(graph, catalog, "Awaiting", "SurfaceAwaiter", "OnCompleted");
        AssertCalls(graph, catalog, "Awaiting", "SurfaceAwaiter", "GetResult");
        AssertCalls(graph, catalog, "AwaitUsing", "AsyncResource", "DisposeAsync");
        AssertCalls(graph, catalog, "AwaitUsingDeclaration", "AsyncResource", "DisposeAsync");
        AssertCalls(graph, catalog, "FixedPattern", "PinnableValue", "GetPinnableReference");
        AssertReferences(graph, catalog, "FunctionPointer", "SurfaceConsumer", "PointerTarget", "int");
        AssertReferencesToParameter(graph, catalog, "SurfaceHandlerConsumer", "Use", "SurfaceHandlerConsumer", "Consume", "handler");

        AssertCallsFrom(graph, catalog, "SurfaceHandlerConsumer", "Use", "SurfaceHandler", ".ctor", "int", "int");
        AssertCallsFrom(graph, catalog, "SurfaceHandlerConsumer", "Use", "SurfaceHandler", "AppendLiteral", "string");
        AssertCallsFrom(graph, catalog, "SurfaceHandlerConsumer", "Use", "SurfaceHandler", "AppendFormatted", "int");
    }

    private static void AssertCalls(
        GraphSnapshot graph,
        DeclarationCatalog catalog,
        string callerName,
        string targetType,
        string targetName,
        params string[] targetParameters)
    {
        AssertEdge(graph, FindMethod(catalog, "SurfaceConsumer", callerName), FindMember(catalog, targetType, targetName, targetParameters), GraphRelation.Calls);
    }

    private static void AssertCallsFrom(
        GraphSnapshot graph,
        DeclarationCatalog catalog,
        string callerType,
        string callerName,
        string targetType,
        string targetName,
        params string[] targetParameters)
    {
        var caller = FindMethod(catalog, callerType, callerName);
        var target = targetName == ".ctor"
            ? Assert.Single(catalog.Declarations.Where(declaration =>
                declaration.Identity.Kind == SymbolKind.Constructor
                && declaration.Identity.ContainingTypes.Any(type => type.Name == targetType)
                && declaration.Identity.Parameters.Select(parameter => parameter.TypeName).SequenceEqual(targetParameters)))
            : FindMember(catalog, targetType, targetName, targetParameters);
        AssertEdge(graph, caller, target, GraphRelation.Calls);
    }

    private static void AssertReferences(
        GraphSnapshot graph,
        DeclarationCatalog catalog,
        string callerName,
        string targetType,
        string targetName,
        params string[] targetParameters)
    {
        AssertEdge(graph, FindMethod(catalog, "SurfaceConsumer", callerName), FindMember(catalog, targetType, targetName, targetParameters), GraphRelation.References);
    }

    private static void AssertReferencesToParameter(
        GraphSnapshot graph,
        DeclarationCatalog catalog,
        string callerType,
        string callerName,
        string targetType,
        string targetName,
        string targetParameterName)
    {
        var caller = FindMethod(catalog, callerType, callerName);
        var targetMethod = FindMethod(catalog, targetType, targetName);
        var targetParameter = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Parameter
            && declaration.Identity.Name == targetParameterName
            && Microsoft.CodeAnalysis.SymbolEqualityComparer.Default.Equals(declaration.Symbol.ContainingSymbol, targetMethod.Symbol)));
        AssertEdge(graph, caller, targetParameter, GraphRelation.References);
    }

    private static void AssertEdge(
        GraphSnapshot graph,
        SymbolDeclaration source,
        SymbolDeclaration target,
        GraphRelation relation) => Assert.Contains(graph.Edges, edge =>
        edge.SourceId == source.Node.Id
        && edge.TargetId == target.Node.Id
        && edge.Relation == relation);

    private static SymbolDeclaration FindMethod(DeclarationCatalog catalog, string containingType, string name) =>
        Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.ContainingTypes.Any(type => type.Name == containingType)
            && declaration.Identity.Name == name));

    private static SymbolDeclaration FindMember(
        DeclarationCatalog catalog,
        string containingType,
        string name,
        IReadOnlyList<string> parameters)
    {
        var candidates = catalog.Declarations.Where(declaration =>
            declaration.Identity.ContainingTypes.Any(type => type.Name == containingType)
            && declaration.Identity.Name == name);
        if (parameters.Count > 0)
        {
            candidates = candidates.Where(declaration => declaration.Identity.Parameters
                .Select(parameter => parameter.TypeName)
                .SequenceEqual(parameters));
        }

        return Assert.Single(candidates);
    }

    private static async Task<LoadedSolution> LoadFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/LanguageSurfaceFixture/LanguageSurfaceFixture.csproj"),
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
