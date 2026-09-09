using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(args, shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
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
            var request = new ProjectLoadRequest(
                options.InputPath,
                options.RepositoryRoot,
                options.Configuration,
                options.TargetFramework);
            if (options.Watch)
            {
                await using var host = new IncrementalWatcherHost(
                    request,
                    options.OutputPath,
                    new IncrementalWatcherOptions(options.WatchScanInterval));
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine(
                    $"Watching {options.RepositoryRoot}; refresh with graphify-csharp --input {options.InputPath}.");
                await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var identity = new RefreshRequestIdentity(
                request.InputPath,
                request.RepositoryRoot,
                request.Configuration,
                request.TargetFramework);
            var remote = await new IncrementalRefreshControlClient()
                .TryRefreshAsync(identity, options.OutputPath, options.Rebuild, cancellationToken)
                .ConfigureAwait(false);
            if (remote is not null)
            {
                Console.WriteLine(
                    $"Wrote {remote.NodeCount} nodes and {remote.EdgeCount} edges to {options.OutputPath} "
                    + $"(watcher, extracted {remote.ExtractedProjectCount} project(s), reused {remote.ReusedProjectCount}).");
                return 0;
            }

            var result = await new IncrementalRefreshEngine().RefreshAsync(
                    request,
                    options.OutputPath,
                    options.Rebuild,
                    cancellationToken)
                .ConfigureAwait(false);
            var mode = result.ExtractedProjectCount == 0 ? "reused" : $"extracted {result.ExtractedProjectCount} project(s)";
            Console.WriteLine(
                $"Wrote {result.Graph.Nodes.Length} nodes and {result.Graph.Edges.Length} edges to {options.OutputPath} ({mode}, reused {result.ReusedProjectCount}).");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or InvalidDataException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }
}
