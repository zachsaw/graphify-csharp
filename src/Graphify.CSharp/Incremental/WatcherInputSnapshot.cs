using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Incremental;

internal enum FileChangeKind
{
    Changed,
    Created,
    Deleted,
    Renamed,
}

internal sealed record FileChangeEvent(
    FileChangeKind Kind,
    string Path,
    string? OldPath = null,
    bool RequiresColdReconciliation = false,
    bool? IsDirectory = null)
{
    public IEnumerable<string> Endpoints
    {
        get
        {
            yield return Path;
            if (!string.IsNullOrWhiteSpace(OldPath))
            {
                yield return OldPath!;
            }
        }
    }
}

internal readonly record struct WatcherEventClassification(
    bool Accepted,
    bool RequiresColdReconciliation);

internal sealed record WatcherRoot(string Path, bool IncludeSubdirectories)
{
    public string CanonicalPath { get; } = IncrementalPaths.CanonicalAbsolutePath(Path);
}

internal sealed class WatcherInputSnapshot
{
    private static readonly ImmutableHashSet<string> EmptyPaths =
        ImmutableHashSet.Create<string>(IncrementalPaths.PathComparer);

    private readonly ImmutableHashSet<string> _knownInputPaths;
    private readonly ImmutableHashSet<string> _knownSourcePaths;
    private readonly ImmutableHashSet<string> _knownDependencyPaths;
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _sourceProjects;
    private readonly ImmutableArray<string> _discoveryRoots;
    private readonly ImmutableHashSet<string> _knownInputAncestors;
    private readonly ImmutableHashSet<string> _knownStructuralInputAncestors;
    private readonly ImmutableHashSet<string> _infrastructurePaths;
    private readonly ImmutableArray<string> _ignoredRoots;
    private readonly ImmutableArray<string> _knownInputPathsOrdered;
    private readonly ImmutableArray<string> _knownSourcePathsOrdered;
    private readonly ImmutableArray<string> _knownDependencyPathsOrdered;
    private readonly ImmutableArray<GlobMatcher> _inputGlobs;
    private readonly ImmutableArray<string> _inputDiscoveryDiagnostics;
    private readonly bool _inputDiscoveryComplete;
    private readonly bool _isBootstrap;

    private WatcherInputSnapshot(
        IEnumerable<string> knownInputPaths,
        IEnumerable<string> knownSourcePaths,
        IEnumerable<string> knownDependencyPaths,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> sourceProjects,
        IEnumerable<string> discoveryRoots,
        IEnumerable<string> ignoredRoots,
        IEnumerable<WatcherRoot> watchRoots,
        IEnumerable<ProjectInputGlob>? inputGlobs = null,
        bool inputDiscoveryComplete = true,
        IEnumerable<string>? inputDiscoveryDiagnostics = null,
        IEnumerable<string>? infrastructurePaths = null,
        bool isBootstrap = false)
    {
        _knownInputPaths = CreatePathSet(knownInputPaths);
        _knownSourcePaths = CreatePathSet(knownSourcePaths);
        _knownDependencyPaths = CreatePathSet(knownDependencyPaths);
        _sourceProjects = sourceProjects
            .Select(pair => new
            {
                Path = IncrementalPaths.CanonicalAbsolutePath(pair.Key),
                Projects = pair.Value
                    .Where(project => !string.IsNullOrWhiteSpace(project))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(project => project, StringComparer.Ordinal)
                    .ToImmutableArray(),
            })
            .Where(pair => pair.Projects.Length > 0)
            .GroupBy(pair => pair.Path, IncrementalPaths.PathComparer)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.SelectMany(item => item.Projects)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(project => project, StringComparer.Ordinal)
                    .ToImmutableArray(),
                IncrementalPaths.PathComparer);
        _discoveryRoots = NormalizePaths(discoveryRoots);
        _ignoredRoots = NormalizePaths(ignoredRoots);
        _infrastructurePaths = CreatePathSet(infrastructurePaths ?? Array.Empty<string>());
        _knownInputAncestors = BuildAncestors(_knownInputPaths)
            .ToImmutableHashSet(IncrementalPaths.PathComparer);
        _knownStructuralInputAncestors = BuildAncestors(
                _knownSourcePaths.Concat(_knownDependencyPaths.Where(path => !_infrastructurePaths.Contains(path))))
            .ToImmutableHashSet(IncrementalPaths.PathComparer);
        _knownInputPathsOrdered = OrderPaths(_knownInputPaths);
        _knownSourcePathsOrdered = OrderPaths(_knownSourcePaths);
        _knownDependencyPathsOrdered = OrderPaths(_knownDependencyPaths);
        _inputGlobs = (inputGlobs ?? Array.Empty<ProjectInputGlob>())
            .Select(GlobMatcher.Create)
            .Distinct(GlobMatcherComparer.Instance)
            .OrderBy(glob => glob.FullPattern, StringComparer.Ordinal)
            .ToImmutableArray();
        _inputDiscoveryComplete = inputDiscoveryComplete;
        _inputDiscoveryDiagnostics = (inputDiscoveryDiagnostics ?? Array.Empty<string>())
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(diagnostic => diagnostic.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToImmutableArray();
        _isBootstrap = isBootstrap;
        WatchRoots = watchRoots
            .Select(root => new WatcherRoot(root.CanonicalPath, root.IncludeSubdirectories))
            .Distinct(WatcherRootComparer.Instance)
            .OrderBy(root => root.CanonicalPath, IncrementalPaths.PathComparer)
            .ThenBy(root => root.IncludeSubdirectories)
            .ToImmutableArray();
    }

