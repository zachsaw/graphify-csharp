using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using SymbolKind = Graphify.CSharp.Domain.SymbolKind;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class GeneratedSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extracts_compilation_trees_outside_project_documents_with_mapped_locations_deterministically(bool relativeMapping)
    {
        string? previous = null;
        for (var run = 0; run < 2; run++)
        {
            var root = Path.Combine(Path.GetTempPath(), "graphify-generated-source-tests", run == 0 ? "short" : "a-longer-checkout-directory");
            var mappedPath = relativeMapping ? "../Counter.razor" : Path.Combine(root, "Counter.razor").Replace('\\', '/');
            using var workspace = MSBuildWorkspace.Create();
            var projectId = ProjectId.CreateNewId();
            var project = workspace.CurrentSolution
                .AddProject(projectId, "GeneratedFixture", "GeneratedFixture", LanguageNames.CSharp)
                .WithProjectFilePath(projectId, Path.Combine(root, "GeneratedFixture.csproj"))
                .GetProject(projectId)!
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument("Service.cs", SourceText.From("""
                    namespace GeneratedFixture;
                    public static class Service
                    {
                        public static int Get(int count) => count;
                        public static int Get(string text) => text.Length;
                    }
                    """), filePath: Path.Combine(root, "Service.cs"))
                .Project;
            var compilation = (await project.GetCompilationAsync())!;
            var generatedTree = CSharpSyntaxTree.ParseText($$"""
                // Generated from {{root}}
                namespace GeneratedFixture;
                public class Counter
                {
                    public int Render(int builder) => builder;
                #line 10 "{{mappedPath}}"
                    public int Load()
                    {
                        var count = 1;
                        return Service.Get(count);
                    }
                #line 10 "{{mappedPath}}"
                    public void Other()
                    {
                        { var count = 2; Service.Get(count); }
                        { var count = 3; Service.Get(count); }
                    }
                #line default
                }
                """, path: Path.Combine(root, "generated", "Counter.g.cs"));
            compilation = compilation.AddSyntaxTrees(generatedTree);
            Assert.DoesNotContain(project.Documents, document => document.FilePath == generatedTree.FilePath);
            Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            using var loaded = new LoadedSolution(workspace,
                [new AnalyzedProject(project, new ProjectIdentity("GeneratedFixture.csproj", "net10.0"), compilation)], root);
            var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
            var extractor = new SemanticReferenceExtractor();
            var graph = await extractor.ExtractAsync(loaded, catalog);
            Assert.Empty(catalog.Diagnostics);
            Assert.Empty(extractor.Diagnostics);

            var caller = Assert.Single(graph.Nodes.Where(node => node.Label == "GeneratedFixture.Counter.Load"));
            var target = Assert.Single(graph.Nodes.Where(node => node.Label == "GeneratedFixture.Service.Get(int)"));
            var otherOverload = Assert.Single(graph.Nodes.Where(node => node.Label == "GeneratedFixture.Service.Get(string)"));
            Assert.Contains(caller.SourceLocations, location => location.FilePath == "Counter.razor" && location.Line == 10);
            var call = Assert.Single(graph.Edges.Where(edge => edge.SourceId == caller.Id
                && edge.TargetId == target.Id && edge.Relation == GraphRelation.Calls));
            Assert.Contains(call.SourceLocations, location => location.FilePath == "Counter.razor" && location.Line == 13);
            Assert.DoesNotContain(graph.Edges, edge => edge.SourceId == caller.Id && edge.TargetId == otherOverload.Id);
            Assert.Equal(3, catalog.Declarations.Count(declaration => declaration.Identity.Kind == SymbolKind.Local
                && declaration.Identity.Name == "count"));
            Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Parameter
                && declaration.Identity.Name == "builder");

            var projectIdentity = loaded.Projects.Single().Identity;
            var index = SemanticEvidenceIndex.Create(catalog,
                [new ProjectContributionEnvelope(new ProjectFingerprint(projectIdentity, null, [], []), graph)]);
            var sessionId = Guid.NewGuid();
            var view = new SemanticEvidenceView(sessionId, "instance", "generated-source-test", loaded, catalog, index,
                WatcherInputSnapshot.CreateForTests([], [], [], [root], outputPath: null, cachePath: null),
                new RefreshGeneration(sessionId), 0, 1, sessionId.ToByteArray());
            var arguments = new SemanticQueryEngine().Execute(new SemanticQuerySpec(
                "arguments", SymbolId: target.Id,
                Filters: new SemanticQueryFilters(Path: Path.Combine(root, "Counter.razor"))), view);
            Assert.True(arguments.Success, arguments.Error?.Message);
            Assert.Empty(arguments.Diagnostics);
            Assert.Equal(3, arguments.Items.Count);
            Assert.All(arguments.Items, argument => Assert.Equal("count", argument.GetProperty("expression_preview").GetString()));

            var serialized = new GraphifyJsonSerializer().Serialize(graph);
            if (previous is not null)
            {
                Assert.Equal(previous, serialized);
            }
            previous = serialized;
        }
    }

    [Fact]
    public void Relative_mappings_are_resolved_per_source_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-mapped-source-tests");
        var locations = new SourceLocationFactory(root);
        foreach (var directory in new[] { "first", "second" })
        {
            var tree = CSharpSyntaxTree.ParseText("""
                #line 100 "Original.cs"
                class Mapped { }
                #line default
                class Unmapped { }
                """, path: Path.Combine(root, directory, "Generated.cs"));
            var declarations = tree.GetRoot().ChildNodes().ToArray();
            Assert.Equal(new SourceLocation($"{directory}/Original.cs", 100, 1), locations.Create(declarations[0].GetLocation()));
            Assert.Equal(new SourceLocation($"{directory}/Generated.cs", 4, 1), locations.Create(declarations[1].GetLocation()));
        }
    }
}
