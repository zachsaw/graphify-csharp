using System.Text.Json;
using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalOutputPublisher
{
    private readonly GraphifyJsonSerializer _serializer;
    private readonly AtomicTextFileWriter _writer;

    public IncrementalOutputPublisher(
        GraphifyJsonSerializer? serializer = null,
        IAtomicCacheCommitter? committer = null)
    {
        _serializer = serializer ?? new GraphifyJsonSerializer();
        _writer = new AtomicTextFileWriter(committer);
    }

    public async Task<string> PublishAsync(
        string outputPath,
        GraphSnapshot graph,
        IEnumerable<string>? diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(graph);

        ValidateGraph(graph);
        var json = _serializer.Serialize(graph, new GraphifySerializationOptions(diagnostics));
        ValidateJson(json);
        await _writer.WriteAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
        return IncrementalHashing.Sha256(json);
    }

    private static void ValidateGraph(GraphSnapshot graph)
    {
        var nodeIds = graph.Nodes
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (nodeIds.Count != graph.Nodes.Length)
        {
            throw new InvalidDataException("The Graphify graph contains duplicate node identities.");
        }

        foreach (var edge in graph.Edges)
        {
            if (!nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId))
            {
                throw new InvalidDataException(
                    $"The Graphify graph contains an edge with a missing endpoint: '{edge.SourceId}' -> '{edge.TargetId}'.");
            }
        }
    }

    private static void ValidateJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Require(root, "directed", JsonValueKind.True);
        Require(root, "multigraph", JsonValueKind.True);
        Require(root, "nodes", JsonValueKind.Array);
        Require(root, "edges", JsonValueKind.Array);
        Require(root, "hyperedges", JsonValueKind.Array);
        if (!root.TryGetProperty("graphify_csharp", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The Graphify JSON is missing graphify_csharp metadata.");
        }

        Require(metadata, "schema_version", JsonValueKind.String);
    }

    private static void Require(JsonElement element, string propertyName, JsonValueKind kind)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != kind)
        {
            throw new InvalidDataException($"The Graphify JSON property '{propertyName}' is missing or invalid.");
        }
    }
}
