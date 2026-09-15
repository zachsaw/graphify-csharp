using Graphify.CSharp.Incremental;
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
        if (CliHelp.IsTopLevelHelp(args))
        {
            Console.WriteLine(CliHelp.Usage);
            return 0;
        }

        if (args.Length > 0 && args[0] == "watch")
        {
            return await WatchCommandLine
                .RunAsync(args, cancellationToken)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "export")
        {
            return await ExportCommandLine
                .RunAsync(args, cancellationToken)
                .ConfigureAwait(false);
        }

        if (SemanticQueryCommandLine.IsSemanticCommand(args))
        {
            return await SemanticQueryCommandLine
                .RunAsync(args, cancellationToken)
                .ConfigureAwait(false);
        }

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

        return await DiskExportCommandLine
            .RunAsync(args, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string GetToolVersion() =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";

}
