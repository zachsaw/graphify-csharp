using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Graphify.CSharp.Roslyn;

internal sealed class FileBasedAppProject : IDisposable
{
    private readonly string _temporaryDirectory;
    private readonly IReadOnlyDictionary<string, string> _sourcePaths;
    private bool _disposed;

    private FileBasedAppProject(
        string temporaryDirectory,
        string projectPath,
        IReadOnlyDictionary<string, string> sourcePaths)
    {
        _temporaryDirectory = temporaryDirectory;
        ProjectPath = projectPath;
        _sourcePaths = sourcePaths;
    }

    public string ProjectPath { get; }

    public static async Task<FileBasedAppProject> CreateAsync(
        string inputPath,
        string? targetFramework,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"graphify-csharp-file-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(temporaryDirectory, "converted");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            await RunDotnetAsync(
                ["project", "convert", Path.GetFullPath(inputPath), "--output", outputDirectory],
                inputPath,
                cancellationToken).ConfigureAwait(false);

            var projectPath = FindSingleProject(outputDirectory);
            var sourcePaths = BuildSourceMap(Path.GetFullPath(inputPath), outputDirectory);
            RewriteProjectReferences(projectPath, Path.GetFullPath(inputPath));

            await RunDotnetAsync(
                RestoreArguments(projectPath, targetFramework),
                inputPath,
                cancellationToken).ConfigureAwait(false);

