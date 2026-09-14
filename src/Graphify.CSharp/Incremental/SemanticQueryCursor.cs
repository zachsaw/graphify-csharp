using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Graphify.CSharp.Incremental;

internal sealed record SemanticCursorPayload(
    int Version,
    string SnapshotId,
    string QueryHash,
    string LastKey);

internal static class SemanticQueryCursor
{
    private const int Version = 1;

    public static string Create(
        SemanticEvidenceView view,
        SemanticQuerySpec specification,
        string lastKey)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastKey);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new SemanticCursorPayload(Version, view.SnapshotId, specification.QueryHash, lastKey),
            SemanticQueryJson.SerializerOptions);
        var signature = HMACSHA256.HashData(view.CursorSecret, payload);
        var cursor = Base64Url(payload) + "." + Base64Url(signature);
        if (Encoding.UTF8.GetByteCount(cursor) > SemanticQueryProtocol.MaximumCursorBytes)
        {
            throw new SemanticQueryException(
                "response_too_large",
                "The semantic continuation cursor exceeded its size limit.");
        }

        return cursor;
    }

    public static SemanticCursorPayload? Read(
        string? encoded,
        SemanticEvidenceView view,
        SemanticQuerySpec specification)
    {
        if (encoded is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(encoded)
            || Encoding.UTF8.GetByteCount(encoded) > SemanticQueryProtocol.MaximumCursorBytes)
        {
            throw new SemanticQueryException("invalid_cursor", "The semantic cursor is empty or too large.");
        }

        var separator = encoded.IndexOf('.');
        if (separator <= 0 || separator == encoded.Length - 1 || encoded.IndexOf('.', separator + 1) >= 0)
        {
            throw new SemanticQueryException("invalid_cursor", "The semantic cursor has an invalid shape.");
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = FromBase64Url(encoded[..separator]);
            signature = FromBase64Url(encoded[(separator + 1)..]);
        }
        catch (FormatException exception)
        {
            throw new SemanticQueryException("invalid_cursor", $"The semantic cursor encoding is invalid: {exception.Message}");
        }

        var expected = HMACSHA256.HashData(view.CursorSecret, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            throw new SemanticQueryException("invalid_cursor", "The semantic cursor signature is invalid.");
        }

        SemanticCursorPayload? cursor;
        try
        {
            cursor = JsonSerializer.Deserialize<SemanticCursorPayload>(payload, SemanticQueryJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new SemanticQueryException("invalid_cursor", $"The semantic cursor payload is invalid: {exception.Message}");
        }

        if (cursor is null
            || cursor.Version != Version
            || string.IsNullOrWhiteSpace(cursor.SnapshotId)
            || string.IsNullOrWhiteSpace(cursor.QueryHash)
            || string.IsNullOrWhiteSpace(cursor.LastKey))
        {
            throw new SemanticQueryException("invalid_cursor", "The semantic cursor payload is incomplete.");
        }

        if (!string.Equals(cursor.SnapshotId, view.SnapshotId, StringComparison.Ordinal))
        {
            throw new SemanticQueryException(
                "stale_snapshot",
                "The semantic cursor belongs to an expired evidence snapshot.");
        }

        if (!string.Equals(cursor.QueryHash, specification.QueryHash, StringComparison.Ordinal))
        {
            throw new SemanticQueryException(
                "invalid_cursor",
                "The semantic cursor belongs to a different query.");
        }

        return cursor;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        standard += new string('=', (4 - (standard.Length % 4)) % 4);
        return Convert.FromBase64String(standard);
    }
}
