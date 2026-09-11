using System.Collections.Immutable;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal sealed record ProjectFingerprint
{
    public ProjectFingerprint(
        ProjectIdentity project,
        SourceFingerprint? projectFile,
        IEnumerable<SourceFingerprint>? sourceFiles,
        IEnumerable<string>? projectReferenceKeys,
        IEnumerable<SourceFingerprint>? dependencyFiles = null,
        bool dependencyDiscoveryComplete = true,
        string? compilationOptionsKey = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        ProjectFile = projectFile;
        var normalizedSourceFiles = (sourceFiles ?? Array.Empty<SourceFingerprint>())
            .Select(file => file ?? throw new ArgumentException("Source fingerprint cannot be null.", nameof(sourceFiles)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (normalizedSourceFiles
            .GroupBy(file => file.RelativePath, StringComparer.Ordinal)
            .Any(group => group.Select(file => file.CanonicalForm).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            throw new ArgumentException("A project fingerprint cannot contain conflicting entries for one source path.", nameof(sourceFiles));
        }

        SourceFiles = normalizedSourceFiles
            .Distinct()
            .ToImmutableArray();
        var normalizedDependencyFiles = (dependencyFiles ?? Array.Empty<SourceFingerprint>())
            .Select(file => file ?? throw new ArgumentException("Dependency fingerprint cannot be null.", nameof(dependencyFiles)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (normalizedDependencyFiles
            .GroupBy(file => file.RelativePath, StringComparer.Ordinal)
            .Any(group => group.Select(file => file.CanonicalForm).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            throw new ArgumentException("A project fingerprint cannot contain conflicting dependency entries for one path.", nameof(dependencyFiles));
        }

        DependencyFiles = normalizedDependencyFiles
            .Distinct()
            .ToImmutableArray();
        DependencyDiscoveryComplete = dependencyDiscoveryComplete;
        CompilationOptionsKey = compilationOptionsKey ?? string.Empty;
        ProjectReferenceKeys = (projectReferenceKeys ?? Array.Empty<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToImmutableArray();
        CanonicalKey = BuildCanonicalKey();
        Digest = IncrementalHashing.Sha256(CanonicalKey);
    }

    public ProjectIdentity Project { get; }

    public SourceFingerprint? ProjectFile { get; }

    public ImmutableArray<SourceFingerprint> SourceFiles { get; }

    public ImmutableArray<string> ProjectReferenceKeys { get; }

    public ImmutableArray<SourceFingerprint> DependencyFiles { get; }

    public bool DependencyDiscoveryComplete { get; }

    public string CompilationOptionsKey { get; }

    public string CanonicalKey { get; }

    public string Digest { get; }

    public FingerprintComparison CompareTo(ProjectFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Project.Key, other.Project.Key, StringComparison.Ordinal)
            || !ProjectReferenceKeys.SequenceEqual(other.ProjectReferenceKeys, StringComparer.Ordinal)
            || !DependencyDiscoveryComplete
            || !other.DependencyDiscoveryComplete
            || !string.Equals(CompilationOptionsKey, other.CompilationOptionsKey, StringComparison.Ordinal))
        {
            return FingerprintComparison.Different;
        }

        var comparison = CompareOptional(ProjectFile, other.ProjectFile);
        if (comparison == FingerprintComparison.Different)
        {
            return comparison;
        }

        if (SourceFiles.Length != other.SourceFiles.Length)
        {
            return FingerprintComparison.Different;
        }

        foreach (var (source, otherSource) in SourceFiles.Zip(other.SourceFiles))
        {
            comparison = Combine(comparison, source.CompareTo(otherSource));
            if (comparison == FingerprintComparison.Different)
            {
                return comparison;
            }
        }

        if (DependencyFiles.Length != other.DependencyFiles.Length)
        {
            return FingerprintComparison.Different;
        }

        foreach (var (dependency, otherDependency) in DependencyFiles.Zip(other.DependencyFiles))
        {
            comparison = Combine(comparison, dependency.CompareTo(otherDependency));
            if (comparison == FingerprintComparison.Different)
            {
                return comparison;
            }
        }

        return comparison;
    }

    private string BuildCanonicalKey()
    {
        var projectFile = ProjectFile?.CanonicalForm ?? "<none>";
        var sources = string.Join('\u001E', SourceFiles.Select(file => file.CanonicalForm));
        var dependencies = string.Join('\u001E', DependencyFiles.Select(file => file.CanonicalForm));
        var references = string.Join('\u001E', ProjectReferenceKeys.Select(CanonicalText.Escape));
        return string.Join(
            '\u001D',
            Project.Key,
            projectFile,
            sources,
            dependencies,
            DependencyDiscoveryComplete ? "dependency-discovery-complete" : "dependency-discovery-incomplete",
            CanonicalText.Escape(CompilationOptionsKey),
            references);
    }

    private static FingerprintComparison CompareOptional(SourceFingerprint? first, SourceFingerprint? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null
                ? FingerprintComparison.ContentMatch
                : FingerprintComparison.Different;
        }

        return first.CompareTo(second);
    }

    private static FingerprintComparison Combine(FingerprintComparison first, FingerprintComparison second) =>
        first == FingerprintComparison.Different || second == FingerprintComparison.Different
            ? FingerprintComparison.Different
            : first == FingerprintComparison.MetadataMatch || second == FingerprintComparison.MetadataMatch
                ? FingerprintComparison.MetadataMatch
                : FingerprintComparison.ContentMatch;
}
