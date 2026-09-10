using Graphify.CSharp.Domain;
using Graphify.CSharp.Incremental;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

internal sealed class IncrementalProjectFingerprintBuilder
{
    public IReadOnlyDictionary<string, ProjectFingerprint> BuildAll(LoadedSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var fingerprints = new Dictionary<string, ProjectFingerprint>(StringComparer.Ordinal);
        foreach (var project in solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
        {
            fingerprints.Add(project.Identity.Key, Build(solution, project));
        }

        return fingerprints;
    }

    public ProjectFingerprint Build(LoadedSolution solution, AnalyzedProject project, bool includeContentHashes = false)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(project);

        var sourceFiles = project.Project.Documents
            .Select(document => document.FilePath)
            .OfType<string>()
            .Select(Path.GetFullPath)
            .Distinct(GetPathComparer())
            .OrderBy(path => path, GetPathComparer())
            .Select(path => SourceFingerprint.FromFile(path, solution.RepositoryRoot, includeContentHashes));
        var projectFile = FindProjectFile(solution, project.Identity);
        var references = project.Project.ProjectReferences
            .Select(reference => FindReferenceKey(solution, reference))
            .OfType<string>()
            .OrderBy(key => key, StringComparer.Ordinal);

        return new ProjectFingerprint(
            project.Identity,
            projectFile is null
                ? null
                : SourceFingerprint.FromFile(projectFile, solution.RepositoryRoot, includeContentHashes),
            sourceFiles,
            references);
    }

    private static string? FindProjectFile(LoadedSolution solution, ProjectIdentity identity)
    {
        if (!identity.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projectPath = Path.GetFullPath(
            Path.Combine(
                solution.RepositoryRoot,
                identity.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(projectPath) ? projectPath : null;
    }

    private static string? FindReferenceKey(LoadedSolution solution, ProjectReference reference)
    {
        var referencedProject = solution.Projects.FirstOrDefault(project => project.Project.Id == reference.ProjectId);
        if (referencedProject is null)
        {
            return null;
        }

        var aliases = string.Join(",", reference.Aliases.OrderBy(alias => alias, StringComparer.Ordinal));
        return string.Join(
            '\u001F',
            referencedProject.Identity.Key,
            $"aliases={CanonicalText.Escape(aliases)}");
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
