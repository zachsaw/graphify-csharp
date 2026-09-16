using Graphify.CSharp.Cli;

namespace Graphify.CSharp.Tests.Cli;

public sealed class AnalysisInputResolverTests
{
    [Fact]
    public void Discovers_one_solution_before_projects_and_ignores_nested_candidates()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Write(root, "Product.SLNX");
            Write(root, "Product.csproj");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            Write(Path.Combine(root, "nested"), "Nested.sln");
            Write(root, "NotAnInput.cs");

            var input = AnalysisInputResolver.ResolveInput(
                null,
                root,
                root,
                "export");

            Assert.Equal(Path.Combine(root, "Product.SLNX"), input);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Rejects_multiple_solutions_in_stable_ordinal_order()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Write(root, "z.sln");
            Write(root, "a.slnx");

            var exception = Assert.Throws<CommandLineException>(() => AnalysisInputResolver.ResolveInput(
                null,
                root,
                root,
                "watch"));

            Assert.Contains("'a.slnx', 'z.sln'", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Falls_back_to_one_project_but_rejects_multiple_projects()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Write(root, "Product.CSPROJ");
            Assert.Equal(
                Path.Combine(root, "Product.CSPROJ"),
                AnalysisInputResolver.ResolveInput(null, root, root, "export"));

            Write(root, "Other.csproj");
            var exception = Assert.Throws<CommandLineException>(() => AnalysisInputResolver.ResolveInput(
                null,
                root,
                root,
                "export"));
            Assert.Contains("multiple projects", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Does_not_discover_lone_or_nested_csharp_files()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Write(root, "Only.cs");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            Write(Path.Combine(root, "nested"), "Nested.csproj");

            var exception = Assert.Throws<CommandLineException>(() => AnalysisInputResolver.ResolveInput(
                null,
                root,
                root,
                "watch"));
            Assert.Contains("no .sln, .slnx, or .csproj", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Explicit_paths_are_relative_to_caller_cwd_not_root()
    {
        var caller = CreateTemporaryDirectory("caller with spaces");
        var root = CreateTemporaryDirectory("analysis-root");
        try
        {
            var options = DiskExportCommandLine.Parse(
                [
                    "export",
                    "--input", "src/Product.sln",
                    "--root", root,
                    "--output", "out/csharp.json",
                ],
                caller);

            Assert.Equal(Path.Combine(caller, "src/Product.sln"), options.Request.InputPath);
            Assert.Equal(Path.Combine(caller, "out/csharp.json"), options.OutputPath);
            Assert.Equal(root, options.Request.RepositoryRoot);
        }
        finally
        {
            DeleteTemporaryDirectory(caller);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Watch_and_export_can_discover_from_an_explicit_root_without_changing_process_cwd()
    {
        var caller = CreateTemporaryDirectory("caller");
        var root = CreateTemporaryDirectory("discovery-root");
        try
        {
            Write(root, "Product.sln");

            var watch = WatchCommandLine.Parse(["watch", "--root", root], caller);
            var export = DiskExportCommandLine.Parse(["export", "--root", root, "--output", "out.json"], caller);

            Assert.Equal(Path.Combine(root, "Product.sln"), watch.Request.InputPath);
            Assert.Equal(Path.Combine(root, "Product.sln"), export.Request.InputPath);
            Assert.Equal(root, watch.Request.RepositoryRoot);
            Assert.Equal(root, export.Request.RepositoryRoot);
        }
        finally
        {
            DeleteTemporaryDirectory(caller);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Rejects_empty_paths_and_duplicate_positional_input()
    {
        var caller = CreateTemporaryDirectory("caller");
        try
        {
            Assert.Throws<CommandLineException>(() => DiskExportCommandLine.Parse(
                ["export", "--input", "", "--output", "out.json"],
                caller));
            Assert.Throws<CommandLineException>(() => DiskExportCommandLine.Parse(
                ["export", "Product.sln", "--input", "Other.sln", "--output", "out.json"],
                caller));
            Assert.Throws<CommandLineException>(() => WatchCommandLine.Parse(
                ["watch", "--root", ""],
                caller));
        }
        finally
        {
            DeleteTemporaryDirectory(caller);
        }
    }

    private static void Write(string directory, string fileName) =>
        File.WriteAllText(Path.Combine(directory, fileName), string.Empty);

    private static string CreateTemporaryDirectory(string? suffix = null)
    {
        var name = suffix is null
            ? Guid.NewGuid().ToString("N")
            : $"{Guid.NewGuid():N}-{suffix}";
        var path = Path.Combine(Path.GetTempPath(), "graphify-csharp-cli-tests", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
