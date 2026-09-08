using System.Text.Json;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Graphify;

public sealed class GraphifyJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public string Serialize(
        GraphSnapshot graph,
        GraphifySerializationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new GraphifySerializationOptions();

        var document = new GraphifyExtractionDocument
        {
            Nodes = graph.Nodes.Select(ToNode).ToArray(),
            Edges = graph.Edges.Select(ToEdge).ToArray(),
            Hyperedges = Array.Empty<object>(),
            InputTokens = 0,
            OutputTokens = 0,
            GraphifyCSharp = ToMetadata(options),
        };

        return JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine;
    }

    private static GraphifyNodeDto ToNode(GraphNode node)
    {
        var locations = node.SourceLocations.Select(ToLocation).ToArray();
        var primary = node.SourceLocations.FirstOrDefault();
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["node_kind"] = SnakeCase(node.Kind.ToString()),
            ["symbol_key"] = node.SymbolKey,
        };
        foreach (var property in node.Properties)
        {
            properties[property.Key] = property.Value;
        }

        return new GraphifyNodeDto
        {
            Id = node.Id,
            Label = node.Label,
            FileType = "code",
            SourceFile = primary?.FilePath ?? string.Empty,
            SourceLocation = FormatLocation(primary),
            SourceLocations = locations.Length == 0 ? null : locations,
            Properties = properties,
        };
    }

    private static GraphifyEdgeDto ToEdge(GraphEdge edge)
    {
        var locations = edge.SourceLocations.Select(ToLocation).ToArray();
        var primary = edge.SourceLocations.FirstOrDefault();
        return new GraphifyEdgeDto
        {
            Source = edge.SourceId,
            Target = edge.TargetId,
            Relation = SnakeCase(edge.Relation.ToString()),
            Confidence = edge.Evidence.ToString().ToUpperInvariant(),
            ConfidenceScore = edge.Confidence,
            SourceFile = primary?.FilePath ?? string.Empty,
            SourceLocation = FormatLocation(primary),
            SourceLocations = locations.Length == 0 ? null : locations,
        };
    }

    private static GraphifyCSharpMetadataDto ToMetadata(GraphifySerializationOptions options) => new()
    {
        SchemaVersion = "csharp/v1",
        Diagnostics = options.Diagnostics,
    };

    private static GraphifySourceLocationDto ToLocation(SourceLocation location) => new()
    {
        File = location.FilePath,
        Line = location.Line,
        Column = location.Column,
    };

    private static string? FormatLocation(SourceLocation? location) => location is null ? null : $"L{location.Line}:C{location.Column}";

    private static string SnakeCase(string value)
    {
        var characters = new List<char>(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                characters.Add('_');
            }

            characters.Add(char.ToLowerInvariant(character));
        }

        return new string(characters.ToArray());
    }
}
