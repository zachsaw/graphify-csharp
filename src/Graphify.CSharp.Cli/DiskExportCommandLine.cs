using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Cli;

internal sealed record DiskExportCommandLineOptions(
    ProjectLoadRequest Request,
    string OutputPath,
    bool Rebuild,
    bool Json,
    bool NoProgress,
    bool ShowHelp);

internal static class DiskExportCommandLine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        DiskExportCommandLineOptions options;
        try
        {
            options = Parse(args);
        }
        catch (CommandLineException exception)
        {
            return WriteFailure(
                args.Contains("--json", StringComparer.Ordinal),
                "export",
                exception.Message,
                exitCode: 2);
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var observation = new IndexingObservation();
        CliProgressReporter? progress = options.NoProgress
            ? null
            : new CliProgressReporter(observation);
        progress?.Start();
        try
        {
            var result = await new IncrementalRefreshEngine(observation: observation)
                .RefreshAsync(
                    options.Request,
                    options.OutputPath,
                    options.Rebuild,
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.WriteCompletionSummary("Completed");
            var response = CreateResponse(options, result);
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            }
            else
            {
                var mode = result.ExtractedProjectCount == 0
                    ? "reused"
                    : $"extracted {result.ExtractedProjectCount} project(s)";
                Console.WriteLine(
                    $"Wrote {result.Graph.Nodes.Length} nodes and {result.Graph.Edges.Length} edges "
                    + $"to {options.OutputPath} ({mode}, reused {result.ReusedProjectCount}).");
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WriteFailure(
                options.Json,
                "export",
                "The export was cancelled.",
                exitCode: 130);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            return WriteFailure(
                options.Json,
                "export",
                exception.Message,
                exitCode: 1);
        }
        finally
        {
            if (progress is not null)
            {
                await progress.DisposeAsync().ConfigureAwait(false);
            }

            await observation.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static DiskExportCommandLineOptions Parse(
        IReadOnlyList<string> args,
        string? currentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var startIndex = args.Count > 0 && args[0] == "export" ? 1 : 0;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionalInput = (string?)null;
        var json = false;
        var noProgress = false;
        var rebuild = false;
        var showHelp = false;
        for (var index = startIndex; index < args.Count; index++)
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

            if (argument == "--json")
            {
                if (json)
                {
                    throw new CommandLineException("Option '--json' may only be supplied once.");
                }

                json = true;
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

            if (argument == "--rebuild")
            {
                if (rebuild)
                {
                    throw new CommandLineException("Option '--rebuild' may only be supplied once.");
                }

                rebuild = true;
                continue;
            }

            if (argument == "--watch")
            {
                throw new CommandLineException("The '--watch' form was removed in v0.2; use 'watch' instead.");
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
                "--output" or "-o" => "output",
                "--configuration" or "-c" => "configuration",
                "--target-framework" or "-f" => "target-framework",
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
            if (positionalInput is not null || values.Count > 0 || rebuild)
            {
                throw new CommandLineException("Help cannot be combined with export options.");
            }

            return new DiskExportCommandLineOptions(
                new ProjectLoadRequest(
                    Path.Combine(Directory.GetCurrentDirectory(), "help.csproj"),
                    Directory.GetCurrentDirectory(),
                    "Debug",
                    null),
                string.Empty,
                false,
                json,
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
        var input = Single(values, "input") ?? positionalInput;
        var inputPath = AnalysisInputResolver.ResolveInput(
            input,
            repositoryRoot,
            callerDirectory,
            "export");
        var outputPath = AnalysisInputResolver.ResolvePath(
            Required(values, "output"),
            callerDirectory,
            "output");
        var configuration = Single(values, "configuration") ?? "Debug";
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new CommandLineException("Configuration cannot be empty.");
        }

        return new DiskExportCommandLineOptions(
            new ProjectLoadRequest(
                inputPath,
                repositoryRoot,
                configuration.Trim(),
                Single(values, "target-framework")),
            outputPath,
            rebuild,
            json,
            noProgress,
            ShowHelp: false);
    }

    private static SemanticQueryResponse CreateResponse(
        DiskExportCommandLineOptions options,
        IncrementalRefreshResult result)
    {
        var outputDigest = result.OutputDigest ?? IncrementalHashing.Sha256File(options.OutputPath);
        return new SemanticQueryResponse(
            SemanticQueryProtocol.SchemaVersion,
            SemanticQueryProtocol.CurrentVersion,
            SessionId: null,
            Mode: "disk",
            Command: "export",
            Success: true,
            Error: null,
            Snapshot: null,
            Scope: null,
            Items: Array.Empty<JsonElement>(),
            Page: new SemanticQueryPage(0, false, null),
            Diagnostics: Array.Empty<SemanticQueryDiagnostic>(),
            DiagnosticsTruncated: false,
            Export: new SemanticExportResult(
                options.OutputPath,
                outputDigest,
                result.Graph.Nodes.Length,
                result.Graph.Edges.Length));
    }

    private static int WriteFailure(
        bool json,
        string command,
        string message,
        int exitCode)
    {
        var response = SemanticQueryResponse.Failure(
            sessionId: null,
            mode: "disk",
            command,
            errorCode: exitCode == 130 ? "cancelled" : "io_error",
            message);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"Error: {message}");
            Console.Error.WriteLine(Usage);
        }

        return exitCode;
    }

    private static string? Single(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string Required(IReadOnlyDictionary<string, string?> values, string key) =>
        Single(values, key) is { Length: > 0 } value
            ? value
            : throw new CommandLineException($"Option '--{key}' is required.");

    public static string Usage => "Usage: graphify-csharp export [--input <solution|project|file.cs>] --output <path> [options]\n"
        + "       graphify-csharp [<input>] --output <path> [options]\n\n"
        + "When --input is omitted, one unambiguous solution or project is discovered\n"
        + "directly under --root (or the current directory).\n\n"
        + "Options:\n"
        + "  -i, --input <path>              C# solution/project/file-based app to export\n"
        + "  -r, --root <path>               Repository root for stable paths\n"
        + "  -o, --output <path>             Graphify JSON output path\n"
        + "  -c, --configuration <name>      MSBuild configuration (default: Debug)\n"
        + "  -f, --target-framework <tfm>    Select one TFM when target selection is ambiguous\n"
        + "      --rebuild                  Ignore incremental cache and rebuild all projects\n"
        + "      --json                     Write one structured result to stdout\n"
        + "      --no-progress              Suppress progress messages on stderr\n"
        + "  -h, --help                      Show this help";
}
