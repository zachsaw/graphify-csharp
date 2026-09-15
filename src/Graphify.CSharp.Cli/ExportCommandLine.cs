namespace Graphify.CSharp.Cli;

internal static class ExportCommandLine
{
    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        // Keep the already-tested live-session export parser isolated while
        // the top-level export verb gains its independent disk route. The
        // explicit --instance token is the only routing signal; no analysis
        // identity lookup is attempted for disk exports.
        return args.Skip(1).Contains("--instance", StringComparer.Ordinal)
            ? SemanticQueryCommandLine.RunAsync(args, cancellationToken)
            : DiskExportCommandLine.RunAsync(args, cancellationToken);
    }
}
