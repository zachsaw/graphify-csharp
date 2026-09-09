namespace Graphify.CSharp.Cli;

public sealed class CommandLineOptions
{
    private CommandLineOptions(
        string inputPath,
        string repositoryRoot,
        string outputPath,
        string configuration,
        string? targetFramework,
        bool showHelp)
    {
        InputPath = inputPath;
        RepositoryRoot = repositoryRoot;
        OutputPath = outputPath;
        Configuration = configuration;
        TargetFramework = targetFramework;
        ShowHelp = showHelp;
    }

    public string InputPath { get; }

    public string RepositoryRoot { get; }

    public string OutputPath { get; }

    public string Configuration { get; }

    public string? TargetFramework { get; }

    public bool ShowHelp { get; }

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positionalInput = (string?)null;
        var showHelp = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (positionalInput is not null)
                {
                    throw new CommandLineException($"Unexpected positional argument '{argument}'.");
                }

                positionalInput = argument;
                continue;
            }

            var key = argument switch
            {
                "--input" or "-i" => "input",
                "--root" or "-r" => "root",
                "--output" or "-o" => "output",
                "--configuration" or "-c" => "configuration",
                "--target-framework" or "-f" => "target-framework",
                _ => throw new CommandLineException($"Unknown option '{argument}'."),
            };
            if (++index >= args.Count || args[index].StartsWith("-", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Option '{argument}' requires a value.");
            }

            if (!values.TryGetValue(key, out var entries))
            {
                entries = [];
                values[key] = entries;
            }

            entries.Add(args[index]);
        }

        if (showHelp)
        {
            return new CommandLineOptions(
                inputPath: string.Empty,
                repositoryRoot: Directory.GetCurrentDirectory(),
                outputPath: string.Empty,
                configuration: "Debug",
                targetFramework: null,
                showHelp: true);
        }

        var input = Single(values, "input") ?? positionalInput;
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new CommandLineException("An input .sln, .slnx, .csproj, or file-based .cs app path is required (use --input).");
        }

        var repositoryRoot = FullPath(Single(values, "root") ?? Directory.GetCurrentDirectory(), Directory.GetCurrentDirectory());
        var inputPath = FullPath(input, repositoryRoot);
        var output = Single(values, "output") ?? Path.Combine(repositoryRoot, "graphify-out", "graph.json");
        var outputPath = FullPath(output, repositoryRoot);
        var configuration = Single(values, "configuration") ?? "Debug";
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new CommandLineException("Configuration cannot be empty.");
        }

        return new CommandLineOptions(
            inputPath,
            repositoryRoot,
            outputPath,
            configuration.Trim(),
            Single(values, "target-framework"),
            showHelp: false);
    }

    public static string Usage => "Usage: graphify-csharp --input <solution|project|file.cs> [options]\n\n"
        + "Options:\n"
        + "  -i, --input <path>              C# solution/project/file-based app to extract (required)\n"
        + "  -r, --root <path>               Repository root for stable paths\n"
        + "  -o, --output <path>             Graphify JSON output path\n"
        + "  -c, --configuration <name>      MSBuild configuration (default: Debug)\n"
        + "  -f, --target-framework <tfm>    Select one TFM when target selection is ambiguous\n"
        + "  -h, --help                      Show this help";

    private static string? Single(IReadOnlyDictionary<string, List<string>> values, string key)
    {
        if (!values.TryGetValue(key, out var entries))
        {
            return null;
        }

        if (entries.Count > 1)
        {
            throw new CommandLineException($"Option '--{key}' may only be supplied once.");
        }

        return entries[0];
    }

    private static string FullPath(string path, string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path, basePath);
    }
}
