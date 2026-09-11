using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class WatcherInputSnapshotTests
{
    [Fact]
    public void Exact_evaluated_source_wins_over_obj_filter_without_forcing_reload_for_content_edits()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var explicitGenerated = Path.Combine(root, "obj", "Generated.cs");
            var snapshot = CreateSnapshot(
                sources: [explicitGenerated],
                dependencies: [],
                discoveryRoots: [root]);

            var exactChange = snapshot.Classify(new FileChangeEvent(FileChangeKind.Changed, explicitGenerated));
            var noiseChange = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Changed,
                Path.Combine(root, "obj", "Noise.cs")));

            Assert.True(exactChange.Accepted);
            Assert.False(exactChange.RequiresColdReconciliation);
            Assert.False(noiseChange.Accepted);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Conventional_excluded_directory_ancestor_does_not_override_the_noise_filter()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var snapshot = CreateSnapshot(
                sources: [],
                dependencies: [Path.Combine(root, "obj", "project.assets.json")],
                discoveryRoots: [root],
                infrastructurePaths: [Path.Combine(root, "obj", "project.assets.json")]);

            var classification = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Created,
                Path.Combine(root, "obj")));

            Assert.False(classification.Accepted);
            Assert.False(classification.RequiresColdReconciliation);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void An_explicit_dependency_named_project_assets_json_keeps_directory_creation_relevant()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dependency = Path.Combine(root, "deps", "obj", "project.assets.json");
            var snapshot = CreateSnapshot(
                sources: [],
                dependencies: [dependency],
                discoveryRoots: [root]);

            var classification = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Created,
                Path.Combine(root, "deps", "obj"),
                IsDirectory: true));

            Assert.True(classification.Accepted);
            Assert.True(classification.RequiresColdReconciliation);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Bootstrap_does_not_assume_obj_or_arbitrary_extensions_are_irrelevant()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var bootstrap = WatcherInputSnapshot.CreateBootstrap(
                [root],
                Path.Combine(root, "graphify-out", "csharp.json"),
                Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json"));

            var generatedSource = bootstrap.Classify(new FileChangeEvent(
                FileChangeKind.Changed,
                Path.Combine(root, "obj", "Generated.cs")));
            var arbitraryInput = bootstrap.Classify(new FileChangeEvent(
                FileChangeKind.Changed,
                Path.Combine(root, "obj", "generator.data")));
            var gitNoise = bootstrap.Classify(new FileChangeEvent(
                FileChangeKind.Changed,
                Path.Combine(root, ".git", "index")));

            Assert.True(bootstrap.IsBootstrap);
            Assert.True(generatedSource.Accepted);
            Assert.True(generatedSource.RequiresColdReconciliation);
            Assert.True(arbitraryInput.Accepted);
            Assert.True(arbitraryInput.RequiresColdReconciliation);
            Assert.False(gitNoise.Accepted);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Directory_events_preserve_known_input_ancestors_and_both_rename_endpoints()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var oldSource = Path.Combine(root, "src", "nested", "Old.cs");
            var newSource = Path.Combine(root, "obj", "New.cs");
            var snapshot = CreateSnapshot(
                sources: [oldSource],
                dependencies: [],
                discoveryRoots: [root]);

            var directoryDelete = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Deleted,
                Path.Combine(root, "src", "nested")));
            var moveOutOfScope = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Renamed,
                newSource,
                oldSource));

            Assert.True(directoryDelete.Accepted);
            Assert.True(directoryDelete.RequiresColdReconciliation);
            Assert.True(moveOutOfScope.Accepted);
            Assert.True(moveOutOfScope.RequiresColdReconciliation);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Known_arbitrary_dependency_and_rename_endpoints_are_accepted_as_reload_inputs()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dependency = Path.Combine(root, "obj", "generator.data");
            var oldSource = Path.Combine(root, "src", "Old.cs");
            var newSource = Path.Combine(root, "src", "New.cs");
            var snapshot = CreateSnapshot(
                sources: [oldSource],
                dependencies: [dependency],
                discoveryRoots: [root]);

            var dependencyChange = snapshot.Classify(new FileChangeEvent(FileChangeKind.Changed, dependency));
            var rename = snapshot.Classify(new FileChangeEvent(FileChangeKind.Renamed, newSource, oldSource));

            Assert.True(dependencyChange.Accepted);
            Assert.True(dependencyChange.RequiresColdReconciliation);
            Assert.True(rename.Accepted);
            Assert.True(rename.RequiresColdReconciliation);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Conventional_candidates_reload_but_unrelated_files_and_tool_output_do_not()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var snapshot = CreateSnapshot(
                sources: [],
                dependencies: [],
                discoveryRoots: [root]);

            var source = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Created,
                Path.Combine(root, "src", "New.cs")));
            var directory = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Deleted,
                Path.Combine(root, "src", "NewFolder")));
            var unrelated = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Changed,
                Path.Combine(root, "README.md")));
            var output = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Changed,
                Path.Combine(root, "graphify-out", "csharp.json")));
            var boundary = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Changed,
                Path.Combine(root, "objects", "keep.cs")));

            Assert.True(source.Accepted);
            Assert.True(source.RequiresColdReconciliation);
            Assert.True(directory.Accepted);
            Assert.True(directory.RequiresColdReconciliation);
            Assert.False(unrelated.Accepted);
            Assert.False(output.Accepted);
            Assert.True(boundary.Accepted);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void A_root_level_output_does_not_hide_unrelated_source_files()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Source.cs");
            var output = Path.Combine(root, "csharp.cs");
            var snapshot = WatcherInputSnapshot.CreateForTests(
                knownSources: [source],
                knownDependencies: [],
                sourceProjects:
                [
                    new KeyValuePair<string, IEnumerable<string>>(source, ["project=Fixture|tfm=net10.0"]),
                ],
                discoveryRoots: [root],
                outputPath: output,
                cachePath: Path.Combine(root, ".graphify-csharp", "manifest.json"));

            Assert.True(snapshot.Classify(new FileChangeEvent(FileChangeKind.Changed, source)).Accepted);
            Assert.False(snapshot.Classify(new FileChangeEvent(FileChangeKind.Changed, output)).Accepted);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Atomic_output_staging_files_are_ignored_alongside_the_final_output()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var output = Path.Combine(root, "csharp.json");
            var snapshot = WatcherInputSnapshot.CreateForTests(
                knownSources: [],
                knownDependencies: [],
                sourceProjects: [],
                discoveryRoots: [root],
                outputPath: output,
                cachePath: Path.Combine(root, ".graphify-csharp", "manifest.json"));
            var temporaryOutput = Path.Combine(
                root,
                $".csharp.json.{Guid.NewGuid():N}.tmp");

            Assert.False(snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Created,
                temporaryOutput)).Accepted);
            Assert.False(snapshot.ShouldIncludeInInventory(temporaryOutput));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Inventory_scans_exact_obj_input_without_opening_obj_noise()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var obj = Path.Combine(root, "obj");
            Directory.CreateDirectory(obj);
            var explicitGenerated = Path.Combine(obj, "Explicit.cs");
            var noise = Path.Combine(obj, "Noise.cs");
            var dependency = Path.Combine(obj, "generator.data");
            await File.WriteAllTextAsync(explicitGenerated, "class ExplicitGenerated { }");
            await File.WriteAllTextAsync(noise, "class Noise { }");
            await File.WriteAllTextAsync(dependency, "generator input");
            var snapshot = CreateSnapshot(
                sources: [explicitGenerated],
                dependencies: [dependency],
                discoveryRoots: [root]);

            var inventory = await new FileInventoryScanner().ScanAsync(
                [root],
                root,
                inputSnapshot: snapshot);

            Assert.Contains(
                IncrementalPaths.CanonicalAbsolutePath(explicitGenerated),
                inventory.Entries.Keys);
            Assert.DoesNotContain(
                IncrementalPaths.CanonicalAbsolutePath(noise),
                inventory.Entries.Keys);
            Assert.Contains(
                IncrementalPaths.CanonicalAbsolutePath(dependency),
                inventory.Entries.Keys);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Recursive_exclusions_prune_only_a_wholly_excluded_subtree()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var obj = Path.Combine(root, "obj");
            var nested = Path.Combine(obj, "Release", "generated");
            var source = Path.Combine(root, "src");
            var recursive = CreateSnapshot(
                sources: [],
                dependencies: [],
                discoveryRoots: [root],
                inputGlobs:
                [
                    new ProjectInputGlob("Compile", root, "**/*.csharp")
                    {
                        ExcludePatterns = ["obj/**"],
                    },
                ]);
            var nonrecursive = CreateSnapshot(
                sources: [],
                dependencies: [],
                discoveryRoots: [root],
                inputGlobs:
                [
                    new ProjectInputGlob("Compile", root, "obj/**/*.csharp")
                    {
                        ExcludePatterns = ["obj/*"],
                    },
                ]);

            Assert.False(recursive.ShouldTraverseDirectory(obj));
            Assert.True(nonrecursive.ShouldTraverseDirectory(obj));
            Assert.True(nonrecursive.ShouldTraverseDirectory(Path.Combine(obj, "Release")));
            Assert.True(nonrecursive.ShouldIncludeInInventory(Path.Combine(nested, "Added.csharp")));
            Assert.False(nonrecursive.ShouldIncludeInInventory(Path.Combine(obj, "Direct.csharp")));
            Assert.True(nonrecursive.ShouldTraverseDirectory(source));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Uncertain_native_file_at_recursive_exclusion_prefix_is_not_dropped()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var generatedSource = Path.Combine(
                root,
                "obj",
                "Release",
                "net10.0",
                "Graphify.CSharp.Tests.GlobalUsings.g.cs");
            var snapshot = CreateSnapshot(
                sources: [],
                dependencies: [],
                discoveryRoots: [root],
                inputGlobs:
                [
                    new ProjectInputGlob("Compile", root, "obj/**/*.cs")
                    {
                        ExcludePatterns = ["obj/**/Graphify*/**"],
                    },
                ]);

            var classification = snapshot.Classify(
                new FileChangeEvent(FileChangeKind.Created, generatedSource));

            Assert.True(snapshot.ShouldIncludeInInventory(generatedSource));
            Assert.True(classification.Accepted);
            Assert.True(classification.RequiresColdReconciliation);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void An_extensionless_reserved_name_is_not_assumed_to_be_an_excluded_directory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "obj", "bin");
            var snapshot = CreateSnapshot(
                sources: [],
                dependencies: [],
                discoveryRoots: [root],
                inputGlobs:
                [
                    new ProjectInputGlob("Compile", root, "obj/b*")
                    {
                        ExcludePatterns = ["obj/bin/**"],
                    },
                ]);

            var uncertain = snapshot.Classify(new FileChangeEvent(FileChangeKind.Created, source));
            var file = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Created,
                source,
                IsDirectory: false));
            var directory = snapshot.Classify(new FileChangeEvent(
                FileChangeKind.Created,
                source,
                IsDirectory: true));

            Assert.True(uncertain.Accepted);
            Assert.True(file.Accepted);
            Assert.False(directory.Accepted);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static WatcherInputSnapshot CreateSnapshot(
        IEnumerable<string> sources,
        IEnumerable<string> dependencies,
        IEnumerable<string> discoveryRoots,
        IEnumerable<ProjectInputGlob>? inputGlobs = null,
        IEnumerable<string>? infrastructurePaths = null)
    {
        var root = discoveryRoots.First();
        var output = Path.Combine(root, "graphify-out", "csharp.json");
        var cache = Path.Combine(root, "graphify-out", ".graphify-csharp", "manifest.json");
        var sourceProjects = sources.Select(source =>
            new KeyValuePair<string, IEnumerable<string>>(source, ["project=Fixture|tfm=net10.0"]));
        return WatcherInputSnapshot.CreateForTests(
            sources,
            dependencies,
            sourceProjects,
            discoveryRoots,
            output,
            cache,
            inputGlobs: inputGlobs,
            infrastructurePaths: infrastructurePaths);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-watcher-policy-tests",
            Guid.NewGuid().ToString("N"));
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
