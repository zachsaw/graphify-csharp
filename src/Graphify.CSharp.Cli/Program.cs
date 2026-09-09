using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(CommandLineOptions.Usage);
            return 0;
        }

        try
        {
            var result = await new IncrementalRefreshEngine().RefreshAsync(
                new ProjectLoadRequest(
                    options.InputPath,
                    options.RepositoryRoot,
                    options.Configuration,
                    options.TargetFramework),
                options.OutputPath,
                options.Rebuild);
            var mode = result.ExtractedProjectCount == 0 ? "reused" : $"extracted {result.ExtractedProjectCount} project(s)";
            Console.WriteLine(
                $"Wrote {result.Graph.Nodes.Length} nodes and {result.Graph.Edges.Length} edges to {options.OutputPath} ({mode}, reused {result.ReusedProjectCount}).");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }
}
