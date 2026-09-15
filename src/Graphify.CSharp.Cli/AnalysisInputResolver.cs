namespace Graphify.CSharp.Cli;

internal static class AnalysisInputResolver
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];

    public static string CurrentDirectory(string? currentDirectory = null)
    {
        var value = string.IsNullOrWhiteSpace(currentDirectory)
            ? Directory.GetCurrentDirectory()
            : currentDirectory;
        return ResolvePath(value, Directory.GetCurrentDirectory(), "current directory");
    }

    public static string ResolveRoot(string? root, string currentDirectory)
    {
        if (root is not null && string.IsNullOrWhiteSpace(root))
        {
            throw new CommandLineException("The '--root' path cannot be empty.");
        }

        return ResolvePath(root ?? currentDirectory, currentDirectory, "root");
    }

    public static string ResolveInput(
        string? input,
        string repositoryRoot,
        string currentDirectory,
        string commandName)
    {
        if (input is not null)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new CommandLineException($"The '{commandName}' input path cannot be empty.");
            }

            // Explicit paths are caller-relative. Root controls analysis scope
            // and discovery, but does not silently rebase a user-supplied path.
            return ResolvePath(input, currentDirectory, "input");
        }

        return DiscoverInput(repositoryRoot);
    }

    public static string ResolvePath(string path, string baseDirectory, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CommandLineException($"The {description} path cannot be empty.");
        }

        try
        {
            return Path.GetFullPath(path, baseDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException)
        {
            throw new CommandLineException(
                $"The {description} path '{path}' is invalid: {exception.Message}");
        }
    }

    private static string DiscoverInput(string repositoryRoot)
    {
        string[] files;
        try
        {
            files = Directory
                .EnumerateFiles(repositoryRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new CommandLineException(
                $"Could not inspect the input discovery root '{repositoryRoot}': {exception.Message}");
        }

        var solutions = files
            .Where(path => SolutionExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (solutions.Length > 1)
        {
            throw new CommandLineException(
                $"Input discovery found multiple solutions directly under '{repositoryRoot}': "
                + $"{FormatNames(solutions)}. Supply '--input <path>'.");
        }

        if (solutions.Length == 1)
        {
            return solutions[0];
        }

        var projects = files
            .Where(path => string.Equals(
                Path.GetExtension(path),
                ".csproj",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (projects.Length == 1)
        {
            return projects[0];
        }

        if (projects.Length > 1)
        {
            throw new CommandLineException(
                $"Input discovery found multiple projects directly under '{repositoryRoot}': "
                + $"{FormatNames(projects)}. Supply '--input <path>'.");
        }

        throw new CommandLineException(
            $"Input discovery found no .sln, .slnx, or .csproj directly under '{repositoryRoot}'. "
            + "Supply '--input <path>'.");
    }

    private static string FormatNames(IEnumerable<string> paths) =>
        string.Join(
            ", ",
            paths.Select(path => $"'{Path.GetFileName(path)}'"));
}
