using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using SymbolKind = Graphify.CSharp.Domain.SymbolKind;

namespace Graphify.CSharp.Tests.Roslyn;

public sealed class GeneratedSourceTests
{
    [Fact]
    public async Task Extracts_compilation_trees_outside_project_documents_with_mapped_locations_deterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-generated-source-tests");
        var mappedPath = Path.Combine(root, "Counter.razor").Replace('\\', '/');
        string? previous = null;
        for (var run = 0; run < 2; run++)
        {
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
                namespace GeneratedFixture;
                public class Counter
                {
                #line 10 "{{mappedPath}}"
                    public int Load()
                    {
                        var count = 1;
                        return Service.Get(count);
                    }
                #line default
                }
                """, path: Path.Combine(root, "Counter.g.cs"));
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
            Assert.Contains(catalog.Declarations, declaration => declaration.Identity.Kind == SymbolKind.Local
                && declaration.Identity.Name == "count");

            var serialized = new GraphifyJsonSerializer().Serialize(graph);
            if (previous is not null)
            {
                Assert.Equal(previous, serialized);
            }
            previous = serialized;
        }
    }
}
