using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalManifestEntry
{
    public IncrementalManifestEntry(
        ProjectFingerprint fingerprint,
        string contributionKey,
        bool isComplete = true)
    {
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        ArgumentException.ThrowIfNullOrWhiteSpace(contributionKey);
        ContributionKey = contributionKey.Trim();
        IsComplete = isComplete;
    }

    public ProjectIdentity Project => Fingerprint.Project;

    public ProjectFingerprint Fingerprint { get; }

    public string ContributionKey { get; }

    public bool IsComplete { get; }
}
