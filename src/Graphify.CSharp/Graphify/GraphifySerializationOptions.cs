using System.Collections.Immutable;

namespace Graphify.CSharp.Graphify;

public sealed class GraphifySerializationOptions
{
    public GraphifySerializationOptions(IEnumerable<string>? diagnostics = null)
    {
        Diagnostics = (diagnostics ?? Array.Empty<string>())
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(diagnostic => diagnostic.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public ImmutableArray<string> Diagnostics { get; }
}
