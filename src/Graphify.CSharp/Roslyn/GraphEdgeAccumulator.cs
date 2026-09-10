using System.Collections.Immutable;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Roslyn;

internal sealed class GraphEdgeAccumulator
{
    private readonly Dictionary<EdgeKey, EdgeState> _edges = new();

    public int Count => _edges.Count;

    public void Add(
        string sourceId,
        string targetId,
        GraphRelation relation,
        EvidenceKind evidence,
        double confidence,
        SourceLocation? location)
    {
        var key = new EdgeKey(sourceId, targetId, relation, evidence);
        if (_edges.TryGetValue(key, out var state))
        {
            state.Add(confidence, location);
            _edges[key] = state;
            return;
        }

        _edges.Add(key, new EdgeState(confidence, location));
    }

    public void Add(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        var key = new EdgeKey(edge.SourceId, edge.TargetId, edge.Relation, edge.Evidence);
        if (_edges.TryGetValue(key, out var state))
        {
            state.Add(edge.Confidence, edge.SourceLocations);
            _edges[key] = state;
            return;
        }

        _edges.Add(key, new EdgeState(edge.Confidence, edge.SourceLocations));
    }

    public ImmutableArray<GraphEdge> ToImmutableArray()
    {
        var result = ImmutableArray.CreateBuilder<GraphEdge>(_edges.Count);
        foreach (var (key, state) in _edges)
        {
            result.Add(new GraphEdge(
                key.SourceId,
                key.TargetId,
                key.Relation,
                key.Evidence,
                state.Confidence,
                state.Locations()));
        }

        return result.MoveToImmutable();
    }

    private readonly record struct EdgeKey(
        string SourceId,
        string TargetId,
        GraphRelation Relation,
        EvidenceKind Evidence);

    private struct EdgeState
    {
        private SourceLocation? _singleLocation;
        private HashSet<SourceLocation>? _locations;

        public EdgeState(double confidence, SourceLocation? location)
        {
            Confidence = confidence;
            _singleLocation = location;
            _locations = null;
        }

        public EdgeState(double confidence, IEnumerable<SourceLocation> locations)
            : this(confidence, location: null)
        {
            Add(confidence, locations);
        }

        public double Confidence { get; private set; }

        public void Add(double confidence, SourceLocation? location)
        {
            Confidence = Math.Max(Confidence, confidence);
            if (location is null)
            {
                return;
            }

            if (_locations is not null)
            {
                _locations.Add(location);
                return;
            }

            if (_singleLocation is null)
            {
                _singleLocation = location;
                return;
            }

            if (_singleLocation.Equals(location))
            {
                return;
            }

            _locations = new HashSet<SourceLocation>
            {
                _singleLocation,
                location,
            };
            _singleLocation = null;
        }

        public void Add(double confidence, IEnumerable<SourceLocation> locations)
        {
            var hadLocation = false;
            foreach (var location in locations)
            {
                hadLocation = true;
                Add(confidence, location);
            }

            if (!hadLocation)
            {
                Confidence = Math.Max(Confidence, confidence);
            }
        }

        public IReadOnlyCollection<SourceLocation> Locations()
        {
            if (_locations is not null)
            {
                return _locations;
            }

            return _singleLocation is null
                ? Array.Empty<SourceLocation>()
                : [_singleLocation];
        }
    }
}
