namespace Graphify.CSharp.Roslyn;

public sealed class SolutionLoadException : Exception
{
    public const string ErrorCode = "solution_load_failed";

    public SolutionLoadException(string solutionPath, Exception innerException)
        : base(CreateMessage(solutionPath, innerException), innerException)
    {
        SolutionPath = Path.GetFullPath(solutionPath);
    }

    public string SolutionPath { get; }

    private static string CreateMessage(string solutionPath, Exception innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        ArgumentNullException.ThrowIfNull(innerException);

        var detail = string.IsNullOrWhiteSpace(innerException.Message)
            ? innerException.GetType().Name
            : innerException.Message;
        return $"Could not load solution '{Path.GetFullPath(solutionPath)}': {detail}";
    }
}