    public static WatcherInputSnapshot CreateBootstrap(
        IEnumerable<string> discoveryRoots,
        string outputPath,
        string cachePath,
        IEnumerable<WatcherRoot>? watchRoots = null)
    {
        ArgumentNullException.ThrowIfNull(discoveryRoots);
        var ignoredRoots = BuildToolIgnoredRoots(outputPath, cachePath);
        return new WatcherInputSnapshot(
            EmptyPaths,
            EmptyPaths,
            EmptyPaths,
            Array.Empty<KeyValuePair<string, IEnumerable<string>>>(),
            discoveryRoots,
            ignoredRoots,
            watchRoots ?? discoveryRoots.Select(root => new WatcherRoot(root, IncludeSubdirectories: true)),
            isBootstrap: true);
    }

    public static WatcherInputSnapshot Create(
        LoadedSolution solution,
        ProjectLoadRequest request,
        string outputPath,
        string cachePath)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(request);

        var sources = new HashSet<string>(IncrementalPaths.PathComparer);
        var dependencies = new HashSet<string>(IncrementalPaths.PathComparer);
        var allInputs = new HashSet<string>(IncrementalPaths.PathComparer);
        var sourceProjects = new Dictionary<string, HashSet<string>>(IncrementalPaths.PathComparer);
        var infrastructurePaths = new HashSet<string>(IncrementalPaths.PathComparer);
        var explicitSemanticPaths = new HashSet<string>(IncrementalPaths.PathComparer);
        var inputGlobs = new HashSet<ProjectInputGlob>();
        var inputDiscoveryDiagnostics = new HashSet<string>(StringComparer.Ordinal);
        var inputDiscoveryComplete = true;
        var discoveryRoots = new HashSet<string>(IncrementalPaths.PathComparer)
        {
            IncrementalPaths.CanonicalAbsolutePath(request.RepositoryRoot),
        };
        var watchRoots = new List<WatcherRoot>
        {
            new(request.RepositoryRoot, IncludeSubdirectories: true),
        };

