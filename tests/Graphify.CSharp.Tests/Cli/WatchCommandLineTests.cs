using Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class WatchCommandLineTests
{
    [Fact]
    public void Parses_an_output_free_watch_session()
    {
        var options = WatchCommandLine.Parse(
        [
            "watch",
            "--input", "src/Product.sln",
            "--root", "/repo",
            "--configuration", "Release",
            "--target-framework", "net10.0",
            "--watch-scan-interval", "00:00:02",
            "--no-progress",
        ],
        "/caller");

        Assert.Equal("/caller/src/Product.sln", options.Request.InputPath);
        Assert.Equal("/repo", options.Request.RepositoryRoot);
        Assert.Equal("Release", options.Request.Configuration);
        Assert.Equal("net10.0", options.Request.TargetFramework);
        Assert.Equal(TimeSpan.FromSeconds(2), options.WatchScanInterval);
        Assert.True(options.NoProgress);
    }

    [Fact]
    public void Watch_rejects_output_json_and_rebuild_options()
    {
        Assert.Throws<CommandLineException>(() => WatchCommandLine.Parse(
            ["watch", "--input", "Product.sln", "--output", "out.json"]));
        Assert.Throws<CommandLineException>(() => WatchCommandLine.Parse(
            ["watch", "--input", "Product.sln", "--json"]));
        Assert.Throws<CommandLineException>(() => WatchCommandLine.Parse(
            ["watch", "--input", "Product.sln", "--rebuild"]));
    }

    [Fact]
    public void Watch_help_does_not_need_an_input()
    {
        var options = WatchCommandLine.Parse(["watch", "--help"]);

        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void Watch_omits_input_only_when_root_discovery_finds_one_candidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-watch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Product.sln"), string.Empty);

            var options = WatchCommandLine.Parse(["watch", "--root", root], "/caller");

            Assert.Equal(Path.Combine(root, "Product.sln"), options.Request.InputPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
