using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal sealed record RefreshRequestIdentity
{
    public const string CurrentCacheSchemaVersion = "graphify-csharp/incremental-cache/v1";
    public const string CurrentGraphSchemaVersion = "csharp/v1";
    public const string CurrentExtractorVersion = "csharp/v1";

    public RefreshRequestIdentity(
        string inputPath,
        string repositoryRoot,
        string configuration,
        string? targetFramework,
        string graphSchemaVersion = CurrentGraphSchemaVersion,
        string extractorVersion = CurrentExtractorVersion,
        string cacheSchemaVersion = CurrentCacheSchemaVersion)
    {
        InputPath = IncrementalPaths.CanonicalAbsolutePath(inputPath);
        RepositoryRoot = IncrementalPaths.CanonicalAbsolutePath(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractorVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheSchemaVersion);

        Configuration = configuration.Trim();
        TargetFramework = string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework.Trim();
        GraphSchemaVersion = graphSchemaVersion.Trim();
        ExtractorVersion = extractorVersion.Trim();
        CacheSchemaVersion = cacheSchemaVersion.Trim();
        CanonicalKey = string.Join(
            '\u001F',
            $"input={CanonicalText.Escape(InputPath)}",
            $"root={CanonicalText.Escape(RepositoryRoot)}",
            $"configuration={CanonicalText.Escape(Configuration)}",
            $"tfm={CanonicalText.Escape(TargetFramework ?? string.Empty)}",
            $"graph={CanonicalText.Escape(GraphSchemaVersion)}",
            $"extractor={CanonicalText.Escape(ExtractorVersion)}",
            $"cache={CanonicalText.Escape(CacheSchemaVersion)}");
        Digest = IncrementalHashing.Sha256(CanonicalKey);
    }

    public string InputPath { get; }

    public string RepositoryRoot { get; }

    public string Configuration { get; }

    public string? TargetFramework { get; }

    public string GraphSchemaVersion { get; }

    public string ExtractorVersion { get; }

    public string CacheSchemaVersion { get; }

    public string CanonicalKey { get; }

    public string Digest { get; }

    public bool Matches(RefreshRequestIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(CanonicalKey, other.CanonicalKey, StringComparison.Ordinal);
    }
}
