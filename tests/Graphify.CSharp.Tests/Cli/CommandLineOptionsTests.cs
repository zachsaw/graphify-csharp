using global::Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void Parses_input_root_output_and_repeatable_production_roots()
    {
        var options = CommandLineOptions.Parse(
        [
            "--input", "src/App/App.sln",
            "--root", "/repo",
            "--output", "out/graph.json",
            "--configuration", "Release",
            "--target-framework", "net10.0",
            "--test-namespace", "Fixtures",
            "--production-root", "cs_root_a",
            "--production-root", "cs_root_b",
        ]);

        Assert.Equal("/repo/src/App/App.sln", options.InputPath);
        Assert.Equal("/repo", options.RepositoryRoot);
        Assert.Equal("/repo/out/graph.json", options.OutputPath);
        Assert.Equal("Release", options.Configuration);
        Assert.Equal("net10.0", options.TargetFramework);
        Assert.Equal("Fixtures", options.TestNamespaceSegment);
        Assert.True(new[] { "cs_root_a", "cs_root_b" }.SequenceEqual(options.ProductionRootNodeIds));
    }

    [Fact]
    public void Supports_a_positional_input_and_help_without_an_input()
    {
        var positional = CommandLineOptions.Parse(["project.csproj"]);
        var help = CommandLineOptions.Parse(["--help"]);

        Assert.EndsWith("/project.csproj", positional.InputPath, StringComparison.Ordinal);
        Assert.False(positional.ShowHelp);
        Assert.True(help.ShowHelp);
    }

    [Fact]
    public void Rejects_unknown_options_and_invalid_namespace_patterns()
    {
        Assert.Throws<CommandLineException>(() => CommandLineOptions.Parse(["--unknown", "value"]));
        Assert.Throws<CommandLineException>(() => CommandLineOptions.Parse(["--input", "a.csproj", "--test-namespace", "Product.Tests"]));
    }
}
