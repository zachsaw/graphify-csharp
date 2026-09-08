using Graphify.CSharp.Audit;
using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Audit;

public sealed class UsageAuditAnalyzerTests
{
    [Fact]
    public async Task Classifies_direct_callers_and_exposes_the_caller_list()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);
        var report = new UsageAuditAnalyzer(catalog).Analyze(graph);

        var mixed = ResultFor(report, catalog, "ReferenceFixture.Production", "Service", "Called", "int");
        var testOnly = ResultFor(report, catalog, "ReferenceFixture.Production", "Service", "Called", "string");
        var production = ResultFor(report, catalog, "ReferenceFixture.Production", "Service", "MethodGroup", "int");

        Assert.Equal(UsageClassification.Mixed, mixed.Classification);
        Assert.Equal(UsageClassification.TestOnly, testOnly.Classification);
        Assert.Equal(UsageClassification.ProductionUsed, production.Classification);
        Assert.Contains(mixed.Callers, caller => caller.CallerNamespace == "ReferenceFixture.Production");
        Assert.Contains(mixed.Callers, caller => caller.CallerNamespace == "ReferenceFixture.Tests");
        Assert.All(mixed.Callers, caller => Assert.Contains(GraphRelation.Calls, caller.Relations));
    }

    [Fact]
    public async Task Reports_zero_references_as_a_static_observation_with_a_dynamic_warning()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);
        var report = new UsageAuditAnalyzer(catalog).Analyze(graph);
        var unused = ResultFor(report, catalog, "ReferenceFixture.Production", "Service", "Unused");

        Assert.Equal(UsageClassification.ZeroReferences, unused.Classification);
        Assert.Empty(unused.Callers);
        Assert.Contains(AuditWarningKind.PotentialDynamicReference, unused.Warnings);
        Assert.True(unused.IsStaticObservationOnly);
    }

    [Fact]
    public async Task Configured_production_root_overrides_zero_reference_classification()
    {
        using var loaded = await LoadFixtureAsync();
        var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
        var graph = await new SemanticReferenceExtractor().ExtractAsync(loaded, catalog);
        var unused = Find(catalog, "ReferenceFixture.Production", "Service", "Unused");
        var report = new UsageAuditAnalyzer(catalog).Analyze(graph, new AuditOptions(productionRootNodeIds: [unused.Node.Id]));

        var result = Assert.Single(report.Results, item => item.Target.Node.Id == unused.Node.Id);
        Assert.Equal(UsageClassification.ProductionUsed, result.Classification);
        Assert.True(result.IsConfiguredProductionRoot);
        Assert.DoesNotContain(AuditWarningKind.PotentialDynamicReference, result.Warnings);
    }

    private static UsageAuditResult ResultFor(UsageAuditReport report, DeclarationCatalog catalog, string namespaceName, string typeName, string memberName, params string[] parameterTypes)
    {
        var declaration = Find(catalog, namespaceName, typeName, memberName, parameterTypes);
        return Assert.Single(report.Results, result => result.Target.Node.Id == declaration.Node.Id);
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

    private static async Task<LoadedSolution> LoadFixtureAsync()
    {
        var root = RepositoryRoot();
        return await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
            Path.Combine(root, "tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj"),
            root,
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
