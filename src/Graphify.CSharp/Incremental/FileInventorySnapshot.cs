namespace Graphify.CSharp.Incremental;

internal sealed record FileInventoryEntry(
    string FullPath,
    SourceFingerprint Fingerprint);

internal sealed class FileInventorySnapshot
{
    public FileInventorySnapshot(IEnumerable<FileInventoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Entries = entries
            .Select(entry => new FileInventoryEntry(
                IncrementalPaths.CanonicalAbsolutePath(entry.FullPath),
                entry.Fingerprint ?? throw new ArgumentException("An inventory entry must have a fingerprint.", nameof(entries))))
            .OrderBy(entry => entry.FullPath, PathComparer)
            .ToDictionary(entry => entry.FullPath, PathComparer);
    }

    public IReadOnlyDictionary<string, FileInventoryEntry> Entries { get; }

    public IReadOnlyList<string> CompareTo(FileInventorySnapshot? previous)
    {
        if (previous is null)
        {
            return Entries.Keys.OrderBy(path => path, PathComparer).ToArray();
        }

        var changed = new HashSet<string>(PathComparer);
        foreach (var (path, entry) in Entries)
        {
            if (!previous.Entries.TryGetValue(path, out var oldEntry)
                || oldEntry.Fingerprint.CompareTo(entry.Fingerprint) == FingerprintComparison.Different)
            {
                changed.Add(path);
            }
        }

        foreach (var path in previous.Entries.Keys)
        {
            if (!Entries.ContainsKey(path))
            {
                changed.Add(path);
            }
        }

        return changed.OrderBy(path => path, PathComparer).ToArray();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
