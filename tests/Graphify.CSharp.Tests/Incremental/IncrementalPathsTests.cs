using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class IncrementalPathsTests
{
    [Fact]
    public void Physical_path_resolution_follows_a_directory_alias_for_a_missing_leaf()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-path-tests",
            Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var alias = Path.Combine(root, "alias");
        try
        {
            Directory.CreateDirectory(target);
            try
            {
                Directory.CreateSymbolicLink(alias, target);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(exception.Message);
            }
            catch (PlatformNotSupportedException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(exception.Message);
            }

            Assert.True(IncrementalPaths.TryResolvePhysicalPath(target, out var resolvedTarget));
            Assert.True(
                IncrementalPaths.TryResolvePhysicalPath(
                    Path.Combine(alias, "missing.json"),
                    out var resolved));
            Assert.Equal(
                IncrementalPaths.CanonicalAbsolutePath(Path.Combine(resolvedTarget, "missing.json")),
                resolved);
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
