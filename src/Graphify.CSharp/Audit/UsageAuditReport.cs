using System.Collections.Immutable;

namespace Graphify.CSharp.Audit;

public sealed class UsageAuditReport
{
    public UsageAuditReport(IEnumerable<UsageAuditResult> results)
    {
        Results = (results ?? throw new ArgumentNullException(nameof(results)))
            .OrderBy(result => result.Target.Identity.CanonicalKey, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public ImmutableArray<UsageAuditResult> Results { get; }

    public bool TryGet(string targetNodeId, out UsageAuditResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        result = Results.FirstOrDefault(item => item.Target.Node.Id == targetNodeId)!;
        return result is not null;
    }
}
