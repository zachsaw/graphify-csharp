using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class TransitionEventJournalTests
{
    [Fact]
    public void Coalesces_repeated_paths_and_keeps_the_strongest_change_kind()
    {
        var path = Path.Combine(Path.GetTempPath(), "transition", "Source.cs");
        var journal = new TransitionEventJournal(capacity: 4);

        Assert.True(journal.TryRecord(new FileChangeEvent(FileChangeKind.Changed, path)));
        Assert.True(journal.TryRecord(new FileChangeEvent(
            FileChangeKind.Created,
            path,
            RequiresColdReconciliation: true)));

        var change = Assert.Single(journal.Drain());
        Assert.Equal(FileChangeKind.Created, change.Kind);
        Assert.True(change.RequiresColdReconciliation);
        Assert.Equal(IncrementalPaths.CanonicalAbsolutePath(path), change.Path);
    }

    [Fact]
    public void Preserves_both_rename_endpoints_and_bounds_unique_paths()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), "transition", "Old.cs");
        var newPath = Path.Combine(Path.GetTempPath(), "transition", "New.cs");
        var otherPath = Path.Combine(Path.GetTempPath(), "transition", "Other.cs");
        var journal = new TransitionEventJournal(capacity: 1);

        Assert.True(journal.TryRecord(new FileChangeEvent(FileChangeKind.Renamed, newPath, oldPath)));
        Assert.False(journal.TryRecord(new FileChangeEvent(FileChangeKind.Created, otherPath)));

        var change = Assert.Single(journal.Drain());
        Assert.Equal(FileChangeKind.Renamed, change.Kind);
        Assert.Equal(IncrementalPaths.CanonicalAbsolutePath(newPath), change.Path);
        Assert.Equal(IncrementalPaths.CanonicalAbsolutePath(oldPath), change.OldPath);
        Assert.Empty(journal.Drain());
    }
}
