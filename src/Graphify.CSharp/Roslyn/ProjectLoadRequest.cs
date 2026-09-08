namespace Graphify.CSharp.Roslyn;

public sealed record ProjectLoadRequest
{
    public ProjectLoadRequest(
        string inputPath,
        string? repositoryRoot = null,
        string configuration = "Debug",
        string? targetFramework = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        InputPath = Path.GetFullPath(inputPath);
        RepositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(repositoryRoot);
        Configuration = configuration.Trim();
        TargetFramework = string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework.Trim();
    }

    public string InputPath { get; }

    public string RepositoryRoot { get; }

    public string Configuration { get; }

    public string? TargetFramework { get; }
}
