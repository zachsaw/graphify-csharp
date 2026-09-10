namespace Graphify.CSharp.Incremental;

internal static class IncrementalRefreshControlChannel
{
    public static string ForRequest(RefreshRequestIdentity request, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpointKey = string.Join(
            '\u001F',
            $"request={request.Digest}",
            $"output={OutputPathIdentity(outputPath)}");
        var endpointDigest = IncrementalHashing.Sha256(endpointKey);
        // Unix domain socket-backed named pipes have a platform-specific path
        // limit. The full request digest remains in the lease and session
        // identity; this truncated endpoint token is 96 bits and stays below
        // that limit even on macOS temporary directories. Output identity is
        // included because a watcher owns one publication target.
        return $"gcf-{endpointDigest[..24]}";
    }

    public static string OutputPathIdentity(string outputPath) =>
        IncrementalHashing.Sha256(IncrementalPaths.CanonicalAbsolutePath(outputPath));
}
