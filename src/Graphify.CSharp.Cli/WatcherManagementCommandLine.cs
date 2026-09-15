namespace Graphify.CSharp.Cli;

internal enum WatcherManagementCommandKind
{
    List,
    Inspect,
    Diagnostics,
    Stop,
}

internal sealed record WatcherManagementCommandLineOptions(
    WatcherManagementCommandKind Kind,
    string? SessionSelector,
    string? OutputPath,
    bool Json,
    bool ShowHelp);

internal static class WatcherManagementCommandLine
{
    public static bool IsManagementCommand(IReadOnlyList<string> args) =>
        args.Count > 0
        && args[0] is "ps" or "info" or "inspect" or "diagnostics" or "stop";

    public static WatcherManagementCommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsManagementCommand(args))
        {
            throw new CommandLineException("A management command must be 'ps', 'info', 'inspect', 'diagnostics', or 'stop'.");
        }

        var kind = args[0] switch
        {
            "ps" => WatcherManagementCommandKind.List,
            "info" or "inspect" => WatcherManagementCommandKind.Inspect,
            "diagnostics" => WatcherManagementCommandKind.Diagnostics,
            "stop" => WatcherManagementCommandKind.Stop,
            _ => throw new InvalidOperationException("Unknown management command."),
        };
        var json = false;
        var showHelp = false;
        string? selector = null;
        string? outputPath = null;
        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--json":
                    if (json)
                    {
                        throw new CommandLineException("Option '--json' may only be supplied once.");
                    }

                    json = true;
                    break;
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--output":
                    if (outputPath is not null
                        || ++index >= args.Count
                        || args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CommandLineException("Option '--output' requires one value and may only be supplied once.");
                    }

                    outputPath = args[index];
                    break;
                case var value when !value.StartsWith("-", StringComparison.Ordinal) && selector is null:
                    selector = value;
                    break;
                default:
                    throw new CommandLineException($"Unknown management argument '{args[index]}'.");
            }
        }

        if (kind == WatcherManagementCommandKind.List && selector is not null)
        {
            throw new CommandLineException("The 'ps' command does not accept a session ID.");
        }

        if (outputPath is not null && kind != WatcherManagementCommandKind.Diagnostics)
        {
            throw new CommandLineException("Option '--output' is only valid for 'diagnostics'.");
        }

        if (kind == WatcherManagementCommandKind.Diagnostics && outputPath is null && !showHelp)
        {
            throw new CommandLineException("The 'diagnostics' command requires '--output <path>'.");
        }

        if (kind != WatcherManagementCommandKind.List && selector is null && !showHelp)
        {
            throw new CommandLineException($"The '{args[0]}' command requires a session ID.");
        }

        if (showHelp && selector is not null)
        {
            throw new CommandLineException("Help cannot be combined with a session ID.");
        }

        return new WatcherManagementCommandLineOptions(kind, selector, outputPath, json, showHelp);
    }

    public static string Usage => "Usage:\n"
        + "  graphify-csharp ps [--json]\n"
        + "  graphify-csharp info <session-id|prefix> [--json]\n"
        + "  graphify-csharp inspect <session-id|prefix> [--json]\n"
        + "  graphify-csharp diagnostics <session-id|prefix> --output <path> [--json]\n"
        + "  graphify-csharp stop <session-id|prefix> [--json]";
}
