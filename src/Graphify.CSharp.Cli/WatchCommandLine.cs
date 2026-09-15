using System.Globalization;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Cli;

internal sealed record WatchCommandLineOptions(
    ProjectLoadRequest Request,
    TimeSpan WatchScanInterval,
    bool NoProgress,
    bool ShowHelp);

internal static class WatchCommandLine
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        WatchCommandLineOptions options;
        try
        {
            options = Parse(args);
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        await using var host = new IncrementalWatcherHost(
            options.Request,
            outputPath: null,
            new IncrementalWatcherOptions(options.WatchScanInterval),
            managementOptions: new WatcherManagementOptions(
                WatcherSessionRegistry.ResolveStateDirectory(),
                Program.GetToolVersion()));
        var sessionId = host.Session.SessionId.ToString("D");
        var targetFramework = options.Request.TargetFramework ?? "automatic";
        await using var progress = options.NoProgress
            ? null
            : new CliProgressReporter(
                host.Observation,
                startingMessage:
                    $"Starting; session={sessionId}; registering endpoints; "
                    + $"input={options.Request.InputPath}; root={options.Request.RepositoryRoot}; "
                    + $"configuration={options.Request.Configuration}; target-framework={targetFramework}.");
        progress?.Start();
        try
        {
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
            progress?.WriteCompletionSummary("Ready");
            if (stopRequested.IsCompleted)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                return 0;
            }

            Console.WriteLine(
                $"Watching {options.Request.RepositoryRoot}; "
                + $"session={sessionId}; "
                + $"query with 'graphify-csharp query symbols <name> --instance {sessionId}'; "
                + $"export with 'graphify-csharp export --instance {sessionId}'.");
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    public static WatchCommandLineOptions Parse(
        IReadOnlyList<string> args,
        string? currentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] != "watch")
        {
            throw new CommandLineException("The watch command must start with 'watch'.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionalInput = (string?)null;
        var noProgress = false;
        var showHelp = false;
        for (var index = 1; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                if (showHelp)
                {
                    throw new CommandLineException("Option '--help' may only be supplied once.");
                }

                showHelp = true;
                continue;
            }

            if (argument == "--no-progress")
            {
                if (noProgress)
                {
                    throw new CommandLineException("Option '--no-progress' may only be supplied once.");
                }

                noProgress = true;
                continue;
            }

            if (argument is "--json" or "--output" or "-o" or "--rebuild")
            {
                throw new CommandLineException(
                    argument is "--output" or "-o"
                        ? "The watch command is output-free; use 'export --instance <id> --output <path>'."
                        : $"The watch command does not accept '{argument}'.");
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (positionalInput is not null)
                {
                    throw new CommandLineException($"Unexpected positional argument '{argument}'.");
                }

                positionalInput = argument;
                continue;
            }

            var key = argument switch
            {
                "--input" or "-i" => "input",
                "--root" or "-r" => "root",
                "--configuration" or "-c" => "configuration",
                "--target-framework" or "-f" => "target-framework",
                "--watch-scan-interval" => "watch-scan-interval",
                _ => throw new CommandLineException($"Unknown option '{argument}'."),
            };
            if (++index >= args.Count || args[index].StartsWith("-", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Option '{argument}' requires a value.");
            }

            if (!values.TryAdd(key, args[index]))
            {
                throw new CommandLineException($"Option '{argument}' may only be supplied once.");
            }
        }

        if (showHelp)
        {
            if (positionalInput is not null || values.Count > 0)
            {
                throw new CommandLineException("Help cannot be combined with watch options.");
            }

            return new WatchCommandLineOptions(
                new ProjectLoadRequest(
                    Path.Combine(Directory.GetCurrentDirectory(), "help.csproj"),
                    Directory.GetCurrentDirectory(),
                    "Debug",
                    null),
                TimeSpan.FromMinutes(5),
                noProgress,
                ShowHelp: true);
        }

        var callerDirectory = AnalysisInputResolver.CurrentDirectory(currentDirectory);
        if (values.ContainsKey("input") && positionalInput is not null)
        {
            throw new CommandLineException("Input was supplied both positionally and with '--input'.");
        }

        var repositoryRoot = AnalysisInputResolver.ResolveRoot(
            Single(values, "root"),
            callerDirectory);
        var inputPath = AnalysisInputResolver.ResolveInput(
            Single(values, "input") ?? positionalInput,
            repositoryRoot,
            callerDirectory,
            "watch");
        var interval = TimeSpan.FromMinutes(5);
        var intervalValue = Single(values, "watch-scan-interval");
        if (intervalValue is not null
            && (!TimeSpan.TryParse(intervalValue, CultureInfo.InvariantCulture, out interval)
                || interval <= TimeSpan.Zero))
        {
            throw new CommandLineException(
                "Watch scan interval must be a positive TimeSpan such as '00:05:00'.");
        }

        var configuration = Single(values, "configuration") ?? "Debug";
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new CommandLineException("Configuration cannot be empty.");
        }

        return new WatchCommandLineOptions(
            new ProjectLoadRequest(
                inputPath,
                repositoryRoot,
                configuration.Trim(),
                Single(values, "target-framework")),
            interval,
            noProgress,
            ShowHelp: false);
    }

    private static string? Single(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

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

    public static string Usage => "Usage: graphify-csharp watch [--input <solution|project|file.cs>] [options]\n\n"
        + "Starts an output-free foreground semantic session. Use ps to find the session\n"
        + "and query/refresh/export with --instance <id|prefix>. If --input is omitted,\n"
        + "one unambiguous solution or project is discovered directly under --root\n"
        + "(or the current directory).\n\n"
        + "Options:\n"
        + "  -i, --input <path>              C# solution/project/file-based app to watch\n"
        + "  -r, --root <path>               Repository root for stable paths\n"
        + "  -c, --configuration <name>      MSBuild configuration (default: Debug)\n"
        + "  -f, --target-framework <tfm>    Select one TFM when target selection is ambiguous\n"
        + "      --watch-scan-interval <t>   Backup inventory interval (default: 00:05:00)\n"
        + "      --no-progress              Suppress progress messages on stderr\n"
        + "  -h, --help                      Show this help";
}
