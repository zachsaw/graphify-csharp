using System.Text.Json.Serialization;

namespace Graphify.CSharp.Graphify;

public sealed class GraphifyExtractionDocument
{
    [JsonPropertyName("directed")]
    public bool Directed { get; init; } = true;

    [JsonPropertyName("multigraph")]
    public bool Multigraph { get; init; } = true;

    [JsonPropertyName("nodes")]
    public required IReadOnlyList<GraphifyNodeDto> Nodes { get; init; }

    [JsonPropertyName("edges")]
    public required IReadOnlyList<GraphifyEdgeDto> Edges { get; init; }

    [JsonPropertyName("hyperedges")]
    public required IReadOnlyList<object> Hyperedges { get; init; }

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }

    [JsonPropertyName("graphify_csharp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphifyCSharpMetadataDto? GraphifyCSharp { get; init; }
}

public sealed class GraphifyNodeDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("file_type")]
    public required string FileType { get; init; }

    [JsonPropertyName("source_file")]
    public required string SourceFile { get; init; }

    [JsonPropertyName("source_location")]
    public string? SourceLocation { get; init; }

    [JsonPropertyName("source_locations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GraphifySourceLocationDto>? SourceLocations { get; init; }

    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

public sealed class GraphifyEdgeDto
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("relation")]
    public required string Relation { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("confidence_score")]
    public double ConfidenceScore { get; init; }

    [JsonPropertyName("source_file")]
    public required string SourceFile { get; init; }

    [JsonPropertyName("source_location")]
    public string? SourceLocation { get; init; }

    [JsonPropertyName("source_locations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GraphifySourceLocationDto>? SourceLocations { get; init; }

    [JsonPropertyName("weight")]
    public double Weight { get; init; } = 1.0;
}

public sealed class GraphifySourceLocationDto
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("column")]
    public int Column { get; init; }
}

public sealed class GraphifyCSharpMetadataDto
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
