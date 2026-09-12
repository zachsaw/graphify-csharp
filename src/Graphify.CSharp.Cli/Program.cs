using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;
using System.Reflection;

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
        if (WatcherManagementCommandLine.IsManagementCommand(args))
        {
            try
            {
                return await WatcherManagementCli
                    .RunAsync(WatcherManagementCommandLine.Parse(args), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CommandLineException exception)
            {
                return WatcherManagementCli.WriteCommandLineFailure(
                    args.Contains("--json", StringComparer.Ordinal),
                    exception.Message);
            }
        }

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
                    new IncrementalWatcherOptions(options.WatchScanInterval),
                    managementOptions: new WatcherManagementOptions(
                        WatcherSessionRegistry.ResolveStateDirectory(),
                        GetToolVersion()));
                var start = host.StartAsync(cancellationToken);
                var stopRequested = host.WaitForStopRequestedAsync();
                var startup = cancellationToken.CanBeCanceled
                    ? start.WaitAsync(cancellationToken)
                    : start;
                if (await Task.WhenAny(startup, stopRequested).ConfigureAwait(false) == stopRequested)
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    await IgnoreRequestedStopStartupAsync(start).ConfigureAwait(false);
                    return 0;
                }

                await startup.ConfigureAwait(false);
                if (stopRequested.IsCompleted)
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    return 0;
                }

                Console.WriteLine(
                    $"Watching {options.RepositoryRoot}; refresh with graphify-csharp --input {options.InputPath}.");
                var shutdown = host.WaitForShutdownAsync(cancellationToken);
                if (await Task.WhenAny(shutdown, stopRequested).ConfigureAwait(false) == stopRequested)
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    await shutdown.ConfigureAwait(false);
                }
                else
                {
                    await shutdown.ConfigureAwait(false);
                }

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

    private static string GetToolVersion() =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static async Task IgnoreRequestedStopStartupAsync(Task start)
    {
        try
        {
            await start.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
