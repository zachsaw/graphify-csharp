namespace Graphify.CSharp.Domain;

public sealed record ProjectIdentity
{
    public ProjectIdentity(string relativePath, string? targetFramework = null)
    {
        RelativePath = CanonicalText.NormalizePath(relativePath);
        TargetFramework = string.IsNullOrWhiteSpace(targetFramework) ? "unknown" : targetFramework.Trim();
    }

    public string RelativePath { get; }

    public string TargetFramework { get; }

    public string Key => $"project={CanonicalText.Escape(RelativePath)}|tfm={CanonicalText.Escape(TargetFramework)}";

    public static ProjectIdentity FromPath(
        string projectPath,
        string? repositoryRoot = null,
        string? targetFramework = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var normalizedProjectPath = NormalizePathForRepository(projectPath, repositoryRoot);
        return new ProjectIdentity(normalizedProjectPath, targetFramework);
    }

    private static string NormalizePathForRepository(string projectPath, string? repositoryRoot)
    {
        var platformProjectPath = projectPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullProjectPath = Path.GetFullPath(platformProjectPath);

        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return CanonicalText.NormalizePath(fullProjectPath);
        }

        var platformRoot = repositoryRoot.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(platformRoot);
        var relativePath = Path.GetRelativePath(fullRoot, fullProjectPath);
        return CanonicalText.NormalizePath(relativePath);
    }
}
