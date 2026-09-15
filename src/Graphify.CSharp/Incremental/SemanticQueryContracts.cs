using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mediator.Switch;

namespace Graphify.CSharp.Incremental;

internal static class SemanticQueryProtocol
{
    public const int CurrentVersion = 1;
    public const string SchemaVersion = "graphify-csharp/query/v1";
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public const int MaximumJsonDepth = 32;
    public const int MaximumCursorBytes = 8 * 1024;
    public const int MaximumConcurrentRequests = 16;
    public const int DefaultLimit = 100;
    public const int MaximumLimit = 1000;
    public const int DefaultTimeoutMilliseconds = 10 * 60 * 1000;
    public const int MaximumTimeoutMilliseconds = 60 * 60 * 1000;
    public const int TransportTimeoutMilliseconds = 5 * 1000;

    private const string EndpointPrefix = "gcs-";
    private const int EndpointHashLength = 24;

    public static string ForSession(Guid sessionId, string stateDirectory)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A semantic endpoint requires a session ID.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var key = $"semantic\u001F{sessionId:D}\u001F{IncrementalPaths.CanonicalAbsolutePath(stateDirectory)}";
        return EndpointPrefix + IncrementalHashing.Sha256(key)[..EndpointHashLength];
    }

    public static bool IsValidEndpoint(string? endpoint)
    {
        if (endpoint is null
            || endpoint.Length != EndpointPrefix.Length + EndpointHashLength
            || !endpoint.StartsWith(EndpointPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return endpoint[EndpointPrefix.Length..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

internal static class SemanticQueryCommands
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "symbols",
        "signature",
        "usages",
        "callers",
        "hierarchy",
        "arguments",
        "usage-summary",
        "export",
    };

    public static bool IsExact(string command) => command is
        "signature" or "usages" or "callers" or "hierarchy" or "arguments";
}

internal static class SemanticQueryKinds
{
    private static readonly IReadOnlySet<string> Values = new HashSet<string>(StringComparer.Ordinal)
    {
        "namespace",
        "class",
        "interface",
        "struct",
        "enum",
        "delegate",
        "record",
        "record_struct",
        "method",
        "constructor",
        "static_constructor",
        "destructor",
        "operator",
        "conversion",
        "userdefinedoperator",
        "localfunction",
        "property",
        "indexer",
        "field",
        "enum_member",
        "event",
        "parameter",
        "local",
        "local_constant",
        "range_variable",
        "type_parameter",
        "label",
        "alias",
        "union",
    };

    public static bool IsKnown(string value) => Values.Contains(value);
}

internal sealed record SemanticQueryFilters(
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("project")] string? Project = null,
    [property: JsonPropertyName("namespace")] string? Namespace = null,
    [property: JsonPropertyName("kind")] string? Kind = null)
{
    [JsonIgnore]
    public bool IsEmpty => Path is null && Project is null && Namespace is null && Kind is null;

    [JsonIgnore]
    public string CanonicalKey => string.Join(
        '\u001F',
        $"path={(Path is null ? "<omitted>" : Path)}",
        $"project={(Project is null ? "<omitted>" : Project)}",
        $"namespace={(Namespace is null ? "<omitted>" : Namespace)}",
        $"kind={(Kind is null ? "<omitted>" : Kind)}");
}

internal sealed record SemanticQuerySpec(
    string Command,
    string? Search = null,
    string? SymbolId = null,
    SemanticQueryFilters? Filters = null,
    string? Direction = null,
    IReadOnlyList<string>? GroupBy = null,
    int Limit = SemanticQueryProtocol.DefaultLimit,
    string? Cursor = null,
    string? SnapshotId = null,
    string? OutputPath = null)
{
    public SemanticQueryFilters EffectiveFilters => Filters ?? new SemanticQueryFilters();

    public IReadOnlyList<string> EffectiveGroupBy => GroupBy ?? Array.Empty<string>();

    public string CanonicalQueryKey => string.Join(
        '\u001F',
        Command,
        Search ?? string.Empty,
        SymbolId ?? string.Empty,
        EffectiveFilters.CanonicalKey,
        Direction ?? string.Empty,
        string.Join(',', EffectiveGroupBy));

    public string QueryHash => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalQueryKey)))
        .ToLowerInvariant();
}

internal sealed record SemanticQuerySnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("target_generation")] long TargetGeneration,
    [property: JsonPropertyName("indexed_generation")] long IndexedGeneration,
    [property: JsonPropertyName("event_generation")] long EventGeneration);

