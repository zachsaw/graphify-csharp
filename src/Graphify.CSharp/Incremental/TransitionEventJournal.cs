namespace Graphify.CSharp.Incremental;

/// <summary>
/// Coalesces file-system events observed while the evaluated project boundary
/// is being replaced. The host owns synchronization; this type intentionally
/// has no background worker or file-system access.
/// </summary>
internal sealed class TransitionEventJournal
{
    private readonly int _capacity;
    private readonly Dictionary<EventKey, FileChangeEvent> _events = new(EventKeyComparer.Instance);

    public TransitionEventJournal(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public bool TryRecord(FileChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var normalized = Normalize(change);
        var key = new EventKey(normalized.Path, normalized.OldPath);
        if (_events.TryGetValue(key, out var previous))
        {
            _events[key] = Merge(previous, normalized);
            return true;
        }

        if (_events.Count >= _capacity)
        {
            return false;
        }

        _events.Add(key, normalized);
        return true;
    }

    public IReadOnlyList<FileChangeEvent> Drain()
    {
        if (_events.Count == 0)
        {
            return Array.Empty<FileChangeEvent>();
        }

        var drained = _events.Values
            .OrderBy(change => change.Path, IncrementalPaths.PathComparer)
            .ThenBy(change => change.OldPath, IncrementalPaths.PathComparer)
            .ThenBy(change => change.Kind)
            .ToArray();
        _events.Clear();
        return drained;
    }

    private static FileChangeEvent Normalize(FileChangeEvent change) =>
        change with
        {
            Path = IncrementalPaths.CanonicalAbsolutePath(change.Path),
            OldPath = string.IsNullOrWhiteSpace(change.OldPath)
                ? null
                : IncrementalPaths.CanonicalAbsolutePath(change.OldPath),
        };

    private static FileChangeEvent Merge(FileChangeEvent previous, FileChangeEvent current) =>
        previous with
        {
            Kind = MergeKind(previous.Kind, current.Kind),
            RequiresColdReconciliation = previous.RequiresColdReconciliation
                || current.RequiresColdReconciliation,
            IsDirectory = previous.IsDirectory == current.IsDirectory
                ? previous.IsDirectory
                : previous.IsDirectory is null
                    ? current.IsDirectory
                    : current.IsDirectory is null
                        ? previous.IsDirectory
                        : null,
        };

    private static FileChangeKind MergeKind(FileChangeKind previous, FileChangeKind current)
    {
        if (previous == FileChangeKind.Deleted || current == FileChangeKind.Deleted)
        {
            return FileChangeKind.Deleted;
        }

        if (previous == FileChangeKind.Renamed || current == FileChangeKind.Renamed)
        {
            return FileChangeKind.Renamed;
        }

        if (previous == FileChangeKind.Created || current == FileChangeKind.Created)
        {
            return FileChangeKind.Created;
        }

        return FileChangeKind.Changed;
    }

    private readonly record struct EventKey(string Path, string? OldPath);

    private sealed class EventKeyComparer : IEqualityComparer<EventKey>
    {
        public static EventKeyComparer Instance { get; } = new();

        public bool Equals(EventKey first, EventKey second) =>
            string.Equals(first.Path, second.Path, IncrementalPaths.PathComparison)
            && string.Equals(first.OldPath, second.OldPath, IncrementalPaths.PathComparison);

        public int GetHashCode(EventKey key) => HashCode.Combine(
            IncrementalPaths.PathComparer.GetHashCode(key.Path),
            key.OldPath is null
                ? 0
                : IncrementalPaths.PathComparer.GetHashCode(key.OldPath));
    }
}
