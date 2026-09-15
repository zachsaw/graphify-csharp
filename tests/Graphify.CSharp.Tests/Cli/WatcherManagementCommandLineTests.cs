using global::Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class WatcherManagementCommandLineTests
{
    [Fact]
    public void Parses_list_info_inspect_diagnostics_and_stop_without_extraction_options()
    {
        var list = WatcherManagementCommandLine.Parse(["ps", "--json"]);
        var info = WatcherManagementCommandLine.Parse(["info", "abc"]);
        var inspect = WatcherManagementCommandLine.Parse(["inspect", "abc", "--json"]);
        var diagnostics = WatcherManagementCommandLine.Parse(["diagnostics", "abc", "--output", "report.json"]);
        var stop = WatcherManagementCommandLine.Parse(["stop", "full-session-id"]);

        Assert.Equal(WatcherManagementCommandKind.List, list.Kind);
        Assert.True(list.Json);
        Assert.Equal(WatcherManagementCommandKind.Inspect, info.Kind);
        Assert.Equal("abc", info.SessionSelector);
        Assert.Equal(WatcherManagementCommandKind.Inspect, inspect.Kind);
        Assert.Equal("abc", inspect.SessionSelector);
        Assert.Equal(WatcherManagementCommandKind.Diagnostics, diagnostics.Kind);
        Assert.Equal("report.json", diagnostics.OutputPath);
        Assert.Equal(WatcherManagementCommandKind.Stop, stop.Kind);
        Assert.Equal("full-session-id", stop.SessionSelector);
    }

    [Fact]
    public void Rejects_missing_or_ambiguous_management_arguments()
    {
        Assert.Throws<CommandLineException>(() => WatcherManagementCommandLine.Parse(["inspect"]));
        Assert.Throws<CommandLineException>(() => WatcherManagementCommandLine.Parse(["ps", "abc"]));
        Assert.Throws<CommandLineException>(() => WatcherManagementCommandLine.Parse(["stop", "abc", "def"]));
        Assert.Throws<CommandLineException>(() => WatcherManagementCommandLine.Parse(["ps", "--json", "--json"]));
        Assert.Throws<CommandLineException>(() => WatcherManagementCommandLine.Parse(["diagnostics", "abc"]));
        Assert.Throws<CommandLineException>(() => WatcherManagementCommandLine.Parse(["inspect", "abc", "--output", "report.json"]));
    }

    [Fact]
    public void Routes_only_exact_management_command_names()
    {
        Assert.True(WatcherManagementCommandLine.IsManagementCommand(["ps"]));
        Assert.True(WatcherManagementCommandLine.IsManagementCommand(["inspect", "abc"]));
        Assert.False(WatcherManagementCommandLine.IsManagementCommand(["--input", "ps"]));
        Assert.False(WatcherManagementCommandLine.IsManagementCommand(["ps-old"]));
    }
}
