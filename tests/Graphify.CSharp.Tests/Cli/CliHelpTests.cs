using Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class CliHelpTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Recognizes_only_standalone_top_level_help(string argument)
    {
        Assert.True(CliHelp.IsTopLevelHelp([argument]));
        Assert.False(CliHelp.IsTopLevelHelp([argument, "--json"]));
    }

    [Fact]
    public void Top_level_help_describes_the_intent_based_routes()
    {
        Assert.Contains("graphify-csharp watch [options]", CliHelp.Usage, StringComparison.Ordinal);
        Assert.Contains("graphify-csharp export [options]", CliHelp.Usage, StringComparison.Ordinal);
        Assert.Contains("--instance <id|prefix>", CliHelp.Usage, StringComparison.Ordinal);
        Assert.Contains("./graphify-out/csharp.json", CliHelp.Usage, StringComparison.Ordinal);
    }
}
