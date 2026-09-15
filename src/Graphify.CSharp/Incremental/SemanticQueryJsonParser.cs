using System.Text.Json;

namespace Graphify.CSharp.Incremental;

internal static class SemanticQueryJsonParser
{
    private static readonly IReadOnlySet<string> EnvelopeProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "protocol_version",
        "session_id",
        "command",
        "analysis_key",
        "timeout_ms",
        "query",
        "output_path",
        "rebuild",
    };

    private static readonly IReadOnlySet<string> QueryProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "search",
        "symbol_id",
        "filters",
        "direction",
        "group_by",
        "limit",
        "cursor",
        "snapshot_id",
    };

    private static readonly IReadOnlySet<string> FilterProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "path",
        "project",
        "namespace",
        "kind",
    };

    public static SemanticQueryWireRequest Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(
                payload.ToArray(),
                new JsonDocumentOptions
                {
                    MaxDepth = SemanticQueryProtocol.MaximumJsonDepth,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "The semantic request must be a JSON object.");
            RejectDuplicateProperties(root, "envelope");
            RejectUnknown(root, EnvelopeProperties, "envelope");

            var protocolVersion = RequiredInt(root, "protocol_version");
            var sessionId = RequiredGuid(root, "session_id");
            var command = RequiredPropertyString(root, "command");
            var analysisKey = RequiredPropertyString(root, "analysis_key");
            var timeoutMilliseconds = OptionalInt(
                root,
                "timeout_ms",
                SemanticQueryProtocol.DefaultTimeoutMilliseconds);
            ValidateTimeout(timeoutMilliseconds);
            var rebuild = OptionalBool(root, "rebuild", defaultValue: false);

            if (!SemanticQueryCommands.All.Contains(command))
            {
                throw new SemanticQueryException("invalid_request", $"Unsupported semantic command '{command}'.");
            }

            var hasQuery = root.TryGetProperty("query", out var queryElement);
            var hasOutputPath = root.TryGetProperty("output_path", out var outputPathElement);
            if (command == "export")
            {
                if (rebuild)
                {
                    throw new SemanticQueryException(
                        "invalid_request",
                        "The export command does not accept rebuild.");
                }

                if (hasQuery)
                {
                    throw new SemanticQueryException("invalid_request", "The export command does not accept a query object.");
                }

                var outputPath = hasOutputPath
                    ? RequiredString(outputPathElement, "output_path")
                    : throw new SemanticQueryException("invalid_request", "The export command requires output_path.");
                return new SemanticQueryWireRequest(
                    protocolVersion,
                    sessionId,
                    command,
                    analysisKey,
                    timeoutMilliseconds,
                    new SemanticQuerySpec(command, OutputPath: outputPath));
            }

            if (hasOutputPath)
            {
                throw new SemanticQueryException("invalid_request", $"The '{command}' command does not accept output_path.");
            }

            if (command == "refresh")
            {
                if (hasQuery)
                {
                    throw new SemanticQueryException(
                        "invalid_request",
                        "The refresh command does not accept a query object.");
                }

                var refreshSpec = new SemanticQuerySpec(command, Rebuild: rebuild);
                ValidateSpec(refreshSpec);
                return new SemanticQueryWireRequest(
                    protocolVersion,
                    sessionId,
                    command,
                    analysisKey,
                    timeoutMilliseconds,
                    refreshSpec);
            }

            if (rebuild)
            {
                throw new SemanticQueryException(
                    "invalid_request",
                    $"The '{command}' command does not accept rebuild.");
            }

            var spec = hasQuery
                ? ParseQuery(command, queryElement)
                : new SemanticQuerySpec(command, Rebuild: rebuild);

            ValidateSpec(spec);
            return new SemanticQueryWireRequest(
                protocolVersion,
                sessionId,
                command,
                analysisKey,
                timeoutMilliseconds,
                spec);
        }
        catch (SemanticQueryException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SemanticQueryException("invalid_request", $"The semantic request JSON is invalid: {exception.Message}");
        }
        catch (FormatException exception)
        {
            throw new SemanticQueryException("invalid_request", exception.Message);
        }
    }

    public static void ValidateSpec(SemanticQuerySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!SemanticQueryCommands.All.Contains(spec.Command))
        {
            throw new SemanticQueryException("invalid_arguments", $"Unsupported semantic command '{spec.Command}'.");
        }

        if (spec.Limit is < 1 or > SemanticQueryProtocol.MaximumLimit)
        {
            throw new SemanticQueryException(
                "invalid_arguments",
                $"Limit must be between 1 and {SemanticQueryProtocol.MaximumLimit}.");
        }

        if (spec.EffectiveFilters.Kind is not null
            && !SemanticQueryKinds.IsKnown(spec.EffectiveFilters.Kind))
        {
            throw new SemanticQueryException(
                "invalid_arguments",
                $"Unknown declaration kind '{spec.EffectiveFilters.Kind}'.");
        }

        if (spec.Command == "refresh")
        {
            if (spec.Search is not null
                || spec.SymbolId is not null
                || !spec.EffectiveFilters.IsEmpty
                || spec.Direction is not null
                || spec.EffectiveGroupBy.Count > 0
                || spec.Limit != SemanticQueryProtocol.DefaultLimit
                || spec.Cursor is not null
                || spec.SnapshotId is not null
                || spec.OutputPath is not null)
            {
                throw new SemanticQueryException(
                    "invalid_arguments",
                    "The refresh command accepts only rebuild.");
            }

            return;
        }

        if (spec.Rebuild)
        {
            throw new SemanticQueryException(
                "invalid_arguments",
                $"The '{spec.Command}' command does not accept rebuild.");
        }

        if (SemanticQueryCommands.IsExact(spec.Command)
            && string.IsNullOrWhiteSpace(spec.SymbolId))
        {
            throw new SemanticQueryException("invalid_arguments", $"The '{spec.Command}' command requires symbol_id.");
        }

        if (spec.Command == "signature" && !spec.EffectiveFilters.IsEmpty)
        {
            throw new SemanticQueryException("invalid_arguments", "The signature command does not accept scope filters.");
        }

        if (spec.Command == "arguments"
            && (spec.EffectiveFilters.Kind is not null || spec.EffectiveFilters.Namespace is not null))
        {
            throw new SemanticQueryException("invalid_arguments", "The arguments command accepts only path and project filters.");
        }

        if (spec.Command != "hierarchy" && spec.Direction is not null)
        {
            throw new SemanticQueryException("invalid_arguments", "Direction is only valid for hierarchy.");
        }

        if (spec.Command != "usage-summary" && spec.EffectiveGroupBy.Count > 0)
        {
            throw new SemanticQueryException("invalid_arguments", "group_by is only valid for usage-summary.");
        }

        if (spec.Command == "usage-summary"
            && (spec.EffectiveGroupBy.Count > 2
                || spec.EffectiveGroupBy.Distinct(StringComparer.Ordinal).Count() != spec.EffectiveGroupBy.Count
                || spec.EffectiveGroupBy.Any(group => group is not ("project" or "namespace"))))
        {
            throw new SemanticQueryException("invalid_arguments", "group_by accepts only project and namespace.");
        }

        if (spec.Command == "hierarchy"
            && spec.Direction is not null
            && spec.Direction is not ("base" or "derived" or "contracts" or "implementations" or "all"))
        {
            throw new SemanticQueryException("invalid_arguments", "Hierarchy direction must be base, derived, contracts, implementations, or all.");
        }

        if (spec.Command == "signature" && spec.Cursor is not null)
        {
            throw new SemanticQueryException("invalid_arguments", "The signature command does not support cursors.");
        }

        if (spec.Command == "export" && string.IsNullOrWhiteSpace(spec.OutputPath))
        {
            throw new SemanticQueryException("invalid_arguments", "The export command requires output_path.");
        }

        if (spec.Command != "export" && spec.OutputPath is not null)
        {
            throw new SemanticQueryException(
                "invalid_arguments",
                $"The '{spec.Command}' command does not accept output_path.");
        }

        if (spec.Command == "usage-summary"
            && (spec.SymbolId is not null
                || spec.Direction is not null
                || spec.SnapshotId is not null))
        {
            throw new SemanticQueryException(
                "invalid_arguments",
                "usage-summary accepts filters, search, grouping and pagination, not symbol_id, direction or snapshot_id.");
        }

        if (spec.Command == "symbols"
            && (spec.Direction is not null
                || spec.EffectiveGroupBy.Count > 0
                || spec.SnapshotId is not null))
        {
            throw new SemanticQueryException(
                "invalid_arguments",
                "symbols accepts search, filters and pagination, not direction, grouping or snapshot_id.");
        }

        if (spec.Command is "signature" or "usages" or "callers" or "hierarchy" or "arguments"
            && (spec.Search is not null || spec.EffectiveGroupBy.Count > 0))
        {
            throw new SemanticQueryException(
                "invalid_arguments",
                $"The '{spec.Command}' command does not accept search or grouping.");
        }

        if (spec.Command == "symbols" && spec.SymbolId is not null)
        {
            throw new SemanticQueryException("invalid_arguments", "The symbols command does not accept symbol_id.");
        }
    }

    private static SemanticQuerySpec ParseQuery(string command, JsonElement query)
    {
        RequireKind(query, JsonValueKind.Object, "The query value must be a JSON object.");
        RejectDuplicateProperties(query, "query");
        RejectUnknown(query, QueryProperties, "query");

        if (command is not ("symbols" or "usage-summary")
            && query.TryGetProperty("search", out _))
        {
            throw new SemanticQueryException(
                "invalid_request",
                $"The '{command}' command does not accept search.");
        }

        if (command == "signature" && query.TryGetProperty("limit", out _))
        {
            throw new SemanticQueryException(
                "invalid_request",
                "The signature command does not accept limit.");
        }

        if (command != "usage-summary" && query.TryGetProperty("group_by", out _))
        {
            throw new SemanticQueryException(
                "invalid_request",
                $"The '{command}' command does not accept group_by.");
        }

        if (command != "hierarchy" && query.TryGetProperty("direction", out _))
        {
            throw new SemanticQueryException(
                "invalid_request",
                $"The '{command}' command does not accept direction.");
        }

        var filters = query.TryGetProperty("filters", out var filterElement)
            ? ParseFilters(filterElement)
            : null;
        var groupBy = query.TryGetProperty("group_by", out var groupElement)
            ? ParseGroupBy(groupElement)
            : null;
        return new SemanticQuerySpec(
            command,
            OptionalString(query, "search"),
            OptionalString(query, "symbol_id"),
            filters,
            OptionalString(query, "direction"),
            groupBy,
            OptionalInt(query, "limit", SemanticQueryProtocol.DefaultLimit),
            OptionalString(query, "cursor"),
            OptionalString(query, "snapshot_id"));
    }

    private static SemanticQueryFilters ParseFilters(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Object, "filters must be a JSON object.");
        RejectDuplicateProperties(element, "filters");
        RejectUnknown(element, FilterProperties, "filters");
        var kind = OptionalString(element, "kind");
        if (kind is not null && !SemanticQueryKinds.IsKnown(kind))
        {
            throw new SemanticQueryException("invalid_request", $"Unknown declaration kind '{kind}'.");
        }

        return new SemanticQueryFilters(
            OptionalString(element, "path"),
            OptionalString(element, "project"),
            element.TryGetProperty("namespace", out var namespaceElement)
                ? ReadString(namespaceElement, "namespace", allowEmpty: true)
                : null,
            kind);
    }

    private static IReadOnlyList<string> ParseGroupBy(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array, "group_by must be an array.");
        var values = element.EnumerateArray()
            .Select(value => RequiredString(value, "group_by entry"))
            .ToArray();
        if (values.Length == 0
            || values.Length > 2
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length
            || values.Any(value => value is not ("project" or "namespace")))
        {
            throw new SemanticQueryException("invalid_request", "group_by must contain project and/or namespace once each.");
        }

        return values;
    }

    private static void RejectUnknown(JsonElement element, IReadOnlySet<string> allowed, string context)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new SemanticQueryException("invalid_request", $"Unknown {context} field '{property.Name}'.");
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string context)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new SemanticQueryException(
                    "invalid_request",
                    $"Duplicate {context} field '{property.Name}'.");
            }
        }
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new SemanticQueryException("invalid_request", $"'{propertyName}' must be a string.");
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SemanticQueryException("invalid_request", $"'{propertyName}' cannot be empty.");
        }

        return value!;
    }

    private static string RequiredPropertyString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var element)
            ? RequiredString(element, propertyName)
            : throw new SemanticQueryException("invalid_request", $"Missing '{propertyName}'.");

    private static string ReadString(
        JsonElement element,
        string propertyName,
        bool allowEmpty)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new SemanticQueryException("invalid_request", $"'{propertyName}' must be a string.");
        }

        var value = element.GetString() ?? string.Empty;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value))
            || (allowEmpty && value.Length > 0 && string.IsNullOrWhiteSpace(value)))
        {
            throw new SemanticQueryException("invalid_request", $"'{propertyName}' cannot be empty.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement parent, string propertyName) =>
        !parent.TryGetProperty(propertyName, out var element)
            ? null
            : element.ValueKind == JsonValueKind.Null
                ? null
                : RequiredString(element, propertyName);

    private static int RequiredInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var element)
            ? ReadInt(element, propertyName)
            : throw new SemanticQueryException("invalid_request", $"Missing '{propertyName}'.");

    private static int OptionalInt(JsonElement parent, string propertyName, int defaultValue) =>
        !parent.TryGetProperty(propertyName, out var element)
            ? defaultValue
            : ReadInt(element, propertyName);

    private static bool OptionalBool(JsonElement parent, string propertyName, bool defaultValue) =>
        !parent.TryGetProperty(propertyName, out var element)
            ? defaultValue
            : element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? element.GetBoolean()
                : throw new SemanticQueryException("invalid_request", $"'{propertyName}' must be a boolean.");

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new SemanticQueryException("invalid_request", $"'{propertyName}' must be a 32-bit integer.");
        }

        return value;
    }

    private static Guid RequiredGuid(JsonElement parent, string propertyName)
    {
        var value = RequiredPropertyString(parent, propertyName);
        if (!Guid.TryParse(value, out var result) || result == Guid.Empty)
        {
            throw new SemanticQueryException("invalid_request", $"'{propertyName}' must be a non-empty GUID.");
        }

        return result;
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind, string message)
    {
        if (element.ValueKind != kind)
        {
            throw new SemanticQueryException("invalid_request", message);
        }
    }

    private static void ValidateTimeout(int milliseconds)
    {
        if (milliseconds <= 0 || milliseconds > SemanticQueryProtocol.MaximumTimeoutMilliseconds)
        {
            throw new SemanticQueryException(
                "invalid_request",
                $"timeout_ms must be between 1 and {SemanticQueryProtocol.MaximumTimeoutMilliseconds}.");
        }
    }
}
