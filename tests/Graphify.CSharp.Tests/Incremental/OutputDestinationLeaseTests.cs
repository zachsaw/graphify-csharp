using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class OutputDestinationLeaseTests
{
    [Fact]
    public void Only_one_process_can_own_a_destination_and_released_lock_files_are_reusable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-output-lease-tests",
            Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "graphify-out", "csharp.json");

        try
        {
            using var first = OutputDestinationLease.Acquire(outputPath);
            Assert.True(OutputDestinationLease.IsHeld(first.Path));
            var conflict = Assert.Throws<InvalidOperationException>(
                () => OutputDestinationLease.Acquire(outputPath));
            Assert.Contains("already owned", conflict.Message, StringComparison.Ordinal);

            var leasePath = first.Path;
            first.Dispose();
            Assert.False(OutputDestinationLease.IsHeld(leasePath));
            // Stable lock files are intentionally retained so late cleanup
            // from an old owner cannot delete a successor's lock path.
            Assert.True(File.Exists(leasePath));

            using var second = OutputDestinationLease.Acquire(outputPath);
            Assert.True(OutputDestinationLease.IsHeld(second.Path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Case_equivalent_output_paths_share_the_lease_when_the_filesystem_treats_them_as_one_path()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-output-lease-case-tests",
            Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "graphify-out", "csharp.json");
        var caseAlias = Path.Combine(root, "GRAPHIFY-OUT", "CSHARP.JSON");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "existing output");
            // Do not assume that every supported volume is case-insensitive.
            // On a case-sensitive filesystem these are genuinely different
            // destinations and the ownership contract must remain independent.
            if (!File.Exists(caseAlias))
            {
                return;
            }

            using var first = OutputDestinationLease.Acquire(outputPath);
            Assert.Throws<InvalidOperationException>(
                () => OutputDestinationLease.Acquire(caseAlias));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