internal sealed record SemanticQueryScope(
    [property: JsonPropertyName("analysis_key")] string AnalysisKey,
    [property: JsonPropertyName("filters")] SemanticQueryFilters Filters,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("input_discovery_complete")] bool InputDiscoveryComplete,
    [property: JsonPropertyName("has_diagnostics")] bool HasDiagnostics,
    [property: JsonPropertyName("operation_complete")] bool OperationComplete,
    [property: JsonPropertyName("operation_diagnostics")] IReadOnlyList<string> OperationDiagnostics);

internal sealed record SemanticQueryPage(
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("continuation_unavailable")] bool ContinuationUnavailable = false);

internal sealed record SemanticQueryDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

internal sealed record SemanticQueryError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

internal sealed record SemanticExportResult(
    [property: JsonPropertyName("output_path")] string OutputPath,
    [property: JsonPropertyName("output_digest")] string OutputDigest,
    [property: JsonPropertyName("nodes")] int NodeCount,
    [property: JsonPropertyName("edges")] int EdgeCount);

internal sealed record SemanticQueryResponse(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("session_id")] Guid? SessionId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] SemanticQueryError? Error,
    [property: JsonPropertyName("snapshot")] SemanticQuerySnapshot? Snapshot,
    [property: JsonPropertyName("scope")] SemanticQueryScope? Scope,
    [property: JsonPropertyName("items")] IReadOnlyList<JsonElement> Items,
    [property: JsonPropertyName("page")] SemanticQueryPage? Page,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<SemanticQueryDiagnostic> Diagnostics,
    [property: JsonPropertyName("diagnostics_truncated")] bool DiagnosticsTruncated,
    [property: JsonPropertyName("export")] SemanticExportResult? Export)
{
    public static SemanticQueryResponse Failure(
        Guid? sessionId,
        string mode,
        string command,
        string errorCode,
        string message,
        string? analysisKey = null) =>
        new(
            SemanticQueryProtocol.SchemaVersion,
            SemanticQueryProtocol.CurrentVersion,
            sessionId,
            mode,
            command,
            Success: false,
            new SemanticQueryError(errorCode, message),
            Snapshot: null,
            analysisKey is null
                ? null
                : new SemanticQueryScope(
                    analysisKey,
                    new SemanticQueryFilters(),
                    "observed_static",
                    InputDiscoveryComplete: false,
                    HasDiagnostics: true,
                    OperationComplete: false,
                    OperationDiagnostics: Array.Empty<string>()),
            Array.Empty<JsonElement>(),
            Page: null,
            Array.Empty<SemanticQueryDiagnostic>(),
            DiagnosticsTruncated: false,
            Export: null);

    public static SemanticQueryResponse SuccessResponse(
        Guid? sessionId,
        string mode,
        string command,
        SemanticQuerySnapshot snapshot,
        SemanticQueryScope scope,
        IReadOnlyList<JsonElement> items,
        SemanticQueryPage page,
        IReadOnlyList<SemanticQueryDiagnostic> diagnostics,
        bool diagnosticsTruncated = false,
        SemanticExportResult? export = null) =>
        new(
            SemanticQueryProtocol.SchemaVersion,
            SemanticQueryProtocol.CurrentVersion,
            sessionId,
            mode,
            command,
            Success: true,
            Error: null,
            snapshot,
            scope,
            items,
            page,
            diagnostics,
            diagnosticsTruncated,
            export);
}

internal sealed record SemanticQueryWireRequest(
    int ProtocolVersion,
    Guid SessionId,
    string Command,
    string AnalysisKey,
    int TimeoutMilliseconds,
    SemanticQuerySpec Spec);

internal sealed record ExecuteSemanticQueryRequest(
    SemanticQuerySpec Specification,
    SemanticEvidenceView View,
    SemanticQueryEngine? Engine = null) : IRequest<SemanticQueryResponse>;

internal sealed class ExecuteSemanticQueryHandler :
    IRequestHandler<ExecuteSemanticQueryRequest, SemanticQueryResponse>
{
    public Task<SemanticQueryResponse> Handle(
        ExecuteSemanticQueryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((request.Engine ?? new SemanticQueryEngine()).Execute(
            request.Specification,
            request.View,
            cancellationToken));
    }
}

internal sealed class SemanticQueryException : Exception
{
    public SemanticQueryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class SemanticQueryJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = SemanticQueryProtocol.MaximumJsonDepth,
    };

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, SerializerOptions);
}
