using System.Collections.Immutable;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalCacheState
{
    public IncrementalCacheState(
        RefreshRequestIdentity request,
        IEnumerable<ProjectContributionEnvelope> contributions,
        IEnumerable<IncrementalManifestEntry> manifest,
        RefreshGeneration generation)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Contributions = (contributions ?? throw new ArgumentNullException(nameof(contributions)))
            .OrderBy(contribution => contribution.Project.Key, StringComparer.Ordinal)
            .ToImmutableArray();
        Manifest = (manifest ?? throw new ArgumentNullException(nameof(manifest)))
            .OrderBy(entry => entry.Project.Key, StringComparer.Ordinal)
            .ToImmutableArray();

        Validate();
    }

    public RefreshRequestIdentity Request { get; }

    public ImmutableArray<ProjectContributionEnvelope> Contributions { get; }

    public ImmutableArray<IncrementalManifestEntry> Manifest { get; }

    public RefreshGeneration Generation { get; }

    private void Validate()
    {
        if (Contributions.Any(contribution => !contribution.IsComplete))
        {
            throw new InvalidDataException("An incremental cache cannot contain incomplete project contributions.");
        }

        if (Manifest.Any(entry => !entry.IsComplete))
        {
            throw new InvalidDataException("An incremental cache cannot contain incomplete manifest entries.");
        }

        var contributionByProject = Contributions.ToDictionary(
            contribution => contribution.Project.Key,
            StringComparer.Ordinal);
        var manifestByProject = Manifest.ToDictionary(
            entry => entry.Project.Key,
            StringComparer.Ordinal);

        if (contributionByProject.Count != Contributions.Length)
        {
            throw new InvalidDataException("An incremental cache cannot contain duplicate project contributions.");
        }

        if (manifestByProject.Count != Manifest.Length)
        {
            throw new InvalidDataException("An incremental cache cannot contain duplicate manifest entries.");
        }

        if (!contributionByProject.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .SequenceEqual(manifestByProject.Keys.OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("Incremental cache manifest and contributions do not cover the same projects.");
        }

        foreach (var contribution in Contributions)
        {
            var manifest = manifestByProject[contribution.Project.Key];
            if (!string.Equals(manifest.ContributionKey, contribution.ContributionKey, StringComparison.Ordinal)
                || !string.Equals(manifest.Fingerprint.Digest, contribution.Fingerprint.Digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Incremental cache manifest disagrees with project '{contribution.Project.Key}'.");
            }
        }
    }
}
