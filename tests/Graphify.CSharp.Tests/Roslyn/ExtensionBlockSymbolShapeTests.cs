using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class ExtensionBlockSymbolShapeTests
{
    [Fact]
    public async Task Catalogs_extension_block_receivers_and_members()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);

        Assert.Empty(catalog.Diagnostics);

        var extensionMember = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Namespace == "ExtensionBlockFixture"
            && declaration.Identity.Kind == SymbolKind.Property
            && declaration.Identity.Name == "IsValid"));
        var receiver = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Namespace == "ExtensionBlockFixture"
            && declaration.Identity.Kind == SymbolKind.Parameter
            && declaration.Identity.Name == "timeline"));

        Assert.Equal(["DateOnlyExtensions"], extensionMember.Identity.ContainingTypes.Select(type => type.Name).ToArray());
        Assert.Equal(
            extensionMember.Identity.ContainingMemberPath.ToArray(),
            receiver.Identity.ContainingMemberPath.ToArray());
        Assert.Single(extensionMember.Identity.ContainingMemberPath);
        Assert.StartsWith("extension:", extensionMember.Identity.ContainingMemberPath[0], StringComparison.Ordinal);
        Assert.Contains(
            receiver.Node.SourceLocations,
            location => location.FilePath == "tests/Fixtures/ExtensionBlockFixture/DateOnlyExtensions.cs");
    }

    [Fact]
    public async Task Extracts_references_from_extension_block_members_to_the_receiver()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);

        var extensionMember = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Property
            && declaration.Identity.Name == "IsValid"));
        var receiver = Assert.Single(catalog.Declarations.Where(declaration =>
            declaration.Identity.Kind == SymbolKind.Parameter
            && declaration.Identity.Name == "timeline"));

        Assert.Contains(graph.Edges, edge =>
            edge.SourceId == extensionMember.Node.Id
            && edge.TargetId == receiver.Node.Id
            && edge.Relation == GraphRelation.References);
    }

    [Fact]
    public async Task Resolves_extension_member_bodies_to_the_declared_member()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var project = Assert.Single(loaded.Projects);
        var document = Assert.Single(project.Project.Documents, item =>
            item.FilePath?.EndsWith("DateOnlyExtensions.cs", StringComparison.Ordinal) == true);
        var syntaxRoot = await document.GetSyntaxRootAsync();
        Assert.NotNull(syntaxRoot);
        var timelineAccesses = syntaxRoot!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(node =>
            node.Identifier.ValueText == "timeline"
            && node.Parent is MemberAccessExpressionSyntax).ToArray();
        Assert.Equal(2, timelineAccesses.Length);
        var semanticModel = project.Compilation.GetSemanticModel(syntaxRoot.SyntaxTree);

        foreach (var timelineAccess in timelineAccesses)
        {
            var caller = new SemanticCallerResolver(catalog).Resolve(semanticModel, timelineAccess.SpanStart);

            Assert.NotNull(caller);
            Assert.Equal("IsValid", caller!.Identity.Name);
        }
    }

    [Fact]
    public async Task Produces_stable_extension_block_identities_across_builds()
    {
        using var firstLoaded = await LoadFixtureAsync();
        var firstCatalog = await new DeclarationCatalogBuilder().BuildAsync(firstLoaded);
        var firstGraph = await new SemanticReferenceExtractor().ExtractAsync(firstLoaded, firstCatalog);

        using var secondLoaded = await LoadFixtureAsync();
        var secondCatalog = await new DeclarationCatalogBuilder().BuildAsync(secondLoaded);
        var secondGraph = await new SemanticReferenceExtractor().ExtractAsync(secondLoaded, secondCatalog);

        var firstJson = new GraphifyJsonSerializer().Serialize(
            firstGraph,
            new GraphifySerializationOptions(firstCatalog.Diagnostics));
        var secondJson = new GraphifyJsonSerializer().Serialize(
            secondGraph,
            new GraphifySerializationOptions(secondCatalog.Diagnostics));

        Assert.Equal(firstJson, secondJson);
    }

    private static async Task<LoadedSolution> LoadFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/ExtensionBlockFixture/ExtensionBlockFixture.csproj"),
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