        AddKnownDependencyWithCoverage(
            request.InputPath,
            request.RepositoryRoot,
            dependencies,
            allInputs,
            discoveryRoots,
            watchRoots);
        foreach (var project in solution.Projects)
        {
            var projectDirectory = GetProjectDirectory(project, request.RepositoryRoot);
            if (!solution.IsTransientPath(projectDirectory))
            {
                discoveryRoots.Add(projectDirectory);
                watchRoots.Add(new WatcherRoot(projectDirectory, IncludeSubdirectories: true));
            }
            if (project.Identity.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && project.Project.FilePath is { } projectFilePath
                && !solution.IsTransientPath(projectFilePath))
            {
                AddKnownDependencyWithCoverage(
                    projectFilePath,
                    request.RepositoryRoot,
                    dependencies,
                    allInputs,
                    discoveryRoots,
                    watchRoots);
            }

            foreach (var document in project.Project.Documents)
            {
                if (document.FilePath is not { } filePath)
                {
                    continue;
                }

                if (solution.IsTransientPath(filePath))
                {
                    continue;
                }

                AddKnownSource(filePath, project.Identity.Key, sources, allInputs, sourceProjects);
                AddDiscoveryRootForExternalInput(filePath, request.RepositoryRoot, discoveryRoots, watchRoots, recursive: true);
            }

            foreach (var document in project.Project.AdditionalDocuments)
            {
                if (document.FilePath is not { } filePath)
                {
                    continue;
                }

                if (solution.IsTransientPath(filePath))
                {
                    continue;
                }

                AddKnownDependencyWithCoverage(
                    filePath,
                    request.RepositoryRoot,
                    dependencies,
                    allInputs,
                    discoveryRoots,
                    watchRoots);
            }

            foreach (var document in project.Project.AnalyzerConfigDocuments)
            {
                if (document.FilePath is not { } filePath)
                {
                    continue;
                }

                if (solution.IsTransientPath(filePath))
                {
                    continue;
                }

                AddKnownDependency(filePath, dependencies, allInputs);
            }

            var inputDiscovery = solution.GetInputDiscovery(project);
            inputDiscoveryComplete &= inputDiscovery.IsComplete;
            inputDiscoveryDiagnostics.UnionWith(inputDiscovery.Diagnostics);
            infrastructurePaths.UnionWith(inputDiscovery.InfrastructurePaths);
            explicitSemanticPaths.UnionWith(inputDiscovery.ExplicitSemanticPaths);
            foreach (var glob in inputDiscovery.Globs)
            {
                inputGlobs.Add(glob);
                AddGlobCoverage(
                    glob,
                    request.RepositoryRoot,
                    discoveryRoots,
                    watchRoots);
            }

            foreach (var dependencyPath in inputDiscovery.Paths)
            {
                if (!solution.IsTransientPath(dependencyPath))
                {
                    AddKnownDependency(dependencyPath, dependencies, allInputs);
                }
            }

        }

        infrastructurePaths.ExceptWith(sources);
        infrastructurePaths.ExceptWith(explicitSemanticPaths);

        AddConfigurationSearchInputs(solution, request, dependencies, allInputs, discoveryRoots, watchRoots);

        var ignoredRoots = BuildToolIgnoredRoots(outputPath, cachePath);
        if (allInputs.Any(path => ignoredRoots.Any(root => IncrementalPaths.IsPathOrUnder(path, root))))
        {
            throw new InvalidOperationException(
                "The configured graph output or cache directory is also a compilation input. Choose an output path outside the analyzed input scope.");
        }

