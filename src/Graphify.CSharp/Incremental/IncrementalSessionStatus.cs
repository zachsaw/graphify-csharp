namespace Graphify.CSharp.Incremental;

internal enum IncrementalSessionStatus
{
    Created,
    Starting,
    Ready,
    Refreshing,
    Failed,
    Stopped,
}
