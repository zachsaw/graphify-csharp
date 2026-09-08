using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class WorkspaceLoaderTests
{
    [Fact]
    public async Task Loads_a_csharp_project_and_compilation()
    {
        var root = RepositoryRoot();
        var request = new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/LoaderFixture/LoaderFixture.csproj"),
            root,
            targetFramework: "net10.0");

        using var loaded = await new RoslynWorkspaceLoader().LoadAsync(request);

        var project = Assert.Single(loaded.Projects);
        Assert.Equal("tests/Fixtures/LoaderFixture/LoaderFixture.csproj", project.Identity.RelativePath);
        Assert.Equal("net10.0", project.Identity.TargetFramework);
        Assert.Contains(project.Compilation.SyntaxTrees, tree => tree.FilePath.EndsWith("FixtureTypes.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalogs_source_types_and_members_with_stable_keys()
    {
        var root = RepositoryRoot();
        var request = new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/LoaderFixture/LoaderFixture.csproj"),
            root,
            targetFramework: "net10.0");

        using var loaded = await new RoslynWorkspaceLoader().LoadAsync(request);
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);

        var methods = catalog.Declarations
            .Where(declaration => declaration.Identity.Kind == SymbolKind.Method)
            .Where(declaration => declaration.Identity.Name is "Run" or "Echo")
            .ToArray();

        Assert.Equal(3, methods.Length);
        Assert.All(methods, declaration => Assert.StartsWith("cs_", declaration.Node.Id, StringComparison.Ordinal));
        Assert.All(methods, declaration => Assert.NotEmpty(declaration.Node.SourceLocations));
        Assert.Contains(
            methods.SelectMany(method => method.Node.SourceLocations),
            location => location.FilePath == "tests/Fixtures/LoaderFixture/FixtureTypes.cs");

        var runInt = Assert.Single(methods, method => method.Identity.Parameters.Single().TypeName == "int");
        Assert.True(catalog.TryGet(runInt.Symbol, out var bySymbol));
        Assert.Equal(runInt.Identity.CanonicalKey, bySymbol.Identity.CanonicalKey);
    }

    [Fact]
    public async Task Rejects_unsupported_input_extensions_before_workspace_creation()
    {
        var request = new ProjectLoadRequest(Path.Combine(RepositoryRoot(), "README.md"), RepositoryRoot());

        await Assert.ThrowsAsync<ArgumentException>(() => new RoslynWorkspaceLoader().LoadAsync(request));
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
