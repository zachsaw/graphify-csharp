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
                cachePath: Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json"),
                inputDiscoveryComplete: false);
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

    [Fact]
    public async Task Bootstrap_inventory_does_not_walk_unrelated_subtrees()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var inputPath = Path.Combine(root, "Fixture.csproj");
            var unrelatedPath = Path.Combine(root, "unrelated", "Noise.cs");
            await File.WriteAllTextAsync(inputPath, "<Project />");
            Directory.CreateDirectory(Path.GetDirectoryName(unrelatedPath)!);
            await File.WriteAllTextAsync(unrelatedPath, "public sealed class Noise { }");

            var snapshot = WatcherInputSnapshot.CreateBootstrap(
                [root],
                Path.Combine(root, "graphify-out", "csharp.json"),
                Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json"),
                knownInputPaths: [inputPath]);
            var inventory = await new FileInventoryScanner().ScanAsync(
                [root],
                root,
                inputSnapshot: snapshot);

            Assert.Contains(
                IncrementalPaths.CanonicalAbsolutePath(inputPath),
                inventory.Entries.Keys);
            Assert.DoesNotContain(
                IncrementalPaths.CanonicalAbsolutePath(unrelatedPath),
                inventory.Entries.Keys);
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