            return new FileBasedAppProject(temporaryDirectory, projectPath, sourcePaths);
        }
        catch
        {
            DeleteTemporaryDirectory(temporaryDirectory);
            throw;
        }
    }

    public async Task<Solution> RemapDocumentsAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var remapped = solution;
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.FilePath is null
                    || !_sourcePaths.TryGetValue(Path.GetFullPath(document.FilePath), out var sourcePath))
                {
                    continue;
                }

                var sourceText = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                remapped = remapped
                    .WithDocumentFilePath(document.Id, sourcePath)
                    .WithDocumentText(document.Id, SourceText.From(RemoveFileDirectives(sourceText)));
            }
        }

        return remapped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteTemporaryDirectory(_temporaryDirectory);
    }

    private static IReadOnlyList<string> RestoreArguments(string projectPath, string? targetFramework)
    {
        var arguments = new List<string> { "restore", projectPath, "--nologo" };
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            arguments.Add($"--property:TargetFramework={targetFramework.Trim()}");
        }

        return arguments;
    }

    private static string FindSingleProject(string outputDirectory)
    {
        var projects = Directory.EnumerateFiles(outputDirectory, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new InvalidOperationException(
                $"The .NET SDK did not generate a project for file-based app '{outputDirectory}'."),
            _ => throw new InvalidOperationException(
                $"The .NET SDK generated multiple projects for file-based app '{outputDirectory}'."),
        };
    }

    private static IReadOnlyDictionary<string, string> BuildSourceMap(string inputPath, string outputDirectory)
    {
        var originalSources = DiscoverIncludedSources(inputPath);
        var generatedSources = Directory.EnumerateFiles(outputDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var remaining = new HashSet<string>(generatedSources, StringComparer.Ordinal);
        var generatedByContent = generatedSources
            .GroupBy(path => NormalizeForGeneratedComparison(File.ReadAllText(path)), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(path => path, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var generatedByName = generatedSources
            .GroupBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(path => path, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var originalSource in originalSources)
        {
            var normalizedOriginal = NormalizeForGeneratedComparison(File.ReadAllText(originalSource));
            var generated = TakeFirstRemaining(generatedByContent, normalizedOriginal, remaining)
                ?? TakeFirstRemaining(generatedByName, Path.GetFileName(originalSource), remaining);
            if (generated is null)
            {
                continue;
            }

            mappings[Path.GetFullPath(generated)] = Path.GetFullPath(originalSource);
        }

        return mappings;
    }

    private static string? TakeFirstRemaining(
        IReadOnlyDictionary<string, List<string>> candidates,
        string key,
        ISet<string> remaining)
    {
        if (!candidates.TryGetValue(key, out var paths))
        {
            return null;
        }

        foreach (var path in paths)
        {
            if (remaining.Remove(path))
            {
                return path;
            }
        }

        return null;
    }

    private static void RewriteProjectReferences(string projectPath, string inputPath)
    {
        var projectReferences = DiscoverProjectReferences(inputPath);
        if (projectReferences.Count == 0)
        {
            return;
        }

        var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var projectReferenceElements = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .ToArray();
        for (var index = 0; index < projectReferences.Count; index++)
        {
            if (index < projectReferenceElements.Length)
            {
                projectReferenceElements[index].SetAttributeValue("Include", projectReferences[index]);
                continue;
            }

            var itemGroup = document.Root!.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "ItemGroup")
                ?? new XElement(document.Root.Name.Namespace + "ItemGroup");
            if (itemGroup.Parent is null)
            {
                document.Root.Add(itemGroup);
            }

            itemGroup.Add(new XElement(
                document.Root.Name.Namespace + "ProjectReference",
                new XAttribute("Include", projectReferences[index])));
        }

        document.Save(projectPath, SaveOptions.DisableFormatting);
    }

    private static IReadOnlyList<string> DiscoverProjectReferences(string inputPath)
    {
        var references = new List<string>();
        foreach (var source in DiscoverIncludedSources(inputPath))
        {
            foreach (var line in File.ReadLines(source))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("#:project", StringComparison.Ordinal)
                    || (trimmed.Length > "#:project".Length
                        && !char.IsWhiteSpace(trimmed["#:project".Length])))
                {
                    continue;
                }

                var projectPath = trimmed["#:project".Length..].Trim();
                if (projectPath.Length >= 2
                    && projectPath[0] == '"'
                    && projectPath[^1] == '"')
                {
                    projectPath = projectPath[1..^1];
                }

                if (projectPath.Length > 0)
                {
                    references.Add(Path.GetFullPath(projectPath, Path.GetDirectoryName(source)!));
                }
            }
        }

        return references;
    }

    private static IReadOnlyList<string> DiscoverIncludedSources(string inputPath)
    {
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<string>();
        queue.Enqueue(Path.GetFullPath(inputPath));
        while (queue.Count > 0)
        {
            var source = queue.Dequeue();
            if (!seen.Add(source))
            {
                continue;
            }

            sources.Add(source);
            foreach (var line in File.ReadLines(source))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("#:include", StringComparison.Ordinal)
                    || (trimmed.Length > "#:include".Length
                        && !char.IsWhiteSpace(trimmed["#:include".Length])))
                {
                    continue;
                }

                var includePath = trimmed["#:include".Length..].Trim();
                if (includePath.Length >= 2
                    && includePath[0] == '"'
                    && includePath[^1] == '"')
                {
                    includePath = includePath[1..^1];
                }

                if (includePath.Length == 0)
                {
                    continue;
                }

                var resolved = Path.GetFullPath(includePath, Path.GetDirectoryName(source)!);
                if (File.Exists(resolved))
                {
                    queue.Enqueue(resolved);
                }
            }
        }

        return sources;
    }

    private static bool IsBuildArtifact(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj");
    }

    private static string NormalizeForGeneratedComparison(string text)
    {
        var normalized = new StringBuilder(text.Length);
        using var reader = new StringReader(text);
        string? line;
        var hasContent = false;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#!", StringComparison.Ordinal)
                || trimmed.StartsWith("#:", StringComparison.Ordinal))
            {
                continue;
            }

            if (!hasContent && line.Length == 0)
            {
                continue;
            }

            normalized.AppendLine(line);
            hasContent = true;
        }

        return normalized.ToString();
    }

    private static string RemoveFileDirectives(string text)
    {
        var output = new StringBuilder(text.Length);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#!", StringComparison.Ordinal)
                && !trimmed.StartsWith("#:", StringComparison.Ordinal))
            {
                output.AppendLine(line);
            }
            else
            {
                output.AppendLine();
            }
        }

        return output.ToString();
    }

    private static async Task RunDotnetAsync(
        IReadOnlyList<string> arguments,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The .NET SDK process could not be started for a file-based app.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var details = string.Join(
                Environment.NewLine,
                new[] { output.Trim(), error.Trim() }.Where(text => text.Length > 0));
            throw new InvalidOperationException(
                $"The .NET SDK could not process file-based app '{Path.GetFullPath(inputPath)}'."
                + (details.Length == 0 ? string.Empty : $"{Environment.NewLine}{details}"));
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
