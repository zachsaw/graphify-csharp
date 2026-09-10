using Graphify.CSharp.Domain;
using Graphify.CSharp.Graphify;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalRefreshEngine
{
    private readonly IProjectLoader _projectLoader;
    private readonly IncrementalCacheStore _cacheStore;
    private readonly IncrementalOutputPublisher _outputPublisher;
    private readonly Func<Guid> _sessionIdFactory;

    public IncrementalRefreshEngine(
        IProjectLoader? projectLoader = null,
        IncrementalCacheStore? cacheStore = null,
        IncrementalOutputPublisher? outputPublisher = null,
        Func<Guid>? sessionIdFactory = null)
    {
        _projectLoader = projectLoader ?? new RoslynWorkspaceLoader();
        _cacheStore = cacheStore ?? new IncrementalCacheStore();
        _outputPublisher = outputPublisher ?? new IncrementalOutputPublisher();
        _sessionIdFactory = sessionIdFactory ?? Guid.NewGuid;
    }

    public async Task<IncrementalRefreshResult> RefreshAsync(
        ProjectLoadRequest request,
        string outputPath,
        bool rebuild = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var requestIdentity = new RefreshRequestIdentity(
            request.InputPath,
            request.RepositoryRoot,
            request.Configuration,
            request.TargetFramework);
        var cachePath = IncrementalCachePath.ForOutput(outputPath);
        var cacheResult = rebuild
            ? IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Incompatible,
                "The cache was intentionally bypassed by rebuild.")
            : await _cacheStore.LoadAsync(cachePath, requestIdentity, cancellationToken).ConfigureAwait(false);
        var previousState = cacheResult.State;

        using var solution = await _projectLoader.LoadAsync(request, cancellationToken).ConfigureAwait(false);
        var fingerprints = new IncrementalProjectFingerprintBuilder().BuildAll(solution);
        var dirtyProjectKeys = DetermineDirtyProjects(previousState, fingerprints, rebuild);
        var currentProjectKeys = fingerprints.Keys.ToHashSet(StringComparer.Ordinal);
        var reusableContributions = previousState?.Contributions
            .Where(contribution => currentProjectKeys.Contains(contribution.Project.Key))
            .ToDictionary(contribution => contribution.Project.Key, StringComparer.Ordinal)
            ?? new Dictionary<string, ProjectContributionEnvelope>(StringComparer.Ordinal);

        IReadOnlyList<ProjectContributionEnvelope> contributions;
        var globalDiagnostics = Array.Empty<string>();
        var extractedProjectCount = dirtyProjectKeys.Count;
        var reusedProjectCount = 0;
        if (dirtyProjectKeys.Count == 0 && previousState is not null)
        {
            contributions = previousState.Contributions
                .Where(contribution => currentProjectKeys.Contains(contribution.Project.Key))
                .OrderBy(contribution => contribution.Project.Key, StringComparer.Ordinal)
                .ToArray();
            reusedProjectCount = contributions.Count;
            globalDiagnostics = previousState.Diagnostics.ToArray();
        }
        else
        {
            var catalog = await new DeclarationCatalogBuilder().BuildAsync(solution, cancellationToken).ConfigureAwait(false);
            var extractor = new SemanticReferenceExtractor();
            var extracted = await extractor
                .ExtractContributionsAsync(solution, catalog, dirtyProjectKeys, cancellationToken)
                .ConfigureAwait(false);
            var extractedByProject = extracted.ToDictionary(
                contribution => contribution.Project.Key,
                StringComparer.Ordinal);
            var merged = new List<ProjectContributionEnvelope>(fingerprints.Count);
            foreach (var project in solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dirtyProjectKeys.Contains(project.Identity.Key))
                {
                    if (!extractedByProject.TryGetValue(project.Identity.Key, out var projectContribution))
                    {
                        throw new InvalidDataException($"No contribution was extracted for project '{project.Identity.Key}'.");
                    }

                    merged.Add(new ProjectContributionEnvelope(
                        fingerprints[project.Identity.Key],
                        projectContribution.Graph,
                        projectContribution.Diagnostics));
                }
                else if (reusableContributions.TryGetValue(project.Identity.Key, out var cachedContribution))
                {
                    merged.Add(cachedContribution);
                    reusedProjectCount++;
                }
                else
                {
                    throw new InvalidDataException($"Project '{project.Identity.Key}' has no reusable or extracted contribution.");
                }
            }

            contributions = merged;
            globalDiagnostics = solution.Diagnostics
                .Select(diagnostic => $"{diagnostic.Kind}: {diagnostic.Message}")
                .Concat(catalog.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
                .ToArray();
        }

        var graph = GraphSnapshot.Create(
            contributions.SelectMany(contribution => contribution.Graph.Nodes),
            contributions.SelectMany(contribution => contribution.Graph.Edges));
        var diagnostics = globalDiagnostics
            .Concat(contributions.SelectMany(contribution => contribution.Diagnostics))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToArray();
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDigest = await PublishIfNeededAsync(
            fullOutputPath,
            graph,
            diagnostics,
            previousState,
            dirtyProjectKeys.Count != 0 || rebuild,
            cancellationToken).ConfigureAwait(false);

        var generation = BuildGeneration(previousState, dirtyProjectKeys.Count != 0 || rebuild);
        var manifest = contributions.Select(contribution => new IncrementalManifestEntry(
                contribution.Fingerprint,
                contribution.ContributionKey))
            .ToArray();
        var newState = new IncrementalCacheState(
            requestIdentity,
            contributions,
            manifest,
            generation,
            globalDiagnostics,
            fullOutputPath,
            outputDigest);
        await _cacheStore.SaveAsync(cachePath, newState, cancellationToken).ConfigureAwait(false);

        return new IncrementalRefreshResult(
            graph,
            outputDigest,
            cacheResult.Status,
            extractedProjectCount,
            reusedProjectCount,
            outputRepublished: dirtyProjectKeys.Count != 0 || rebuild || !IsPublishedOutputCurrent(previousState, fullOutputPath),
            generation);
    }

    private async Task<string> PublishIfNeededAsync(
        string outputPath,
        GraphSnapshot graph,
        IReadOnlyList<string> diagnostics,
        IncrementalCacheState? previousState,
        bool forcePublish,
        CancellationToken cancellationToken)
    {
        if (!forcePublish && IsPublishedOutputCurrent(previousState, outputPath))
        {
            return previousState!.PublishedOutputDigest!;
        }

        return await _outputPublisher
            .PublishAsync(outputPath, graph, diagnostics, cancellationToken)
            .ConfigureAwait(false);
    }

    private RefreshGeneration BuildGeneration(IncrementalCacheState? previousState, bool changed)
    {
        var generation = previousState?.Generation
            ?? new RefreshGeneration(_sessionIdFactory());
        if (!changed)
        {
            return generation;
        }

        var withEvent = generation.RecordEvent();
        return withEvent.MarkIndexed(withEvent.EventGeneration)
            .MarkPublished(withEvent.EventGeneration);
    }

    private static bool IsPublishedOutputCurrent(IncrementalCacheState? state, string outputPath)
    {
        if (state?.PublishedOutputPath is null
            || state.PublishedOutputDigest is null
            || !string.Equals(
                state.PublishedOutputPath,
                IncrementalPaths.CanonicalAbsolutePath(outputPath),
                StringComparison.Ordinal)
            || !File.Exists(outputPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                IncrementalHashing.Sha256File(outputPath),
                state.PublishedOutputDigest,
                StringComparison.Ordinal);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static HashSet<string> DetermineDirtyProjects(
        IncrementalCacheState? previousState,
        IReadOnlyDictionary<string, ProjectFingerprint> currentFingerprints,
        bool rebuild)
    {
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        if (rebuild || previousState is null)
        {
            dirty.UnionWith(currentFingerprints.Keys);
            return dirty;
        }

        var previousByProject = previousState.Manifest.ToDictionary(
            entry => entry.Project.Key,
            StringComparer.Ordinal);
        var previousProjectKeys = previousByProject.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var (projectKey, fingerprint) in currentFingerprints)
        {
            if (!previousByProject.TryGetValue(projectKey, out var previous)
                || previous.Fingerprint.CompareTo(fingerprint) == FingerprintComparison.Different)
            {
                dirty.Add(projectKey);
            }
        }

        var removedProjects = previousProjectKeys
            .Except(currentFingerprints.Keys, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var changedProjects = dirty.Concat(removedProjects).ToHashSet(StringComparer.Ordinal);
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var (projectKey, fingerprint) in currentFingerprints)
            {
                if (dirty.Contains(projectKey)
                    || !fingerprint.ProjectReferenceKeys
                        .Select(ReferenceProjectKey)
                        .Any(changedProjects.Contains))
                {
                    continue;
                }

                dirty.Add(projectKey);
                if (changedProjects.Add(projectKey))
                {
                    expanded = true;
                }
            }
        }

        return dirty;
    }

    private static string ReferenceProjectKey(string referenceKey)
    {
        var separator = referenceKey.IndexOf('\u001F');
        return separator < 0 ? referenceKey : referenceKey[..separator];
    }
}
