namespace Graphify.CSharp.Domain;

public enum CallerClassification
{
    Production,
    Test,
    External,
}

public sealed record NamespaceTestPolicy
{
    public NamespaceTestPolicy(string testNamespaceSegment = "Tests")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testNamespaceSegment);
        if (testNamespaceSegment.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("The test namespace must be a single namespace segment.", nameof(testNamespaceSegment));
        }

        TestNamespaceSegment = testNamespaceSegment.Trim();
    }

    public string TestNamespaceSegment { get; }

    public CallerClassification Classify(string? namespaceName)
    {
        var normalized = CanonicalText.NormalizeNamespace(namespaceName);
        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(segment => string.Equals(segment, TestNamespaceSegment, StringComparison.Ordinal))
            ? CallerClassification.Test
            : CallerClassification.Production;
    }
}
