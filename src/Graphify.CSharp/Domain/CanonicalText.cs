namespace Graphify.CSharp.Domain;

internal static class CanonicalText
{
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal);
    }

    public static string NormalizeNamespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("global::", StringComparison.Ordinal))
        {
            normalized = normalized[8..];
        }

        return string.Join(
            ".",
            normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string NormalizeType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Where(character => !char.IsWhiteSpace(character))
            .Aggregate(new System.Text.StringBuilder(), static (builder, character) => builder.Append(character))
            .ToString();
    }

    public static string NormalizePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}
