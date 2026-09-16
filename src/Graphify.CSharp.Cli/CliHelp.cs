namespace Graphify.CSharp.Cli;

internal static class CliHelp
{
    public static bool IsTopLevelHelp(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0] is "--help" or "-h";

    public static string Usage => "Usage:\n"
        + "  graphify-csharp [<input>] [export options]       Export JSON (default)\n"
        + "  graphify-csharp export [options]                  Export JSON\n"
        + "  graphify-csharp watch [options]                   Start a warm session\n"
        + "  graphify-csharp query <verb> [options]            Query semantic evidence\n"
        + "  graphify-csharp refresh --instance <id>          Refresh a warm session\n"
        + "  graphify-csharp ps [--json]                       List warm sessions\n"
        + "  graphify-csharp info <id|prefix> [--json]         Inspect a warm session\n"
        + "  graphify-csharp diagnostics <id|prefix> --output <path>\n"
        + "  graphify-csharp stop <id|prefix> [--json]        Stop a warm session\n\n"
        + "Work from disk:\n"
        + "  Omit --input for export/watch when the current root contains one\n"
        + "  solution (.sln/.slnx), or one project (.csproj) when no solution exists.\n"
        + "  Disk export defaults to ./graphify-out/csharp.json.\n\n"
        + "Warm sessions:\n"
        + "  watch is output-free and prints its session ID before loading completes.\n"
        + "  Route query, refresh, and export explicitly with --instance <id|prefix>.\n"
        + "  No command attaches to a session by matching input or output paths.\n\n"
        + "Use '<command> --help' for command-specific options and examples.";
}
