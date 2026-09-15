using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Cli;

internal sealed record SemanticQueryCommandLineOptions(
    SemanticQuerySpec Specification,
    string? InstanceSelector,
    ProjectLoadRequest? ColdRequest,
    TimeSpan Timeout,
    bool Json,
    bool ShowHelp);

internal static class SemanticQueryCommandLine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsSemanticCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is "query" or "export";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        SemanticQueryCommandLineOptions options;
        try
        {
            options = Parse(args);
        }
        catch (CommandLineException exception)
        {
            return WriteFailure(
                args.Contains("--json", StringComparer.Ordinal),
                InferCommand(args),
                "invalid_arguments",
                exception.Message,
                exitCode: 2);
        }
        catch (SemanticQueryException exception)
        {
            // Parse-time semantic validation (for example, a missing exact
            // symbol or an invalid command-specific option) must use the same
            // structured error path as a worker response. In particular, do
            // not let `--json` callers receive an unhandled process exception.
            return WriteFailure(
                args.Contains("--json", StringComparer.Ordinal),
                InferCommand(args),
                exception.Code,
                exception.Message,
                ExitCode(exception.Code));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Path normalization happens during parsing so the worker never
            // receives an ambiguous route. Convert filesystem/path failures
            // at that boundary as well; malformed CLI input must not escape as
            // an unhandled process exception.
            return WriteFailure(
                args.Contains("--json", StringComparer.Ordinal),
                InferCommand(args),
                "invalid_arguments",
                exception.Message,
                exitCode: 2);
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var response = options.ColdRequest is not null
                ? await ExecuteColdAsync(options, cancellationToken).ConfigureAwait(false)
                : await ExecuteInstanceAsync(options, cancellationToken).ConfigureAwait(false);
            WriteResponse(response, options.Json);
            return response.Success ? 0 : ExitCode(response.Error?.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (options.Json)
            {
                return WriteFailure(
                    true,
                    options.Specification.Command,
                    "cancelled",
                    "The semantic request was cancelled.",
                    exitCode: 130,
                    mode: options.ColdRequest is null ? "instance" : "cold");
            }

            return 130;
        }
        catch (SemanticClientException exception)
        {
            return WriteFailure(
                options.Json,
                options.Specification.Command,
                exception.Code,
                exception.Message,
                ExitCode(exception.Code),
                options.ColdRequest is null ? "instance" : "cold");
        }
        catch (SemanticQueryException exception)
        {
            return WriteFailure(
                options.Json,
                options.Specification.Command,
                exception.Code,
                exception.Message,
                ExitCode(exception.Code),
                options.ColdRequest is null ? "instance" : "cold");
        }
        catch (TimeoutException exception)
        {
            return WriteFailure(
                options.Json,
                options.Specification.Command,
                "timeout",
                exception.Message,
                ExitCode("timeout"),
                options.ColdRequest is null ? "instance" : "cold");
        }
        catch (WatcherManagementException exception)
        {
            return WriteFailure(
                options.Json,
                options.Specification.Command,
                exception.ErrorCode,
                exception.Message,
                ExitCode(exception.ErrorCode),
                options.ColdRequest is null ? "instance" : "cold");
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
        {
            return WriteFailure(
                options.Json,
                options.Specification.Command,
                "internal_error",
                exception.Message,
                exitCode: 5,
                mode: options.ColdRequest is null ? "instance" : "cold");
        }
    }

    public static SemanticQueryCommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsSemanticCommand(args))
        {
            throw new CommandLineException("A semantic command must start with 'query' or 'export'.");
        }

        var isExport = args[0] == "export";
        if (args.Count == 2
            && args[1] is "--help" or "-h")
        {
            return new SemanticQueryCommandLineOptions(
                new SemanticQuerySpec(isExport ? "export" : "symbols"),
                null,
                null,
                TimeSpan.FromMinutes(10),
                args.Contains("--json", StringComparer.Ordinal),
                ShowHelp: true);
        }

        var command = isExport
            ? "export"
            : args.Count > 1 && !args[1].StartsWith("-", StringComparison.Ordinal)
                ? args[1]
                : throw new CommandLineException("The 'query' command requires a query verb.");
        if (!SemanticQueryCommands.All.Contains(command))
        {
            throw new CommandLineException($"Unsupported query verb '{command}'.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var json = false;
        var showHelp = false;
        string? positional = null;
        var startIndex = isExport ? 1 : 2;
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

            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (positional is not null)
                {
                    throw new CommandLineException($"Unexpected positional argument '{argument}'.");
                }

                positional = argument;
                continue;
            }

            var key = argument switch
            {
                "--instance" => "instance",
                "--input" => "input",
                "--root" => "root",
                "--configuration" => "configuration",
                "--target-framework" => "target-framework",
                "--symbol" => "symbol",
                "--path" => "path",
                "--project" => "project",
                "--namespace" => "namespace",
                "--kind" => "kind",
                "--direction" => "direction",
                "--group-by" => "group-by",
                "--limit" => "limit",
                "--cursor" => "cursor",
                "--snapshot" => "snapshot",
                "--timeout" => "timeout",
                "--output" => "output",
                _ => throw new CommandLineException($"Unknown option '{argument}'."),
            };
            if (++index >= args.Count)
            {
                throw new CommandLineException($"Option '{argument}' requires a value.");
            }

            var value = args[index];
            if (value.StartsWith("-", StringComparison.Ordinal) && value.Length > 0)
            {
                throw new CommandLineException($"Option '{argument}' requires a value.");
            }

            if (!values.TryAdd(key, value))
            {
                throw new CommandLineException($"Option '{argument}' may only be supplied once.");
            }
        }

        if (showHelp)
        {
            if (positional is not null || values.Count > 0)
            {
                throw new CommandLineException("Help cannot be combined with query options.");
            }

            return new SemanticQueryCommandLineOptions(
                new SemanticQuerySpec(command),
                null,
                null,
                TimeSpan.FromMinutes(10),
                json,
                ShowHelp: true);
        }

        if (isExport)
        {
            RejectExportQueryOptions(values, positional);
            var instance = Required(values, "instance");
            var output = FullPath(Required(values, "output"), Directory.GetCurrentDirectory());
            var timeout = ParseTimeout(values);
            return new SemanticQueryCommandLineOptions(
                new SemanticQuerySpec("export", OutputPath: output),
                instance,
                null,
                timeout,
                json,
                ShowHelp: false);
        }

        if (positional is not null && command is not ("symbols" or "usage-summary"))
        {
            throw new CommandLineException($"The '{command}' query does not accept a positional search.");
        }

        if (values.ContainsKey("output"))
        {
            throw new CommandLineException($"The '{command}' query does not accept '--output'.");
        }

        if (command == "signature" && values.ContainsKey("limit"))
        {
            throw new CommandLineException("The signature query does not accept '--limit'.");
        }

        var hasInstance = values.ContainsKey("instance");
        var hasInput = values.ContainsKey("input");
        if (hasInstance == hasInput)
        {
            throw new CommandLineException("A query requires exactly one route: '--instance <id>' or '--input <path>'.");
        }

        if (hasInstance && values.Keys.Any(IsAnalysisOption))
        {
            throw new CommandLineException("Analysis options cannot be combined with '--instance'.");
        }

        var timeoutValue = ParseTimeout(values);
        var filters = ParseFilters(values);
        var groupBy = ParseGroupBy(values);
        var specification = new SemanticQuerySpec(
            command,
            positional,
            Single(values, "symbol"),
            filters,
            Single(values, "direction"),
            groupBy,
            ParseLimit(values),
            Single(values, "cursor"),
            Single(values, "snapshot"));

        if (hasInstance)
        {
            SemanticQueryJsonParser.ValidateSpec(specification);
            return new SemanticQueryCommandLineOptions(
                specification,
                Required(values, "instance"),
                null,
                timeoutValue,
                json,
                ShowHelp: false);
        }

        var root = FullPath(Single(values, "root") ?? Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory());
        var input = FullPath(Required(values, "input"), root);
        var request = new ProjectLoadRequest(
            input,
            root,
            Single(values, "configuration") ?? "Debug",
            Single(values, "target-framework"));
        specification = NormalizePaths(specification, root);
        SemanticQueryJsonParser.ValidateSpec(specification);
        if (specification.Cursor is not null || specification.SnapshotId is not null)
        {
            throw new CommandLineException("Cold queries do not support --cursor or --snapshot; start a watcher for continuation.");
        }

        return new SemanticQueryCommandLineOptions(
            specification,
            null,
            request,
            timeoutValue,
            json,
            ShowHelp: false);

    }

    private static async Task<SemanticQueryResponse> ExecuteColdAsync(
        SemanticQueryCommandLineOptions options,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        await using var session = new IncrementalIndexSession(
            options.ColdRequest!,
            outputPath: null,
            semanticMode: "cold");
        try
        {
            await session.StartAsync(deadline.Token).ConfigureAwait(false);
            return await session
                .ExecuteSemanticQueryAsync(options.Specification, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new SemanticClientException(
                "timeout",
                "The cold semantic query request exceeded its deadline.");
        }
    }

    private static async Task<SemanticQueryResponse> ExecuteInstanceAsync(
        SemanticQueryCommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var registry = new WatcherSessionRegistry(WatcherSessionRegistry.ResolveStateDirectory());
        var descriptor = ResolveDescriptor(registry, options.InstanceSelector!);
        if (descriptor.SemanticEndpoint is null
            || descriptor.SemanticProtocolVersion != SemanticQueryProtocol.CurrentVersion)
        {
            throw new SemanticClientException(
                "unsupported_capability",
                "The selected watcher does not expose the current semantic query endpoint. Restart it with this version.");
        }

        var specification = NormalizePaths(options.Specification, descriptor.RepositoryRoot);
        return await new SemanticQueryClient()
            .SendAsync(descriptor, specification, options.Timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static WatcherSessionDescriptor ResolveDescriptor(
        WatcherSessionRegistry registry,
        string selector)
    {
        var candidates = registry.ReadAll()
            .Where(record => record.Descriptor is not null)
            .Select(record => record.Descriptor!)
            .Where(descriptor => descriptor.SessionId
                .ToString("D")
                .StartsWith(selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new SemanticClientException(
                "session_not_found",
                $"No watcher session matches '{selector}'."),
            _ => throw new SemanticClientException(
                "ambiguous_session",
                $"More than one watcher session matches '{selector}'."),
        };
    }

    private static SemanticQuerySpec NormalizePaths(
        SemanticQuerySpec specification,
        string root)
    {
        var filters = specification.Filters;
        if (filters is null)
        {
            return specification;
        }

        return specification with
        {
            Filters = filters with
            {
                Path = filters.Path is null ? null : FullPath(filters.Path, root),
                Project = filters.Project is null ? null : FullPath(filters.Project, root),
            },
        };
    }

    private static SemanticQueryFilters? ParseFilters(
        IReadOnlyDictionary<string, string?> values)
    {
        if (!values.Keys.Any(key => key is "path" or "project" or "namespace" or "kind"))
        {
            return null;
        }

        var kind = Single(values, "kind");
        if (kind is not null && !SemanticQueryKinds.IsKnown(kind))
        {
            throw new CommandLineException($"Unknown declaration kind '{kind}'.");
        }

        return new SemanticQueryFilters(
            Single(values, "path"),
            Single(values, "project"),
            values.TryGetValue("namespace", out var @namespace) ? @namespace : null,
            kind);
    }

    private static IReadOnlyList<string>? ParseGroupBy(
        IReadOnlyDictionary<string, string?> values)
    {
        var value = Single(values, "group-by");
        if (value is null)
        {
            return null;
        }

        var groupBy = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (groupBy.Length == 0
            || groupBy.Length > 2
            || groupBy.Distinct(StringComparer.Ordinal).Count() != groupBy.Length
            || groupBy.Any(group => group is not ("project" or "namespace")))
        {
            throw new CommandLineException("--group-by accepts project and/or namespace once each.");
        }

        return groupBy;
    }

    private static int ParseLimit(IReadOnlyDictionary<string, string?> values)
    {
        var value = Single(values, "limit");
        if (value is null)
        {
            return SemanticQueryProtocol.DefaultLimit;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit is < 1 or > SemanticQueryProtocol.MaximumLimit)
        {
            throw new CommandLineException(
                $"--limit must be between 1 and {SemanticQueryProtocol.MaximumLimit}.");
        }

        return limit;
    }

    private static TimeSpan ParseTimeout(IReadOnlyDictionary<string, string?> values)
    {
        var value = Single(values, "timeout");
        if (value is null)
        {
            return TimeSpan.FromMilliseconds(SemanticQueryProtocol.DefaultTimeoutMilliseconds);
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeout)
            || timeout <= TimeSpan.Zero
            || timeout > TimeSpan.FromMilliseconds(SemanticQueryProtocol.MaximumTimeoutMilliseconds))
        {
            throw new CommandLineException("The timeout must be a positive TimeSpan no greater than 01:00:00.");
        }

        return timeout;
    }

    private static void RejectExportQueryOptions(
        IReadOnlyDictionary<string, string?> values,
        string? positional)
    {
        if (positional is not null)
        {
            throw new CommandLineException("The export command does not accept positional arguments.");
        }

        var invalid = values.Keys.FirstOrDefault(key => key is not ("instance" or "output" or "timeout"));
        if (invalid is not null)
        {
            throw new CommandLineException($"The export command does not accept '--{invalid}'.");
        }
    }

    private static bool IsAnalysisOption(string key) =>
        key is "input" or "root" or "configuration" or "target-framework";

    private static string Required(
        IReadOnlyDictionary<string, string?> values,
        string key) =>
        values.TryGetValue(key, out var value) && value is not null && value.Length > 0
            ? value
            : throw new CommandLineException($"Option '--{key}' is required.");

    private static string? Single(
        IReadOnlyDictionary<string, string?> values,
        string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string FullPath(string path, string basePath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CommandLineException("A path value cannot be empty.");
        }

        return Path.GetFullPath(path, basePath);
    }

    private static string InferCommand(IReadOnlyList<string> args) =>
        args.Count > 1 && !args[0].Equals("export", StringComparison.Ordinal)
            ? args[1]
            : args.Count > 0 ? args[0] : "query";

    private static void WriteResponse(SemanticQueryResponse response, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            return;
        }

        if (!response.Success)
        {
            Console.Error.WriteLine($"Error ({response.Error?.Code}): {response.Error?.Message}");
            return;
        }

        Console.WriteLine(
            $"{response.Command}: {response.Items.Count} result(s), "
            + $"snapshot={response.Snapshot?.Id ?? "<none>"}"
            + (response.Page?.HasMore == true ? " (more available)" : string.Empty));
        foreach (var item in response.Items)
        {
            Console.WriteLine(item.GetRawText());
        }

        if (response.Export is not null)
        {
            Console.WriteLine(
                $"exported {response.Export.NodeCount} nodes and {response.Export.EdgeCount} edges "
                + $"to {response.Export.OutputPath}");
        }
    }

    private static int WriteFailure(
        bool json,
        string command,
        string code,
        string message,
        int exitCode,
        string mode = "cold")
    {
        var response = SemanticQueryResponse.Failure(null, mode, command, code, message);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"Error ({code}): {message}");
        }

        return exitCode;
    }

    private static int ExitCode(string? code) => code switch
    {
        null => 5,
        "invalid_arguments" or "invalid_request" or "symbol_not_found" or "invalid_cursor"
            or "unsupported_symbol" or "unsupported_capability" => 2,
        "session_not_found" or "ambiguous_session" or "unreachable" or "session_failed"
            or "session_stopping" or "incompatible_protocol" or "session_mismatch" => 3,
        "server_busy" or "timeout" or "stale_snapshot" => 4,
        "output_conflict" or "io_error" or "internal_error" => 5,
        "cancelled" => 130,
        _ => 5,
    };

    public static string Usage => "Usage:\n"
        + "  graphify-csharp query <verb> [options]\n"
        + "  graphify-csharp export --instance <id|prefix> --output <path> [--json]\n\n"
        + "Query verbs: symbols, signature, usages, callers, hierarchy, arguments, usage-summary\n"
        + "Routing: --instance <id|prefix> or cold --input <solution|project|file.cs>\n"
        + "Common: --json --limit <1..1000> --cursor <token> --snapshot <id> --timeout <TimeSpan>\n"
        + "Scope: --path <file|directory> --project <csproj> --namespace <name> --kind <declaration_kind>\n"
        + "Exact queries: --symbol <graph node id>\n"
        + "Hierarchy: --direction base|derived|contracts|implementations|all\n"
        + "Summaries: [search] --group-by project,namespace\n\n"
        + "Examples:\n"
        + "  graphify-csharp query symbols OrderService --instance abc --json\n"
        + "  graphify-csharp query callers --instance abc --symbol M:... --json\n"
        + "  graphify-csharp query symbols OrderService --input ./Product.sln --root . --json";
}
