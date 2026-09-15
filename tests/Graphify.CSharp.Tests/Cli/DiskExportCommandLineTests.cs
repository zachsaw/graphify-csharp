using Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class DiskExportCommandLineTests
{
    [Fact]
    public void Parses_the_explicit_export_route()
    {
        var options = DiskExportCommandLine.Parse(
        [
            "export",
            "--input", "src/Product.sln",
            "--root", "/repo",
            "--output", "out/csharp.json",
            "--configuration", "Release",
            "--target-framework", "net10.0",
            "--rebuild",
            "--json",
            "--no-progress",
        ],
        "/caller");

        Assert.Equal("/caller/src/Product.sln", options.Request.InputPath);
        Assert.Equal("/repo", options.Request.RepositoryRoot);
        Assert.Equal("/caller/out/csharp.json", options.OutputPath);
        Assert.Equal("Release", options.Request.Configuration);
        Assert.Equal("net10.0", options.Request.TargetFramework);
        Assert.True(options.Rebuild);
        Assert.True(options.Json);
        Assert.True(options.NoProgress);
    }

    [Fact]
    public void Bare_positional_input_is_the_disk_export_alias()
    {
        var options = DiskExportCommandLine.Parse(["Product.sln", "--output", "out.json"], "/caller");

        Assert.Equal("/caller/Product.sln", options.Request.InputPath);
        Assert.Equal("/caller/out.json", options.OutputPath);
    }

    [Fact]
    public void Disk_export_requires_an_explicit_input_and_output_until_defaults_are_enabled()
    {
        Assert.Throws<CommandLineException>(() => DiskExportCommandLine.Parse([]));
        Assert.Throws<CommandLineException>(() => DiskExportCommandLine.Parse(
            ["export", "--input", "Product.sln"]));
    }

    [Fact]
    public void The_removed_watch_flag_has_an_actionable_migration_error()
    {
        var exception = Assert.Throws<CommandLineException>(() => DiskExportCommandLine.Parse(
            ["--input", "Product.sln", "--output", "out.json", "--watch"]));

        Assert.Contains("use 'watch'", exception.Message, StringComparison.Ordinal);
    }
}
