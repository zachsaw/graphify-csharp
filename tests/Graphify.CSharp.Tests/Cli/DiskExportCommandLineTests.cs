using System.Text.Json;
using Graphify.CSharp.Cli;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Cli;

[Collection("CLI console")]
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
    public void Disk_export_uses_the_caller_default_output()
    {
        var options = DiskExportCommandLine.Parse(
            ["export", "--input", "Product.sln"],
            "/caller");

        Assert.Equal("/caller/graphify-out/csharp.json", options.OutputPath);
    }

    [Fact]
    public void Disk_export_rejects_an_explicit_empty_output()
    {
        Assert.Throws<CommandLineException>(() => DiskExportCommandLine.Parse(
            ["export", "--input", "Product.sln", "--output", ""],
            "/caller"));
    }

    [Fact]
    public void The_removed_watch_flag_has_an_actionable_migration_error()
    {
        var exception = Assert.Throws<CommandLineException>(() => DiskExportCommandLine.Parse(
            ["--input", "Product.sln", "--output", "out.json", "--watch"]));

        Assert.Contains("use 'watch'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_solution_parser_failures_as_structured_errors()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-cli-solution-load-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var solutionPath = Path.Combine(root, "Duplicate.slnx");
        var outputPath = Path.Combine(root, "output.json");
        await File.WriteAllTextAsync(
            solutionPath,
            """
            <Solution>
              <Project Path="Missing.csproj" />
              <Project Path="Missing.csproj" />
            </Solution>
            """);

        var originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await DiskExportCommandLine.RunAsync(
            [
                "export",
                "--input", solutionPath,
                "--root", root,
                "--output", outputPath,
                "--target-framework", "net10.0",
                "--no-progress",
                "--json",
            ]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(
                SolutionLoadException.ErrorCode,
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(
                "Duplicate item 'Missing.csproj'",
                document.RootElement.GetProperty("error").GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Console.SetOut(originalOutput);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
