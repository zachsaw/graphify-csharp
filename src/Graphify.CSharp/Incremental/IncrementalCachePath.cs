namespace Graphify.CSharp.Incremental;

internal static class IncrementalCachePath
{
    public static string ForOutput(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException($"Output path '{outputPath}' has no parent directory.");
        var outputIdentity = IncrementalHashing.Sha256(IncrementalPaths.CanonicalAbsolutePath(outputPath));
        return Path.Combine(outputDirectory, ".graphify-csharp", $"manifest-{outputIdentity}.json");
    }
}
