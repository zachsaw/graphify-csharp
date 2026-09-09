using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
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
            using var loaded = await new RoslynWorkspaceLoader().LoadAsync(new ProjectLoadRequest(
                options.InputPath,
                options.RepositoryRoot,
                options.Configuration,
                options.TargetFramework));
            var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
            var extractor = new SemanticReferenceExtractor();
            var graph = await extractor.ExtractAsync(loaded, catalog);
            var diagnostics = loaded.Diagnostics
                .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
                .Concat(catalog.Diagnostics)
                .Concat(extractor.Diagnostics);
            var json = new GraphifyJsonSerializer().Serialize(
                graph,
                new GraphifySerializationOptions(diagnostics));

            var outputDirectory = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await File.WriteAllTextAsync(options.OutputPath, json);
            Console.WriteLine($"Wrote {graph.Nodes.Length} nodes and {graph.Edges.Length} edges to {options.OutputPath}.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }
}