        return new WatcherInputSnapshot(
            allInputs,
            sources,
            dependencies,
            sourceProjects.Select(pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)),
            discoveryRoots,
            ignoredRoots,
            watchRoots,
            inputGlobs,
            inputDiscoveryComplete,
            inputDiscoveryDiagnostics,
            infrastructurePaths);
    }

    internal static WatcherInputSnapshot CreateForTests(
        IEnumerable<string> knownSources,
        IEnumerable<string> knownDependencies,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> sourceProjects,
        IEnumerable<string> discoveryRoots,
        string outputPath,
        string cachePath,
        IEnumerable<ProjectInputGlob>? inputGlobs = null,
        bool inputDiscoveryComplete = true,
        IEnumerable<string>? inputDiscoveryDiagnostics = null,
        IEnumerable<string>? infrastructurePaths = null)
    {
        var sources = CreatePathSet(knownSources);
        var dependencies = CreatePathSet(knownDependencies);
        return new WatcherInputSnapshot(
            sources.Concat(dependencies),
            sources,
            dependencies,
            sourceProjects,
            discoveryRoots,
            BuildToolIgnoredRoots(outputPath, cachePath),
            discoveryRoots.Select(root => new WatcherRoot(root, IncludeSubdirectories: true)),
            inputGlobs,
            inputDiscoveryComplete,
            inputDiscoveryDiagnostics,
            infrastructurePaths);
    }

    public ImmutableArray<string> KnownInputPaths => _knownInputPathsOrdered;

    public ImmutableArray<string> KnownSourcePaths => _knownSourcePathsOrdered;

    public ImmutableArray<string> KnownDependencyPaths => _knownDependencyPathsOrdered;

    public ImmutableArray<string> DiscoveryRoots => _discoveryRoots;

    public ImmutableArray<WatcherRoot> WatchRoots { get; }

    public ImmutableArray<ProjectInputGlob> InputGlobs =>
        _inputGlobs.Select(glob => glob.Definition).ToImmutableArray();

    public bool InputDiscoveryComplete => _inputDiscoveryComplete;

    public ImmutableArray<string> InputDiscoveryDiagnostics => _inputDiscoveryDiagnostics;

    public bool IsBootstrap => _isBootstrap;

    public bool IsKnownInput(string path) => _knownInputPaths.Contains(Canonicalize(path));

    public bool IsKnownSource(string path) => _knownSourcePaths.Contains(Canonicalize(path));

    public bool IsKnownDependency(string path) => _knownDependencyPaths.Contains(Canonicalize(path));

    public IReadOnlyList<string> GetSourceProjects(string path) =>
        _sourceProjects.GetValueOrDefault(Canonicalize(path), ImmutableArray<string>.Empty);

    public bool ShouldIncludeInInventory(string path)
    {
        var canonicalPath = Canonicalize(path);
        return !IsToolPath(canonicalPath)
            && (_knownInputPaths.Contains(canonicalPath)
                || _inputGlobs.Any(glob => glob.Matches(canonicalPath)
                    && glob.MayIntentionallyIncludeExcludedPath(canonicalPath, isDirectory: false))
                || (_isBootstrap && IsBootstrapRelevantBuildFile(canonicalPath))
                || (!IsConventionalExcludedDirectory(canonicalPath)
                    && IsConventionalRelevantFilePath(canonicalPath)));
    }

    public bool ShouldTraverseDirectory(string path)
    {
        var canonicalPath = Canonicalize(path);
        return !IsToolPath(canonicalPath)
            && (!IsConventionalExcludedDirectory(canonicalPath)
                || _inputGlobs.Any(glob =>
                    glob.MayContain(canonicalPath)
                    && glob.MayIntentionallyIncludeExcludedPath(canonicalPath, isDirectory: true)));
    }

    public WatcherEventClassification Classify(FileChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var endpoints = change.Endpoints
            .Select(Canonicalize)
            .Distinct(IncrementalPaths.PathComparer)
            .ToArray();
        if (endpoints.Length == 0)
        {
            return new WatcherEventClassification(Accepted: false, RequiresColdReconciliation: false);
        }

        var relevantEndpoints = endpoints
            .Where(path => !IsToolPath(path))
            .ToArray();
        if (relevantEndpoints.Length == 0)
        {
            return new WatcherEventClassification(Accepted: false, RequiresColdReconciliation: false);
        }

        if (_isBootstrap)
        {
            // Before Roslyn has evaluated the project, exclusions such as
            // obj/bin are not authoritative: a project may explicitly include
            // a file there, or a non-source input may have any extension. The
            // host coalesces the first uncertain event into recovery rather
            // than enqueueing the entire startup burst.
            var uncertainEndpoints = relevantEndpoints
                .Where(path => !IsBootstrapConfidentlyExcludedDirectory(path))
                .Where(IsUnderDiscoveryRoot)
                .ToArray();
            return new WatcherEventClassification(
                Accepted: uncertainEndpoints.Length > 0,
                RequiresColdReconciliation: uncertainEndpoints.Length > 0);
        }

        var exactInput = relevantEndpoints.Any(_knownInputPaths.Contains);
        if (exactInput)
        {
            var sourceOnlyChange = change.Kind == FileChangeKind.Changed
                && relevantEndpoints.All(_knownSourcePaths.Contains);
            return new WatcherEventClassification(
                Accepted: true,
                RequiresColdReconciliation: change.RequiresColdReconciliation
                    || !sourceOnlyChange
                    || relevantEndpoints.Any(_knownDependencyPaths.Contains));
        }

        if (relevantEndpoints.Any(path =>
                IsKnownInputAncestor(path)
                && (!IsConventionalExcludedDirectoryRoot(path)
                    || _knownStructuralInputAncestors.Contains(path)
                    || change.Kind is FileChangeKind.Deleted or FileChangeKind.Renamed)))
        {
            return new WatcherEventClassification(
                Accepted: true,
                RequiresColdReconciliation: true);
        }

        var discoveryEndpoints = relevantEndpoints
            .Where(path => !IsConventionalExcludedDirectory(path)
                || _inputGlobs.Any(glob =>
                    glob.MayIntentionallyIncludeExcludedPath(path, change.IsDirectory)
                    && (glob.Matches(path) || glob.MayContain(path))))
            .ToArray();
        var discoveryEvent = discoveryEndpoints.Any(IsUnderDiscoveryRoot);
        if (!discoveryEvent)
        {
            return new WatcherEventClassification(Accepted: false, RequiresColdReconciliation: false);
        }

        var conventionalEvent = discoveryEndpoints.Any(path =>
            IsConventionalRelevantFilePath(path)
            || (change.IsDirectory ?? IsDirectoryLike(path)));
        var globEvent = discoveryEndpoints.Any(path =>
            _inputGlobs.Any(glob => glob.Matches(path)
                || glob.MayContain(path)));
        return new WatcherEventClassification(
            Accepted: conventionalEvent || globEvent,
            RequiresColdReconciliation: conventionalEvent || globEvent);
    }

    private bool IsToolPath(string path) =>
        _ignoredRoots.Any(root => IncrementalPaths.IsPathOrUnder(path, root)
            || IsAtomicTemporaryPath(path, root));

    private static bool IsAtomicTemporaryPath(string path, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        var pathDirectory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(destinationDirectory)
            || !string.Equals(destinationDirectory, pathDirectory, IncrementalPaths.PathComparison))
        {
            return false;
        }

        var destinationFileName = Path.GetFileName(destinationPath);
        var pathFileName = Path.GetFileName(path);
        return pathFileName.StartsWith($".{destinationFileName}.", IncrementalPaths.PathComparison)
            && pathFileName.EndsWith(".tmp", IncrementalPaths.PathComparison);
    }

    private bool IsKnownInputAncestor(string path) =>
        _knownInputAncestors.Contains(path);

    private bool IsUnderDiscoveryRoot(string path) =>
        _discoveryRoots.Any(root => IncrementalPaths.IsUnderDirectory(path, root));

    private static string Canonicalize(string path) =>
        IncrementalPaths.CanonicalAbsolutePath(path);

    private static ImmutableHashSet<string> CreatePathSet(IEnumerable<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Canonicalize)
            .ToImmutableHashSet(IncrementalPaths.PathComparer);

    private static ImmutableArray<string> NormalizePaths(IEnumerable<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Canonicalize)
            .Distinct(IncrementalPaths.PathComparer)
            .OrderBy(path => path, IncrementalPaths.PathComparer)
            .ToImmutableArray();

    private static ImmutableArray<string> OrderPaths(IEnumerable<string> paths) =>
        paths.OrderBy(path => path, IncrementalPaths.PathComparer).ToImmutableArray();

    private static ImmutableArray<string> BuildAncestors(IEnumerable<string> paths)
    {
        var ancestors = new HashSet<string>(IncrementalPaths.PathComparer);
        foreach (var path in paths)
        {
            var directory = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrWhiteSpace(directory))
            {
                var canonicalDirectory = Canonicalize(directory);
                if (!ancestors.Add(canonicalDirectory))
                {
                    break;
                }

                var parent = Directory.GetParent(directory)?.FullName;
                if (string.Equals(parent, directory, IncrementalPaths.PathComparison))
                {
                    break;
                }

                directory = parent;
            }
        }

        return ancestors.OrderBy(path => path, IncrementalPaths.PathComparer).ToImmutableArray();
    }

    private static IReadOnlyList<string> BuildToolIgnoredRoots(string outputPath, string cachePath) =>
    [
        Canonicalize(outputPath),
        Path.GetDirectoryName(Canonicalize(cachePath)) ?? Canonicalize(cachePath),
    ];

    private static string GetProjectDirectory(AnalyzedProject project, string repositoryRoot)
    {
        var projectPath = project.Project.FilePath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            projectPath = Path.Combine(
                repositoryRoot,
                project.Identity.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.GetDirectoryName(Canonicalize(projectPath)) ?? Canonicalize(repositoryRoot);
    }

    private static void AddKnownSource(
        string path,
        string projectKey,
        ISet<string> sources,
        ISet<string> allInputs,
        IDictionary<string, HashSet<string>> sourceProjects)
    {
        var canonicalPath = Canonicalize(path);
        sources.Add(canonicalPath);
        allInputs.Add(canonicalPath);
        if (!sourceProjects.TryGetValue(canonicalPath, out var projects))
        {
            projects = new HashSet<string>(StringComparer.Ordinal);
            sourceProjects.Add(canonicalPath, projects);
        }

        projects.Add(projectKey);
    }

    private static void AddKnownDependency(string path, ISet<string> dependencies, ISet<string> allInputs)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var canonicalPath = Canonicalize(path);
        dependencies.Add(canonicalPath);
        allInputs.Add(canonicalPath);
    }

    private static void AddKnownDependencyWithCoverage(
        string path,
        string repositoryRoot,
        ISet<string> dependencies,
        ISet<string> allInputs,
        ISet<string> discoveryRoots,
        ICollection<WatcherRoot> watchRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        AddKnownDependency(path, dependencies, allInputs);
        AddDiscoveryRootForExternalInput(
            path,
            repositoryRoot,
            discoveryRoots,
            watchRoots,
            recursive: false);
    }

    private static void AddDiscoveryRootForExternalInput(
        string path,
        string repositoryRoot,
        ISet<string> discoveryRoots,
        ICollection<WatcherRoot> watchRoots,
        bool recursive)
    {
        var canonicalPath = Canonicalize(path);
        if (IncrementalPaths.IsUnderDirectory(canonicalPath, repositoryRoot))
        {
            return;
        }

        var directory = Path.GetDirectoryName(canonicalPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        discoveryRoots.Add(directory);
        watchRoots.Add(new WatcherRoot(directory, recursive));
    }

    private static void AddGlobCoverage(
        ProjectInputGlob glob,
        string repositoryRoot,
        ISet<string> discoveryRoots,
        ICollection<WatcherRoot> watchRoots)
    {
        var coverageRoot = FindExistingDirectory(glob);
        if (coverageRoot is null
            || IncrementalPaths.IsUnderDirectory(coverageRoot, repositoryRoot))
        {
            return;
        }

        discoveryRoots.Add(coverageRoot);
        watchRoots.Add(new WatcherRoot(coverageRoot, IncludeSubdirectories: true));
    }

    private static string? FindExistingDirectory(ProjectInputGlob glob)
    {
        var pattern = glob.Pattern.Replace('\\', '/');
        var fullPattern = Path.IsPathRooted(pattern)
            ? pattern
            : Path.Combine(glob.CanonicalBaseDirectory, pattern);
        var wildcardIndex = fullPattern.IndexOfAny(['*', '?']);
        if (wildcardIndex < 0)
        {
            return null;
        }

        var slashIndex = fullPattern.LastIndexOf('/', wildcardIndex);
        var candidate = slashIndex < 0
            ? glob.CanonicalBaseDirectory
            : fullPattern[..slashIndex];
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Path.DirectorySeparatorChar.ToString();
        }

        candidate = Canonicalize(candidate);
        while (!Directory.Exists(candidate))
        {
            var parent = Directory.GetParent(candidate.Replace('/', Path.DirectorySeparatorChar))?.FullName;
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, candidate, IncrementalPaths.PathComparison))
            {
                return null;
            }

            candidate = Canonicalize(parent);
        }

        return candidate;
    }

    private static void AddConfigurationSearchInputs(
        LoadedSolution solution,
        ProjectLoadRequest request,
        ISet<string> dependencies,
        ISet<string> allInputs,
        ISet<string> discoveryRoots,
        ICollection<WatcherRoot> watchRoots)
    {
        var projectDirectories = solution.Projects
            .Select(project => project.Project.FilePath is { } path
                ? Path.GetDirectoryName(path) ?? request.RepositoryRoot
                : request.RepositoryRoot)
            .Append(Path.GetDirectoryName(request.InputPath) ?? request.RepositoryRoot)
            .Where(directory => !solution.IsTransientPath(directory))
            .Distinct(IncrementalPaths.PathComparer);
        var repositoryRoot = Canonicalize(request.RepositoryRoot);
        var repositoryParent = Directory.GetParent(repositoryRoot)?.FullName;
        foreach (var projectDirectory in projectDirectories)
        {
            var directory = projectDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                foreach (var name in ConventionalConfigurationNames)
                {
                    var candidate = Path.Combine(directory, name);
                    AddKnownDependency(candidate, dependencies, allInputs);
                }

                var canonicalDirectory = Canonicalize(directory);
                if (!IncrementalPaths.IsUnderDirectory(canonicalDirectory, repositoryRoot)
                    && (repositoryParent is not null
                        && string.Equals(canonicalDirectory, Canonicalize(repositoryParent), IncrementalPaths.PathComparison)))
                {
                    discoveryRoots.Add(canonicalDirectory);
                    watchRoots.Add(new WatcherRoot(canonicalDirectory, IncludeSubdirectories: false));
                }

                var parent = Directory.GetParent(directory)?.FullName;
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(parent, directory, IncrementalPaths.PathComparison))
                {
                    break;
                }

                directory = parent;
            }
        }
    }

    private static bool IsConventionalExcludedDirectory(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => ExcludedDirectoryNames.Contains(segment));
    }

    private static bool IsConventionalExcludedDirectoryRoot(string path) =>
        ExcludedDirectoryNames.Contains(Path.GetFileName(path));

    private static bool IsDirectoryLike(string path) =>
        string.IsNullOrEmpty(Path.GetExtension(Path.GetFileName(path)));

    private static bool IsBootstrapConfidentlyExcludedDirectory(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => BootstrapExcludedDirectoryNames.Contains(segment));
    }

    private static bool IsBootstrapRelevantBuildFile(string path) =>
        Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase);

    internal static bool IsConventionalRelevantFilePath(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.rsp", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Solution.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Solution.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("MSBuild.rsp", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly ImmutableHashSet<string> ExcludedDirectoryNames =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ".git",
            "bin",
            "obj",
            "node_modules",
            ".e2e",
            "graphify-out",
            ".vs",
            "TestResults",
            "artifacts");

    private static readonly ImmutableHashSet<string> BootstrapExcludedDirectoryNames =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ".git",
            "node_modules",
            ".e2e",
            "graphify-out",
            ".vs",
            "TestResults",
            "artifacts");

    private static readonly ImmutableArray<string> ConventionalConfigurationNames =
    [
        "global.json",
        ".editorconfig",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Build.rsp",
        "Directory.Solution.props",
        "Directory.Solution.targets",
        "MSBuild.rsp",
        "Directory.Packages.props",
        "packages.lock.json",
        "NuGet.Config",
    ];

    private sealed class WatcherRootComparer : IEqualityComparer<WatcherRoot>
    {
        public static WatcherRootComparer Instance { get; } = new();

        public bool Equals(WatcherRoot? first, WatcherRoot? second) =>
            first is not null
            && second is not null
            && first.IncludeSubdirectories == second.IncludeSubdirectories
            && string.Equals(first.CanonicalPath, second.CanonicalPath, IncrementalPaths.PathComparison);

        public int GetHashCode(WatcherRoot root) =>
            HashCode.Combine(
                IncrementalPaths.PathComparer.GetHashCode(root.CanonicalPath),
                root.IncludeSubdirectories);
    }

    private sealed class GlobMatcher
    {
        private GlobMatcher(
            ProjectInputGlob definition,
            string fullPattern,
            string coverageRoot,
            bool hasWildcard,
            Regex regex,
            ImmutableArray<GlobMatcher> exclusions)
        {
            Definition = definition;
            FullPattern = fullPattern;
            CoverageRoot = coverageRoot;
            HasWildcard = hasWildcard;
            Regex = regex;
            _exclusions = exclusions;
            ExclusionPatterns = exclusions
                .Select(exclusion => exclusion.FullPattern)
                .OrderBy(pattern => pattern, IncrementalPaths.PathComparer)
                .ToImmutableArray();
            RecursiveDirectoryRegex = CreateRecursiveDirectoryRegex(fullPattern);
        }

        public ProjectInputGlob Definition { get; }

        public string FullPattern { get; }

        public string CoverageRoot { get; }

        private bool HasWildcard { get; }

        private Regex Regex { get; }

        private ImmutableArray<GlobMatcher> _exclusions { get; }

        public ImmutableArray<string> ExclusionPatterns { get; }

        private Regex? RecursiveDirectoryRegex { get; }

        public static GlobMatcher Create(ProjectInputGlob definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            var include = CreatePattern(definition, requireWildcard: true);
            var exclusions = definition.ExcludePatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => CreatePattern(
                    new ProjectInputGlob(definition.ItemType, definition.CanonicalBaseDirectory, pattern),
                    requireWildcard: false))
                .Distinct(GlobMatcherComparer.Instance)
                .ToImmutableArray();
            return new GlobMatcher(
                definition,
                include.FullPattern,
                include.CoverageRoot,
                include.HasWildcard,
                include.Regex,
                exclusions);
        }

        public bool Matches(string path) => Regex.IsMatch(path.Replace('\\', '/'));

        public bool MayContain(string path) =>
            HasWildcard
            && (IncrementalPaths.IsPathOrUnder(path, CoverageRoot)
                || IncrementalPaths.IsPathOrUnder(CoverageRoot, path));

        public bool MayIntentionallyIncludeExcludedPath(string path, bool? isDirectory)
        {
            if (!IsConventionalExcludedDirectory(path))
            {
                return true;
            }

            return !_exclusions.Any(exclusion =>
                isDirectory switch
                {
                    true => exclusion.ExcludesDirectory(path),
                    false => exclusion.Matches(path),
                    // FileSystemWatcher events do not carry a reliable entry
                    // type. Native events are annotated by the watcher's
                    // worker when the entry still exists; deleted or otherwise
                    // ambiguous paths remain conservative. Reject an uncertain
                    // path only when both interpretations are excluded;
                    // otherwise a supported file whose name resembles an
                    // exclusion directory prefix must still be delivered.
                    null => exclusion.Matches(path) && exclusion.ExcludesDirectory(path),
                });
        }

        private bool ExcludesDirectory(string path) =>
            // Only a recursive catch-all suffix proves that every descendant
            // is excluded. A nonrecursive pattern such as obj/* may match a
            // directory name, but it does not exclude files nested beneath
            // that directory.
            RecursiveDirectoryRegex?.IsMatch(path.Replace('\\', '/')) == true;

        private static Regex? CreateRecursiveDirectoryRegex(string fullPattern)
        {
            var normalized = fullPattern.TrimEnd('/');
            string? prefix = null;
            if (normalized.EndsWith("/**/*", StringComparison.Ordinal))
            {
                prefix = normalized[..^5];
            }
            else if (normalized.EndsWith("/**", StringComparison.Ordinal))
            {
                prefix = normalized[..^3];
            }

            if (prefix is null)
            {
                return null;
            }

            var prefixRegex = BuildRegex(prefix);
            return new Regex(
                    prefixRegex.TrimEnd('$') + "(?:/.*)?$",
                    RegexOptions.CultureInvariant
                    | RegexOptions.Compiled
                    | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
        }

        private static GlobMatcher CreatePattern(ProjectInputGlob definition, bool requireWildcard)
        {
            var pattern = definition.Pattern.Replace('\\', '/');
            var fullPattern = Path.IsPathRooted(pattern)
                ? pattern
                : Path.Combine(definition.CanonicalBaseDirectory, pattern);
            fullPattern = Path.GetFullPath(fullPattern).Replace('\\', '/');
            var wildcardIndex = fullPattern.IndexOfAny(['*', '?']);
            if (requireWildcard && wildcardIndex < 0)
            {
                throw new ArgumentException("A project input glob must contain a wildcard.", nameof(definition));
            }

            var slashIndex = wildcardIndex < 0
                ? fullPattern.LastIndexOf('/')
                : fullPattern.LastIndexOf('/', wildcardIndex);
            var coverageRoot = slashIndex < 0
                ? definition.CanonicalBaseDirectory
                : fullPattern[..slashIndex];
            if (string.IsNullOrWhiteSpace(coverageRoot))
            {
                coverageRoot = Path.DirectorySeparatorChar.ToString();
            }

            return new GlobMatcher(
                definition,
                fullPattern,
                Canonicalize(coverageRoot),
                wildcardIndex >= 0,
                new Regex(
                    BuildRegex(fullPattern),
                    RegexOptions.CultureInvariant
                    | RegexOptions.Compiled
                    | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None)),
                ImmutableArray<GlobMatcher>.Empty);
        }

        private static string BuildRegex(string fullPattern)
        {
            var normalized = fullPattern.Replace('\\', '/');
            var builder = new System.Text.StringBuilder("^");
            for (var index = 0; index < normalized.Length; index++)
            {
                var character = normalized[index];
                if (character == '*' && index + 1 < normalized.Length && normalized[index + 1] == '*')
                {
                    if (index + 2 < normalized.Length && normalized[index + 2] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        index += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        index++;
                    }

                    continue;
                }

                if (character == '*')
                {
                    builder.Append("[^/]*");
                }
                else if (character == '?')
                {
                    builder.Append("[^/]");
                }
                else
                {
                    builder.Append(Regex.Escape(character.ToString()));
                }
            }

            return builder.Append('$').ToString();
        }
    }

    private sealed class GlobMatcherComparer : IEqualityComparer<GlobMatcher>
    {
        public static GlobMatcherComparer Instance { get; } = new();

        public bool Equals(GlobMatcher? first, GlobMatcher? second)
        {
            if (first is null
                || second is null
                || !string.Equals(first.FullPattern, second.FullPattern, IncrementalPaths.PathComparison))
            {
                return false;
            }

            return first.ExclusionPatterns.SequenceEqual(
                second.ExclusionPatterns,
                IncrementalPaths.PathComparer);
        }

        public int GetHashCode(GlobMatcher matcher)
        {
            var hash = IncrementalPaths.PathComparer.GetHashCode(matcher.FullPattern);
            foreach (var exclusion in matcher.ExclusionPatterns)
            {
                hash = HashCode.Combine(
                    hash,
                    IncrementalPaths.PathComparer.GetHashCode(exclusion));
            }

            return hash;
        }
    }
}
