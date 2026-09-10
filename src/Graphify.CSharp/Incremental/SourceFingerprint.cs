using System.Globalization;
using System.Security.Cryptography;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal enum FingerprintComparison
{
    Different,
    MetadataMatch,
    ContentMatch,
}

internal sealed record SourceFingerprint
{
    public SourceFingerprint(
        string relativePath,
        bool exists,
        long length,
        long lastWriteTimeUtcTicks,
        string? contentSha256 = null)
    {
        RelativePath = CanonicalText.NormalizePath(relativePath);
        Exists = exists;
        if (exists)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            ArgumentOutOfRangeException.ThrowIfNegative(lastWriteTimeUtcTicks);
            ContentSha256 = NormalizeHash(contentSha256);
            Length = length;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        }
        else
        {
            if (length != 0 || lastWriteTimeUtcTicks != 0 || contentSha256 is not null)
            {
                throw new ArgumentException(
                    "A missing source must have zero metadata and no content hash.",
                    nameof(exists));
            }

            Length = 0;
            LastWriteTimeUtcTicks = 0;
            ContentSha256 = null;
        }
    }

    public string RelativePath { get; }

    public bool Exists { get; }

    public long Length { get; }

    public long LastWriteTimeUtcTicks { get; }

    public string? ContentSha256 { get; }

    public bool HasContentHash => ContentSha256 is not null;

    public static SourceFingerprint FromFile(
        string fullPath,
        string repositoryRoot,
        bool includeContentHash = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var relativePath = IncrementalPaths.CanonicalRelativePath(fullPath, repositoryRoot);
        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                return new SourceFingerprint(relativePath, exists: false, 0, 0);
            }

            var contentSha256 = includeContentHash ? IncrementalHashing.Sha256File(fileInfo.FullName) : null;
            return new SourceFingerprint(
                relativePath,
                exists: true,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                contentSha256);
        }
        catch (FileNotFoundException)
        {
            return new SourceFingerprint(relativePath, exists: false, 0, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new SourceFingerprint(relativePath, exists: false, 0, 0);
        }
    }

    public FingerprintComparison CompareTo(SourceFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(RelativePath, other.RelativePath, StringComparison.Ordinal)
            || Exists != other.Exists
            || Length != other.Length
            || LastWriteTimeUtcTicks != other.LastWriteTimeUtcTicks)
        {
            return FingerprintComparison.Different;
        }

        if (ContentSha256 is not null && other.ContentSha256 is not null)
        {
            return string.Equals(ContentSha256, other.ContentSha256, StringComparison.Ordinal)
                ? FingerprintComparison.ContentMatch
                : FingerprintComparison.Different;
        }

        return FingerprintComparison.MetadataMatch;
    }

    public string CanonicalForm => string.Join(
        '\u001F',
        CanonicalText.Escape(RelativePath),
        Exists ? "1" : "0",
        Length.ToString(CultureInfo.InvariantCulture),
        LastWriteTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
        CanonicalText.Escape(ContentSha256 ?? string.Empty));

    private static string? NormalizeHash(string? contentSha256)
    {
        if (string.IsNullOrWhiteSpace(contentSha256))
        {
            return null;
        }

        var normalized = contentSha256.Trim().ToLowerInvariant();
        if (normalized.Length != SHA256.HashSizeInBytes * 2 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("ContentSha256 must be a SHA-256 hexadecimal digest.", nameof(contentSha256));
        }

        return normalized;
    }
}
