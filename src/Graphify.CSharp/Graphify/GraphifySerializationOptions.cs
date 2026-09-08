using System.Collections.Immutable;

namespace Graphify.CSharp.Graphify;

public sealed class GraphifySerializationOptions
{
    public GraphifySerializationOptions(
        string testNamespaceSegment = "Tests",
        IEnumerable<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testNamespaceSegment);
        TestNamespaceSegment = testNamespaceSegment.Trim();
        Diagnostics = (diagnostics ?? Array.Empty<string>()).ToImmutableArray();
    }

    public string TestNamespaceSegment { get; }

    public ImmutableArray<string> Diagnostics { get; }
}
