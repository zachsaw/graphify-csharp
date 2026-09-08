using System.Text.Json;
using global::Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class CliSmokeTests
{
    [Fact]
    public async Task Runs_headlessly_and_writes_graphify_json_for_a_fixture_project()
    {
        var root = RepositoryRoot();
        var outputDirectory = Path.Combine(Path.GetTempPath(), "graphify-csharp-smoke", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(outputDirectory, "graph.json");

        var exitCode = await Program.Main(
        [
            "--input", Path.Combine(root, "tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj"),
            "--root", root,
            "--output", outputPath,
            "--target-framework", "net10.0",
        ]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        Assert.NotEmpty(document.RootElement.GetProperty("nodes").EnumerateArray());
        Assert.NotEmpty(document.RootElement.GetProperty("edges").EnumerateArray());
        Assert.Equal("csharp/v1", document.RootElement.GetProperty("graphify_csharp").GetProperty("schema_version").GetString());
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
