using Graphify.CSharp.Cli;
using System.Text.Json;

namespace Graphify.CSharp.Tests.Cli;

public sealed class SemanticQueryCommandLineTests
{
    [Fact]
    public void Parses_cold_and_batch_query_shapes()
    {
        var symbols = SemanticQueryCommandLine.Parse(
        [
            "query",
            "symbols",
            "OrderService",
            "--input", "Product.sln",
            "--root", "/repo",
            "--configuration", "Release",
            "--kind", "class",
            "--limit", "2",
            "--json",
        ]);
        var summary = SemanticQueryCommandLine.Parse(
        [
            "query",
            "usage-summary",
            "Order",
            "--instance", "abc",
            "--group-by", "project,namespace",
        ]);

        Assert.NotNull(symbols.ColdRequest);
        Assert.Null(symbols.InstanceSelector);
        Assert.Equal("OrderService", symbols.Specification.Search);
        Assert.Equal("/repo", symbols.ColdRequest!.RepositoryRoot);
        Assert.Equal("class", symbols.Specification.EffectiveFilters.Kind);
        Assert.Equal(2, symbols.Specification.Limit);
        Assert.Equal("Order", summary.Specification.Search);
        Assert.Equal("abc", summary.InstanceSelector);
        Assert.Equal(["project", "namespace"], summary.Specification.EffectiveGroupBy);
        Assert.False(symbols.NoProgress);
    }

    [Fact]
    public void Parses_refresh_as_an_explicit_instance_operation()
    {
        var options = SemanticQueryCommandLine.Parse(
            ["refresh", "--instance", "abc", "--rebuild", "--json"]);

        Assert.Null(options.ColdRequest);
        Assert.Equal("abc", options.InstanceSelector);
        Assert.Equal("refresh", options.Specification.Command);
        Assert.True(options.Specification.Rebuild);
        Assert.True(options.Json);
    }

    [Fact]
    public void Enforces_exact_routing_and_command_option_boundaries()
    {
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["query", "symbols", "--instance", "abc", "--input", "Product.sln"]));
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["query", "signature", "--instance", "abc", "--limit", "2"]));
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["query", "symbols", "--instance", "abc", "--output", "out.json"]));
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["export", "--input", "Product.sln", "--output", "out.json"]));
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["export", "--instance", "abc"]));
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["refresh", "--input", "Product.sln"]));
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["refresh", "--instance", "abc", "--output", "out.json"]));
        Assert.Throws<CommandLineException>(() => SemanticQueryCommandLine.Parse(
            ["refresh", "--instance", "abc", "--rebuild", "--rebuild"]));
    }

    [Fact]
    public void Help_is_available_without_an_analysis_or_live_instance()
    {
        var help = SemanticQueryCommandLine.Parse(["query", "symbols", "--help"]);

        Assert.True(help.ShowHelp);
        Assert.Null(help.ColdRequest);
        Assert.Null(help.InstanceSelector);
    }

    [Fact]
    public void Parses_no_progress_for_cold_queries_as_a_local_presentation_flag()
    {
        var options = SemanticQueryCommandLine.Parse(
            ["query", "symbols", "Order", "--input", "Product.sln", "--no-progress"]);

        Assert.True(options.NoProgress);
        Assert.NotNull(options.ColdRequest);
    }

    [Fact]
    public async Task Parse_time_semantic_validation_returns_json_instead_of_crashing()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await SemanticQueryCommandLine.RunAsync(
                ["query", "signature", "--instance", "abc", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal(
                "invalid_arguments",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public async Task Invalid_path_values_return_structured_json_instead_of_crashing()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await SemanticQueryCommandLine.RunAsync(
                ["query", "symbols", "--input", "\0", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal(
                "invalid_arguments",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }
}
