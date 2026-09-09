using System.Security.Cryptography;
using System.Text;

namespace Graphify.CSharp.Incremental;

internal static class IncrementalHashing
{
    public static string Sha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool IsSha256(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == SHA256.HashSizeInBytes * 2
        && value.All(Uri.IsHexDigit);
}
