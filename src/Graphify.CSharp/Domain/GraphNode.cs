using System.Collections.Immutable;

namespace Graphify.CSharp.Domain;

public enum GraphNodeKind
{
    Project,
    Namespace,
    Type,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    ExternalRoot,
}

public sealed class GraphNode
{
    public GraphNode(
        string id,
        GraphNodeKind kind,
        string label,
        string symbolKey,
        IEnumerable<SourceLocation>? sourceLocations = null,
        IEnumerable<KeyValuePair<string, string>>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolKey);

        Id = id;
        Kind = kind;
        Label = label;
        SymbolKey = symbolKey;
        SourceLocations = NormalizeLocations(sourceLocations);
        Properties = NormalizeProperties(properties);
    }

    public string Id { get; }

    public GraphNodeKind Kind { get; }

    public string Label { get; }

    public string SymbolKey { get; }

    public ImmutableArray<SourceLocation> SourceLocations { get; }

    public ImmutableDictionary<string, string> Properties { get; }

    public static GraphNode ForSymbol(
        SymbolIdentity symbol,
        IEnumerable<SourceLocation>? sourceLocations = null,
        IEnumerable<KeyValuePair<string, string>>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var kind = symbol.Kind switch
        {
            SymbolKind.Namespace => GraphNodeKind.Namespace,
            SymbolKind.Type => GraphNodeKind.Type,
            SymbolKind.Method => GraphNodeKind.Method,
            SymbolKind.Constructor => GraphNodeKind.Constructor,
            SymbolKind.Property => GraphNodeKind.Property,
            SymbolKind.Field => GraphNodeKind.Field,
            SymbolKind.Event => GraphNodeKind.Event,
            _ => throw new ArgumentOutOfRangeException(nameof(symbol), symbol.Kind, "Unknown symbol kind."),
        };

        return new GraphNode(NodeId.ForSymbol(symbol), kind, symbol.DisplayName, symbol.CanonicalKey, sourceLocations, properties);
    }

    public GraphNode Merge(GraphNode other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(Id, other.Id, StringComparison.Ordinal)
            || Kind != other.Kind
            || !string.Equals(SymbolKey, other.SymbolKey, StringComparison.Ordinal)
            || !string.Equals(Label, other.Label, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot merge different graph nodes with id '{Id}'.");
        }

        var properties = Properties.ToBuilder();
        foreach (var property in other.Properties)
        {
            if (properties.TryGetValue(property.Key, out var current)
                && !string.Equals(current, property.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Graph node '{Id}' has conflicting property '{property.Key}'.");
            }

            properties[property.Key] = property.Value;
        }

        return new GraphNode(
            Id,
            Kind,
            Label,
            SymbolKey,
            SourceLocations.Concat(other.SourceLocations),
            properties);
    }

    private static ImmutableArray<SourceLocation> NormalizeLocations(IEnumerable<SourceLocation>? locations)
    {
        return (locations ?? Array.Empty<SourceLocation>())
            .Distinct()
            .OrderBy(location => location.FilePath, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToImmutableArray();
    }

    private static ImmutableDictionary<string, string> NormalizeProperties(IEnumerable<KeyValuePair<string, string>>? properties)
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in properties ?? Array.Empty<KeyValuePair<string, string>>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(property.Key);
            ArgumentNullException.ThrowIfNull(property.Value);

            if (result.TryGetValue(property.Key, out var current)
                && !string.Equals(current, property.Value, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Property '{property.Key}' was supplied with conflicting values.", nameof(properties));
            }

            result[property.Key] = property.Value;
        }

        return result.ToImmutable();
    }
}
