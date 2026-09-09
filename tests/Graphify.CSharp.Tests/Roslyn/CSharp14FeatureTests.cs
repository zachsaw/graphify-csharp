using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis;
using System.Text.Json;
using SymbolKind = Graphify.CSharp.Domain.SymbolKind;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class CSharp14FeatureTests
{
    [Fact]
    public async Task Catalogs_CSharp14_declarations_without_diagnostics()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);

        Assert.Empty(catalog.Diagnostics);
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Property
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "FieldBackedPropertyHost")
            && declaration.Identity.Name == "Value");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "FieldBackedPropertyHost")
            && declaration.Identity.Name == "Assign");

        var partialConstructors = catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Constructor
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "PartialHost")).ToArray();
        Assert.Single(partialConstructors);
        Assert.Equal(2, partialConstructors[0].Node.SourceLocations.Length);

        var partialEvents = catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Event
            && declaration.Identity.ContainingTypes.Any(type => type.Name == "PartialHost")
            && declaration.Identity.Name == "Changed").ToArray();
        Assert.Single(partialEvents);
        Assert.Equal(2, partialEvents[0].Node.SourceLocations.Length);

        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.ContainingTypes.Any(type => type.Name == "Counter")
            && declaration.Identity.Name == "op_AdditionAssignment");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.ContainingTypes.Any(type => type.Name == "Counter")
            && declaration.Identity.Name == "op_IncrementAssignment");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Field
            && declaration.Identity.Name == "UnboundGenericName");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Field
            && declaration.Identity.Name == "OwnUnboundGenericName");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "ToSpan");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "Parse");
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Type
            && declaration.Identity.Name == "TryParse");

        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Property
            && declaration.Identity.Name == "IsEmpty"
            && declaration.Identity.ContainingMemberPath.Any(path => path.StartsWith("extension:", StringComparison.Ordinal)));
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "FirstValue"
            && declaration.Identity.ContainingMemberPath.Any(path => path.StartsWith("extension:", StringComparison.Ordinal)));
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Property
            && declaration.Identity.Name == "Identity"
            && declaration.Identity.ContainingMemberPath.Any(path => path.StartsWith("extension:", StringComparison.Ordinal)));
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "Combine"
            && declaration.Identity.ContainingMemberPath.Any(path => path.StartsWith("extension:", StringComparison.Ordinal)));
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Method
            && declaration.Identity.Name == "op_Addition"
            && declaration.Identity.ContainingMemberPath.Any(path => path.StartsWith("extension:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Extracts_CSharp14_semantic_references_without_diagnostics()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        Assert.Empty(catalog.Diagnostics);

        var assign = Find(catalog, SymbolKind.Method, "FieldBackedPropertyHost", "Assign");
        var value = Find(catalog, SymbolKind.Property, "FieldBackedPropertyHost", "Value");
        Assert.Contains(graph.Edges, edge => edge.SourceId == assign.Node.Id
            && edge.TargetId == value.Node.Id
            && edge.Relation == GraphRelation.References);

        var parse = Find(catalog, SymbolKind.Method, "ModernFeatures", "Parse");
        var tryParse = Find(catalog, SymbolKind.Type, null, "TryParse");
        Assert.Contains(graph.Edges, edge => edge.SourceId == parse.Node.Id
            && edge.TargetId == tryParse.Node.Id
            && edge.Relation == GraphRelation.References);

        var firstValue = Find(catalog, SymbolKind.Method, "GenericExtensions", "FirstValue");
        var receiver = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Parameter
            && declaration.Identity.Name == "values"
            && declaration.Identity.ContainingMemberPath.Any(path => path.StartsWith("extension:", StringComparison.Ordinal))));
        Assert.Contains(graph.Edges, edge => edge.SourceId == firstValue.Node.Id
            && edge.TargetId == receiver.Node.Id
            && edge.Relation == GraphRelation.References);

        var ownGenericName = Find(catalog, SymbolKind.Field, null, "OwnUnboundGenericName");
        var genericNameTarget = Find(catalog, SymbolKind.Type, null, "GenericNameTarget");
        Assert.Contains(graph.Edges, edge => edge.SourceId == ownGenericName.Node.Id
            && edge.TargetId == genericNameTarget.Node.Id
            && edge.Relation == GraphRelation.References);

        var extensionCaller = Find(catalog, SymbolKind.Method, "ModernFeatures", "UseExtensions");
        var extensionMethod = Find(catalog, SymbolKind.Method, "GenericExtensions", "FirstValue");
        Assert.Contains(graph.Edges, edge => edge.SourceId == extensionCaller.Node.Id
            && edge.TargetId == extensionMethod.Node.Id
            && edge.Relation == GraphRelation.Calls);

        var counterUse = Find(catalog, SymbolKind.Method, "Counter", "Use");
        var compoundAssignment = Find(catalog, SymbolKind.Method, "Counter", "op_AdditionAssignment");
        var increment = Find(catalog, SymbolKind.Method, "Counter", "op_IncrementAssignment");
        Assert.Contains(graph.Edges, edge => edge.SourceId == counterUse.Node.Id
            && edge.TargetId == compoundAssignment.Node.Id
            && edge.Relation == GraphRelation.Calls);
        Assert.Contains(graph.Edges, edge => edge.SourceId == counterUse.Node.Id
            && edge.TargetId == increment.Node.Id
            && edge.Relation == GraphRelation.Calls);
    }

    [Fact]
    public async Task Reports_identity_failures_and_keeps_serializing_the_remaining_graph()
    {
        using var loaded = await LoadFixtureAsync();
        var fallbackFactory = new RoslynSymbolIdentityFactory();
        var catalog = await DeclarationCatalogBuilder.ForTesting((symbol, project, repositoryRoot) =>
        {
            if (symbol is IPropertySymbol property
                && property.Name == "Value"
                && property.ContainingType?.Name == "FieldBackedPropertyHost")
            {
                throw new ArgumentException("synthetic test failure", nameof(symbol));
            }

            return fallbackFactory.Create(symbol, project, repositoryRoot);
        }).BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);
        var json = new GraphifyJsonSerializer().Serialize(
            graph,
            new GraphifySerializationOptions(catalog.Diagnostics));

        Assert.Contains(catalog.Diagnostics, diagnostic =>
            diagnostic.Contains("Identity: skipped Property", StringComparison.Ordinal)
            && diagnostic.Contains("synthetic test failure", StringComparison.Ordinal));
        Assert.Contains(catalog.Declarations, declaration =>
            declaration.Identity.Kind == SymbolKind.Type
            && declaration.Identity.Name == "Counter");

        using var document = JsonDocument.Parse(json);
        Assert.Contains(
            document.RootElement.GetProperty("graphify_csharp").GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetString()?.Contains("synthetic test failure", StringComparison.Ordinal) == true);
    }

    private static SymbolDeclaration Find(
        DeclarationCatalog catalog,
        SymbolKind kind,
        string? containingType,
        string name) => Assert.Single(catalog.Declarations.Where(declaration =>
        declaration.Identity.Kind == kind
        && (containingType is null || declaration.Identity.ContainingTypes.Any(type => type.Name == containingType))
        && declaration.Identity.Name == name));

    private static async Task<LoadedSolution> LoadFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/CSharp14Fixture/CSharp14Fixture.csproj"),
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
