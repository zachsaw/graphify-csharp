using System.Collections.Immutable;
using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Audit;

public enum UsageClassification
{
    ProductionUsed,
    TestOnly,
    Mixed,
    ZeroReferences,
}

public enum AuditWarningKind
{
    PotentialDynamicReference,
    AmbiguousEvidence,
}

public sealed class UsageAuditResult
{
    public UsageAuditResult(
        SymbolDeclaration target,
        UsageClassification classification,
        IEnumerable<CallerUsage> callers,
        bool isConfiguredProductionRoot,
        IEnumerable<AuditWarningKind>? warnings = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Classification = classification;
        Callers = (callers ?? throw new ArgumentNullException(nameof(callers)))
            .OrderBy(caller => caller.CallerNodeId, StringComparer.Ordinal)
            .ToImmutableArray();
        IsConfiguredProductionRoot = isConfiguredProductionRoot;
        Warnings = (warnings ?? Array.Empty<AuditWarningKind>())
            .Distinct()
            .OrderBy(warning => warning)
            .ToImmutableArray();
    }

    public SymbolDeclaration Target { get; }

    public UsageClassification Classification { get; }

    public ImmutableArray<CallerUsage> Callers { get; }

    public bool IsConfiguredProductionRoot { get; }

    public ImmutableArray<AuditWarningKind> Warnings { get; }

    public bool IsStaticObservationOnly => true;
}
