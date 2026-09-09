using global::Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void Parses_input_root_output_and_optional_target_framework()
    {
        var options = CommandLineOptions.Parse(
        [
            "--input", "src/App/App.sln",
            "--root", "/repo",
            "--output", "out/graph.json",
            "--configuration", "Release",
            "--target-framework", "net10.0",
        ]);

        Assert.Equal("/repo/src/App/App.sln", options.InputPath);
        Assert.Equal("/repo", options.RepositoryRoot);
        Assert.Equal("/repo/out/graph.json", options.OutputPath);
        Assert.Equal("Release", options.Configuration);
        Assert.Equal("net10.0", options.TargetFramework);
        Assert.False(options.Rebuild);
    }

    [Fact]
    public void Supports_a_positional_input_and_help_without_an_input()
    {
        var positional = CommandLineOptions.Parse(["project.csproj"]);
        var help = CommandLineOptions.Parse(["--help"]);

        Assert.EndsWith("/project.csproj", positional.InputPath, StringComparison.Ordinal);
        Assert.False(positional.ShowHelp);
        Assert.Null(positional.TargetFramework);
        Assert.False(positional.Rebuild);
        Assert.True(help.ShowHelp);
    }

    [Fact]
    public void Parses_the_explicit_rebuild_switch()
    {
        var options = CommandLineOptions.Parse(["--input", "project.csproj", "--rebuild"]);

        Assert.True(options.Rebuild);
        Assert.Throws<CommandLineException>(() => CommandLineOptions.Parse(["--input", "project.csproj", "--rebuild", "--rebuild"]));
    }

    [Fact]
    public void Accepts_file_based_app_input()
    {
        var options = CommandLineOptions.Parse(["app.cs"]);

        Assert.EndsWith("/app.cs", options.InputPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unknown_options_and_analysis_policy_options()
    {
        Assert.Throws<CommandLineException>(() => CommandLineOptions.Parse(["--unknown", "value"]));
        Assert.Throws<CommandLineException>(() => CommandLineOptions.Parse(["--input", "a.csproj", "--test-namespace", "Tests"]));
        Assert.Throws<CommandLineException>(() => CommandLineOptions.Parse(["--input", "a.csproj", "--production-root", "cs_root"]));
    }
}
