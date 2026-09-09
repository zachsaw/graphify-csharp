namespace Graphify.CSharp.Incremental;

internal static class IncrementalRefreshControlChannel
{
    public static string ForRequest(RefreshRequestIdentity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Unix domain socket-backed named pipes have a platform-specific path
        // limit. The full request digest remains in the lease and session
        // identity; this truncated routing token is 96 bits and stays below
        // that limit even on macOS temporary directories.
        return $"gcf-{request.Digest[..24]}";
    }
}
