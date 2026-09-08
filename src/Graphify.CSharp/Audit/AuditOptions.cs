using System.Collections.Immutable;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Audit;

public sealed class AuditOptions
{
    public AuditOptions(
        NamespaceTestPolicy? testNamespacePolicy = null,
        IEnumerable<string>? productionRootNodeIds = null)
    {
        TestNamespacePolicy = testNamespacePolicy ?? new NamespaceTestPolicy();
        ProductionRootNodeIds = (productionRootNodeIds ?? Array.Empty<string>())
            .Select(nodeId => nodeId.Trim())
            .Where(nodeId => nodeId.Length > 0)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    public NamespaceTestPolicy TestNamespacePolicy { get; }

    public ImmutableHashSet<string> ProductionRootNodeIds { get; }
}
