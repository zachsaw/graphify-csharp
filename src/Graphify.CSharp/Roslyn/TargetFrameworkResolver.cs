using Microsoft.Build.Evaluation;

namespace Graphify.CSharp.Roslyn;

public sealed class TargetFrameworkResolver
{
    public string Resolve(
        string projectPath,
        string configuration,
        string? requestedTargetFramework = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        if (!string.IsNullOrWhiteSpace(requestedTargetFramework))
        {
            return requestedTargetFramework.Trim();
        }

        MsBuildEnvironment.EnsureRegistered();
        return ResolveEvaluatedProject(projectPath, configuration);
    }

    private static string ResolveEvaluatedProject(string projectPath, string configuration)
    {
        var globalProperties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Configuration"] = configuration.Trim(),
            ["DesignTimeBuild"] = "true",
            ["BuildingProject"] = "false",
        };
        using var projectCollection = new ProjectCollection(globalProperties);
        var project = projectCollection.LoadProject(Path.GetFullPath(projectPath));

        var targetFrameworks = Split(project.GetPropertyValue("TargetFrameworks"));
        if (targetFrameworks.Length > 1)
        {
            throw new InvalidOperationException(
                $"Project '{projectPath}' targets multiple frameworks ({string.Join(", ", targetFrameworks)}). "
                + "Pass --target-framework <tfm> to select one graph.");
        }

        var targetFramework = project.GetPropertyValue("TargetFramework").Trim();
        return targetFrameworks.FirstOrDefault()
            ?? (targetFramework.Length == 0 ? "unknown" : targetFramework);
    }

    private static string[] Split(string value) => value
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal)
        .ToArray();
}
