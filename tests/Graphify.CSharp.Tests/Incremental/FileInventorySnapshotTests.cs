using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class FileInventorySnapshotTests
{
    [Fact]
    public async Task Backup_inventory_detects_missing_exact_inputs_and_new_candidates()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dependencyPath = Path.Combine(root, "obj", "generator.data");
            var candidatePath = Path.Combine(root, "src", "New.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(dependencyPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(candidatePath)!);
            await File.WriteAllTextAsync(dependencyPath, "generator input");

            var snapshot = WatcherInputSnapshot.CreateForTests(
                knownSources: [],
                knownDependencies: [dependencyPath],
                sourceProjects: [],
                discoveryRoots: [root],
                outputPath: Path.Combine(root, "graphify-out", "csharp.json"),
                cachePath: Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json"));
            var scanner = new FileInventoryScanner();
            var before = await scanner.ScanAsync([root], root, inputSnapshot: snapshot);

            await File.WriteAllTextAsync(candidatePath, "public sealed class New { }");
            File.Delete(dependencyPath);
            var after = await scanner.ScanAsync([root], root, inputSnapshot: snapshot);

            var changes = after.CompareToEvents(before);
            Assert.Contains(
                changes,
                change => change.Kind == FileChangeKind.Deleted
                    && string.Equals(change.Path, dependencyPath, IncrementalPaths.PathComparison));
            Assert.Contains(
                changes,
                change => change.Kind == FileChangeKind.Created
                    && string.Equals(change.Path, candidatePath, IncrementalPaths.PathComparison));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-inventory-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
