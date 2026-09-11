using System.Collections.Immutable;
using Graphify.CSharp.Incremental;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis;
using System.Text.RegularExpressions;
using BuildProject = Microsoft.Build.Evaluation.Project;
using RoslynProject = Microsoft.CodeAnalysis.Project;

namespace Graphify.CSharp.Roslyn;

internal sealed record ProjectInputGlob(
    string ItemType,
    string BaseDirectory,
    string Pattern)
{
    public ImmutableArray<string> ExcludePatterns { get; init; } = ImmutableArray<string>.Empty;

    public string CanonicalBaseDirectory { get; } =
        IncrementalPaths.CanonicalAbsolutePath(BaseDirectory);
}

internal sealed record ProjectInputDiscoveryResult(
    ImmutableArray<string> Paths,
    bool IsComplete,
    ImmutableArray<ProjectInputGlob> Globs,
    ImmutableArray<string> InfrastructurePaths,
    ImmutableArray<string> ExplicitSemanticPaths,
    ImmutableArray<string> Diagnostics);

internal static class RoslynProjectInputDiscovery
{
    public static ProjectInputDiscoveryResult Discover(
        RoslynProject project,
        string projectDirectory,
        ProjectLoadRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var paths = new HashSet<string>(IncrementalPaths.PathComparer);
        var infrastructurePaths = new HashSet<string>(IncrementalPaths.PathComparer);
        var explicitSemanticPaths = new HashSet<string>(IncrementalPaths.PathComparer);
        var globs = new HashSet<ProjectInputGlob>();
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in project.AdditionalDocuments)
        {
            AddPath(document.FilePath, paths);
            AddPath(document.FilePath, explicitSemanticPaths);
        }

        foreach (var document in project.AnalyzerConfigDocuments)
        {
            AddPath(document.FilePath, paths);
            AddPath(document.FilePath, explicitSemanticPaths);
        }

        foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            AddPath(reference.FilePath, paths);
            AddPath(reference.FilePath, explicitSemanticPaths);
        }

        foreach (var reference in project.AnalyzerReferences)
        {
            if (reference.Display is { } display && Path.IsPathRooted(display))
            {
                AddPath(display, paths);
                AddPath(display, explicitSemanticPaths);
            }
        }

        // Restore metadata is not a Roslyn document or import, but it can
        // change the resolved references and analyzers used by the evaluated
        // project. Keep the conventional path even when it does not exist yet
        // so a later restore/create invalidates the warm boundary.
        var restoreMetadataPath = Path.Combine(
            IncrementalPaths.CanonicalAbsolutePath(projectDirectory),
            "obj",
            "project.assets.json");
        AddPath(restoreMetadataPath, paths);
        AddPath(restoreMetadataPath, infrastructurePaths);

        foreach (var path in GetConventionalConfigurationPaths(projectDirectory))
        {
            AddPath(path, paths);
        }

        var isComplete = true;
        if (project.FilePath is { } projectPath)
        {
            if (request is null)
            {
                // A loaded solution created outside RoslynWorkspaceLoader does
                // not carry the global MSBuild properties needed to evaluate
                // conditional imports. Its explicit Roslyn inputs are still
                // useful, but persisted reuse must remain conservative.
                isComplete = false;
                diagnostics.Add(
                    $"Watcher input discovery was incomplete for project '{IncrementalPaths.CanonicalAbsolutePath(projectPath)}': no load request was available for conditional MSBuild evaluation.");
            }
            else
            {
                try
                {
                    var evaluated = EvaluateProjectInputs(projectPath, request);
                    isComplete &= evaluated.IsComplete;
                    diagnostics.UnionWith(evaluated.Diagnostics);
                    foreach (var path in evaluated.ImportPaths)
                    {
                        AddPath(path, paths);
                    }

                    foreach (var path in evaluated.ExplicitSemanticPaths)
                    {
                        AddPath(path, paths);
                        AddPath(path, explicitSemanticPaths);
                    }

                    foreach (var glob in evaluated.Globs)
                    {
                        globs.Add(glob);
                    }
                }
                catch (Exception exception)
                {
                    // The semantic workspace has already loaded successfully.
                    // An auxiliary dependency probe must not turn a usable
                    // project into a crash. The incomplete flag also forces
                    // foreground refreshes to use an authoritative cold path.
                    isComplete = false;
                    diagnostics.Add(CreateDiscoveryDiagnostic(projectPath, exception));
                }
            }
        }

        infrastructurePaths.RemoveWhere(explicitSemanticPaths.Contains);

        return new ProjectInputDiscoveryResult(
            paths
                .OrderBy(path => path, IncrementalPaths.PathComparer)
                .ToImmutableArray(),
            isComplete,
            globs
                .OrderBy(glob => glob.ItemType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(glob => glob.CanonicalBaseDirectory, IncrementalPaths.PathComparer)
                .ThenBy(glob => glob.Pattern, StringComparer.Ordinal)
                .ToImmutableArray(),
            infrastructurePaths
                .OrderBy(path => path, IncrementalPaths.PathComparer)
                .ToImmutableArray(),
            explicitSemanticPaths
                .OrderBy(path => path, IncrementalPaths.PathComparer)
                .ToImmutableArray(),
            diagnostics
                .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static EvaluatedProjectInputs EvaluateProjectInputs(
        string projectPath,
        ProjectLoadRequest request)
    {
        MsBuildEnvironment.EnsureRegistered();
        var globalProperties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Configuration"] = request.Configuration,
            ["DesignTimeBuild"] = "true",
            ["BuildingProject"] = "false",
        };
        if (request.TargetFramework is not null)
        {
            globalProperties["TargetFramework"] = request.TargetFramework;
        }

        using var projectCollection = new ProjectCollection(globalProperties);
        var evaluatedProject = projectCollection.LoadProject(Path.GetFullPath(projectPath));
        var importPaths = evaluatedProject.Imports
            .Select(import => import.ImportedProject.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(IncrementalPaths.PathComparer)
            .ToArray();
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            ?? Directory.GetCurrentDirectory();
        var globDiscovery = DiscoverGlobs(evaluatedProject, projectDirectory);
        return new EvaluatedProjectInputs(
            importPaths,
            GetExplicitSemanticPaths(evaluatedProject, projectDirectory).ToArray(),
            globDiscovery.Globs,
            globDiscovery.IsComplete,
            globDiscovery.Diagnostics);
    }

    private static GlobDiscoveryResult DiscoverGlobs(
        BuildProject evaluatedProject,
        string projectDirectory)
    {
        var roots = new[] { evaluatedProject.Xml }
            .Concat(evaluatedProject.Imports.Select(import => import.ImportedProject))
            .DistinctBy(root => root.FullPath, IncrementalPaths.PathComparer)
            .ToArray();
        var msbuildToolsPath = evaluatedProject.GetPropertyValue("MSBuildToolsPath");
        var rulesByItemType = roots
            .SelectMany(root => root.Items.Select(item => new
            {
                Root = root,
                Item = item,
            }))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Item.Include))
            .GroupBy(entry => entry.Item.ItemType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => CreateRawItemRule(entry.Root, entry.Item, msbuildToolsPath))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var globs = new HashSet<ProjectInputGlob>();
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var isComplete = true;
        foreach (var root in roots)
        {
            foreach (var item in root.Items)
            {
                if (!IsPotentialSemanticInput(item.ItemType))
                {
                    continue;
                }

                var include = item.Include;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var resolution = ResolveCandidateRules(
                    item.ItemType,
                    CreateRawItemRule(root, item, msbuildToolsPath),
                    evaluatedProject,
                    rulesByItemType,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                isComplete &= resolution.IsComplete;
                diagnostics.UnionWith(resolution.Diagnostics);
                foreach (var glob in resolution.Globs)
                {
                    globs.Add(glob);
                }
            }
        }

        return new GlobDiscoveryResult(
            globs.ToArray(),
            isComplete,
            diagnostics.ToImmutableArray());
    }

    private static CandidateRuleResolution ResolveCandidateRules(
        string semanticItemType,
        RawItemRule rule,
        BuildProject evaluatedProject,
        IReadOnlyDictionary<string, RawItemRule[]> rulesByItemType,
        ISet<string> resolvingItemTypes)
    {
        var globs = new List<ProjectInputGlob>();
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var isComplete = true;
        var expandedInclude = evaluatedProject.ExpandString(rule.Include);
        var expandedExclude = string.IsNullOrWhiteSpace(rule.Exclude)
            ? string.Empty
            : evaluatedProject.ExpandString(rule.Exclude);
        if (!rule.IsSdkRule
            && RequiresFutureMembershipCoverage(semanticItemType)
            && ContainsDynamicReference(expandedExclude))
        {
            isComplete = false;
            diagnostics.Add(
                CreateIncompleteRuleDiagnostic(
                    semanticItemType,
                    rule,
                    "its exclusion expression could not be evaluated to a stable path rule"));
        }

        var directExcludes = SplitItemList(expandedExclude).ToImmutableArray();
        foreach (var pattern in SplitItemList(expandedInclude))
        {
            if (ContainsDynamicReference(pattern))
            {
                if (!rule.IsSdkRule
                    && RequiresFutureMembershipCoverage(semanticItemType))
                {
                    isComplete = false;
                    diagnostics.Add(
                        CreateIncompleteRuleDiagnostic(
                            semanticItemType,
                            rule,
                            "its include expression could not be evaluated to a stable path rule"));
                }

                continue;
            }

            if (ContainsWildcard(pattern))
            {
                globs.Add(CreateGlob(semanticItemType, evaluatedProject, pattern, directExcludes));
            }
        }

        foreach (var referencedItemType in ExtractItemVectorNames(rule.Include))
        {
            if (!resolvingItemTypes.Add(referencedItemType))
            {
                // Self-forwarding (for example the SDK's
                // <Compile Include="@(Compile)" />) terminates here. Any
                // direct wildcard rule for that item type has already been
                // collected by the outer resolution.
                continue;
            }

            if (!rulesByItemType.TryGetValue(referencedItemType, out var upstreamRules))
            {
                if (!rule.IsSdkRule
                    && RequiresFutureMembershipCoverage(semanticItemType)
                    && !string.Equals(referencedItemType, semanticItemType, StringComparison.OrdinalIgnoreCase))
                {
                    isComplete = false;
                    diagnostics.Add(
                        CreateIncompleteRuleDiagnostic(
                            semanticItemType,
                            rule,
                            $"item vector '@({referencedItemType})' has no evaluated provenance rule"));
                }

                resolvingItemTypes.Remove(referencedItemType);
                continue;
            }

            var upstreamHasCandidate = false;
            foreach (var upstreamRule in upstreamRules)
            {
                var upstreamResolution = ResolveCandidateRules(
                    semanticItemType,
                    upstreamRule,
                    evaluatedProject,
                    rulesByItemType,
                    resolvingItemTypes);
                if (upstreamResolution.Globs.Count > 0)
                {
                    upstreamHasCandidate = true;
                    foreach (var upstreamGlob in upstreamResolution.Globs)
                    {
                        globs.Add(upstreamGlob with
                        {
                            ExcludePatterns = upstreamGlob.ExcludePatterns
                                .Concat(directExcludes)
                                .Distinct(StringComparer.Ordinal)
                                .ToImmutableArray(),
                        });
                    }
                }

                isComplete &= upstreamResolution.IsComplete;
                diagnostics.UnionWith(upstreamResolution.Diagnostics);
            }

            if (!rule.IsSdkRule
                && RequiresFutureMembershipCoverage(semanticItemType)
                && !upstreamHasCandidate
                && !string.Equals(referencedItemType, semanticItemType, StringComparison.OrdinalIgnoreCase))
            {
                isComplete = false;
                diagnostics.Add(
                    CreateIncompleteRuleDiagnostic(
                        semanticItemType,
                        rule,
                        $"item vector '@({referencedItemType})' has no wildcard provenance for future inputs"));
            }

            resolvingItemTypes.Remove(referencedItemType);
        }

        if (!rule.IsSdkRule
            && RequiresFutureMembershipCoverage(semanticItemType)
            && ExtractItemVectorNames(rule.Include).Count == 0
            && ContainsDynamicReference(expandedInclude))
        {
            isComplete = false;
            diagnostics.Add(
                CreateIncompleteRuleDiagnostic(
                    semanticItemType,
                    rule,
                    "its include expression contains an unresolved MSBuild reference"));
        }

        return new CandidateRuleResolution(
            globs,
            isComplete,
            diagnostics.ToArray());
    }

    private static ProjectInputGlob CreateGlob(
        string itemType,
        BuildProject evaluatedProject,
        string pattern,
        ImmutableArray<string> excludePatterns) =>
        new(itemType, GetProjectDirectory(evaluatedProject), pattern)
        {
            ExcludePatterns = excludePatterns,
        };

    private static RawItemRule CreateRawItemRule(
        ProjectRootElement root,
        ProjectItemElement item,
        string msbuildToolsPath) =>
        new(
            root.FullPath,
            item.ItemType,
            item.Include!,
            item.Exclude,
            IsSdkFile(root.FullPath, msbuildToolsPath));

    private static string CreateIncompleteRuleDiagnostic(
        string semanticItemType,
        RawItemRule rule,
        string reason) =>
        $"Watcher input discovery was incomplete for item '{semanticItemType}' from '{IncrementalPaths.CanonicalAbsolutePath(rule.SourcePath)}': {reason}.";

    private static IEnumerable<string> GetExplicitSemanticPaths(
        BuildProject evaluatedProject,
        string projectDirectory) =>
        evaluatedProject.Items
            .Where(item => item.ItemType.Equals("AdditionalFiles", StringComparison.OrdinalIgnoreCase)
                || item.ItemType.Equals("AnalyzerConfig", StringComparison.OrdinalIgnoreCase)
                || item.ItemType.Equals("EditorConfigFiles", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EvaluatedInclude)
            .Where(include => !string.IsNullOrWhiteSpace(include)
                && !ContainsWildcard(include)
                && !ContainsDynamicReference(include))
            .Select(include => Path.IsPathRooted(include)
                ? include
                : Path.Combine(projectDirectory, include))
            .Select(IncrementalPaths.CanonicalAbsolutePath)
            .Distinct(IncrementalPaths.PathComparer);

    private static string GetProjectDirectory(BuildProject project) =>
        Path.GetDirectoryName(project.FullPath) ?? Directory.GetCurrentDirectory();

    private static IReadOnlyList<string> ExtractItemVectorNames(string value) =>
        Regex.Matches(value, @"@\(([^)]+)\)")
            .Select(match => match.Groups[1].Value.Split("->", 2, StringSplitOptions.TrimEntries)[0])
            .Where(name => !string.IsNullOrWhiteSpace(name)
                && !name.StartsWith("%(", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsSdkFile(string path, string msbuildToolsPath)
    {
        var canonicalPath = IncrementalPaths.CanonicalAbsolutePath(path);
        if (string.IsNullOrWhiteSpace(msbuildToolsPath))
        {
            return false;
        }

        var canonicalToolsPath = IncrementalPaths.CanonicalAbsolutePath(msbuildToolsPath);
        if (IncrementalPaths.IsUnderDirectory(canonicalPath, canonicalToolsPath))
        {
            return true;
        }

        // Workload SDKs and manifests live beside the selected SDK rather
        // than beneath MSBuildToolsPath. They are framework-owned inputs too;
        // treating their implementation item expressions as project input
        // rules would make ordinary SDK projects appear permanently
        // incomplete (for example AdditionalFiles=@(%(...))).
        var sdkDirectory = Directory.GetParent(canonicalToolsPath)?.FullName;
        var dotnetRoot = sdkDirectory is null
            ? null
            : Directory.GetParent(sdkDirectory)?.FullName;
        return sdkDirectory is not null
            && dotnetRoot is not null
            && (IncrementalPaths.IsUnderDirectory(canonicalPath, sdkDirectory)
                || IncrementalPaths.IsUnderDirectory(canonicalPath, Path.Combine(dotnetRoot, "packs"))
                || IncrementalPaths.IsUnderDirectory(canonicalPath, Path.Combine(dotnetRoot, "sdk-manifests")));
    }

    private static bool IsPotentialSemanticInput(string itemType) =>
        itemType.Equals("Compile", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("AdditionalFiles", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("Analyzer", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("AnalyzerConfig", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("EditorConfigFiles", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("Reference", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("FrameworkReference", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("PackageReference", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresFutureMembershipCoverage(string itemType) =>
        itemType.Equals("Compile", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("AdditionalFiles", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsDynamicReference(string value) =>
        value.Contains("$(", StringComparison.Ordinal)
        || value.Contains("@(", StringComparison.Ordinal)
        || value.Contains("%(", StringComparison.Ordinal);

    private static bool ContainsWildcard(string value) =>
        value.IndexOfAny(['*', '?']) >= 0;

    private static IEnumerable<string> SplitItemList(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CreateDiscoveryDiagnostic(string projectPath, Exception exception) =>
        $"Watcher input discovery was incomplete for project '{IncrementalPaths.CanonicalAbsolutePath(projectPath)}': {exception.Message}";

    private static IEnumerable<string> GetConventionalConfigurationPaths(string projectDirectory)
    {
        var directory = IncrementalPaths.CanonicalAbsolutePath(projectDirectory);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            foreach (var name in ConventionalConfigurationNames)
            {
                var candidate = Path.Combine(directory, name);
                // Track absent candidates too. A newly created ancestor
                // configuration file must invalidate the persisted fingerprint
                // and be observable by the exact-input inventory pass.
                yield return candidate;
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

    private static void AddPath(string? path, ISet<string> paths)
    {
        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path))
        {
            paths.Add(IncrementalPaths.CanonicalAbsolutePath(path));
        }
    }

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

    private sealed record EvaluatedProjectInputs(
        IReadOnlyList<string> ImportPaths,
        IReadOnlyList<string> ExplicitSemanticPaths,
        IReadOnlyList<ProjectInputGlob> Globs,
        bool IsComplete,
        IReadOnlyList<string> Diagnostics);

    private sealed record RawItemRule(
        string SourcePath,
        string ItemType,
        string Include,
        string? Exclude,
        bool IsSdkRule);

    private sealed record CandidateRuleResolution(
        IReadOnlyList<ProjectInputGlob> Globs,
        bool IsComplete,
        IReadOnlyList<string> Diagnostics);

    private sealed record GlobDiscoveryResult(
        IReadOnlyList<ProjectInputGlob> Globs,
        bool IsComplete,
        IReadOnlyList<string> Diagnostics);
}
