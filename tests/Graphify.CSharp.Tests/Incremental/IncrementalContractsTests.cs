using Graphify.CSharp.Domain;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class IncrementalContractsTests
{
    [Fact]
    public void Request_identity_is_canonical_and_changes_when_analysis_inputs_change()
    {
        var relative = new RefreshRequestIdentity(
            "./tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj",
            ".",
            " Release ",
            " net10.0 ");
        var absolute = new RefreshRequestIdentity(
            Path.GetFullPath("tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj"),
            Directory.GetCurrentDirectory(),
            "Release",
            "net10.0");
        var differentConfiguration = new RefreshRequestIdentity(
            absolute.InputPath,
            absolute.RepositoryRoot,
            "Debug",
            absolute.TargetFramework);

        Assert.True(relative.Matches(absolute));
        Assert.Equal(relative.Digest, absolute.Digest);
        Assert.NotEqual(absolute.Digest, differentConfiguration.Digest);
        Assert.Contains("configuration=Release", absolute.CanonicalKey, StringComparison.Ordinal);
        Assert.Contains("tfm=net10.0", absolute.CanonicalKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Watcher_endpoint_identity_includes_output_path_and_canonicalizes_aliases()
    {
        var request = new RefreshRequestIdentity(
            "./tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj",
            ".",
            "Release",
            "net10.0");
        var relativeOutput = Path.Combine(".", "graphify-out", "csharp.json");
        var absoluteOutput = Path.GetFullPath(relativeOutput);
        var alternateOutput = Path.Combine(".", "graphify-out", "alternate.json");

        Assert.Equal(
            IncrementalRefreshControlChannel.ForRequest(request, relativeOutput),
            IncrementalRefreshControlChannel.ForRequest(request, absoluteOutput));
        Assert.Equal(
            IncrementalRefreshControlChannel.OutputPathIdentity(relativeOutput),
            IncrementalRefreshControlChannel.OutputPathIdentity(absoluteOutput));
        Assert.NotEqual(
            IncrementalRefreshControlChannel.ForRequest(request, relativeOutput),
            IncrementalRefreshControlChannel.ForRequest(request, alternateOutput));
        Assert.NotEqual(
            IncrementalCachePath.ForOutput(relativeOutput),
            IncrementalCachePath.ForOutput(alternateOutput));
        Assert.NotEqual(
            WatcherLease.ForOutput(relativeOutput, request),
            WatcherLease.ForOutput(alternateOutput, request));
    }

    [Fact]
    public void Fingerprints_distinguish_content_when_hashes_are_available_and_report_metadata_only_otherwise()
    {
        var firstHash = IncrementalHashing.Sha256("first");
        var secondHash = IncrementalHashing.Sha256("second");
        var first = new SourceFingerprint("src/App.cs", true, 10, 20, firstHash);
        var sameContent = new SourceFingerprint("src/App.cs", true, 10, 20, firstHash.ToUpperInvariant());
        var metadataOnly = new SourceFingerprint("src/App.cs", true, 10, 20);
        var differentContent = new SourceFingerprint("src/App.cs", true, 10, 20, secondHash);
        var differentMetadata = new SourceFingerprint("src/App.cs", true, 11, 20, firstHash);

        Assert.Equal(FingerprintComparison.ContentMatch, first.CompareTo(sameContent));
        Assert.Equal(FingerprintComparison.MetadataMatch, first.CompareTo(metadataOnly));
        Assert.Equal(FingerprintComparison.Different, first.CompareTo(differentContent));
        Assert.Equal(FingerprintComparison.Different, first.CompareTo(differentMetadata));
    }

    [Fact]
    public void Project_fingerprints_are_sorted_and_dependency_changes_invalidate_them()
    {
        var project = new ProjectIdentity("src/App/App.csproj", "net10.0");
        var sourceA = new SourceFingerprint("src/App/A.cs", true, 1, 1);
        var sourceB = new SourceFingerprint("src/App/B.cs", true, 1, 1);
        var dependency = new SourceFingerprint("build/generator.props", true, 1, 1);
        var first = new ProjectFingerprint(
            project,
            new SourceFingerprint("src/App/App.csproj", true, 1, 1),
            [sourceB, sourceA],
            ["project=z", "project=a"],
            [dependency]);
        var same = new ProjectFingerprint(
            project,
            new SourceFingerprint("src/App/App.csproj", true, 1, 1),
            [sourceA, sourceB],
            ["project=a", "project=z"],
            [dependency]);
        var changedDependency = new ProjectFingerprint(
            project,
            new SourceFingerprint("src/App/App.csproj", true, 1, 1),
            [sourceA, sourceB],
            ["project=a", "project=b"],
            [dependency]);

        Assert.True(
            new[] { "src/App/A.cs", "src/App/B.cs" }
                .SequenceEqual(first.SourceFiles.Select(source => source.RelativePath)));
        Assert.True(new[] { "project=a", "project=z" }.SequenceEqual(first.ProjectReferenceKeys));
        Assert.Equal("build/generator.props", Assert.Single(first.DependencyFiles).RelativePath);
        Assert.Equal(first.Digest, same.Digest);
        Assert.Equal(FingerprintComparison.MetadataMatch, first.CompareTo(same));
        Assert.Equal(FingerprintComparison.Different, first.CompareTo(changedDependency));
    }

    [Fact]
    public void Compilation_option_keys_are_deterministic_and_semantic()
    {
        var firstParse = new CSharpParseOptions(
            LanguageVersion.CSharp13,
            DocumentationMode.Parse,
            SourceCodeKind.Regular,
            preprocessorSymbols: ["FEATURE_B", "FEATURE_A"]);
        var sameParse = new CSharpParseOptions(
            LanguageVersion.CSharp13,
            DocumentationMode.Parse,
            SourceCodeKind.Regular,
            preprocessorSymbols: ["FEATURE_A", "FEATURE_B"]);
        var changedParse = firstParse.WithLanguageVersion(LanguageVersion.CSharp12);
        var firstCompilation = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithAllowUnsafe(true)
            .WithUsings(System.Collections.Immutable.ImmutableArray.Create("System.Linq", "System"));
        var sameCompilation = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithAllowUnsafe(true)
            .WithUsings(System.Collections.Immutable.ImmutableArray.Create("System", "System.Linq"));
        var changedCompilation = firstCompilation.WithOptimizationLevel(OptimizationLevel.Release);

        var first = IncrementalProjectFingerprintBuilder.CreateCompilationOptionsKey(firstParse, firstCompilation);
        var same = IncrementalProjectFingerprintBuilder.CreateCompilationOptionsKey(sameParse, sameCompilation);
        var parseChanged = IncrementalProjectFingerprintBuilder.CreateCompilationOptionsKey(changedParse, firstCompilation);
        var compilationChanged = IncrementalProjectFingerprintBuilder.CreateCompilationOptionsKey(firstParse, changedCompilation);

        Assert.Equal(first, same);
        Assert.NotEqual(first, parseChanged);
        Assert.NotEqual(first, compilationChanged);
    }

    [Fact]
    public void Generation_state_is_monotonic_and_targets_are_session_scoped()
    {
        var sessionId = Guid.Parse("b6d59f3c-1b2c-4c6d-a3c2-8f8c50311b5e");
        var initial = new RefreshGeneration(sessionId);
        var firstEvent = initial.RecordEvent();
        var target = firstEvent.CaptureTarget();
        var indexed = firstEvent.MarkIndexed(firstEvent.EventGeneration);
        var published = indexed.MarkPublished(indexed.IndexedGeneration);

        Assert.False(firstEvent.IsPublishedThrough(target));
        Assert.True(published.IsIndexedThrough(target));
        Assert.True(published.IsPublishedThrough(target));
        Assert.Throws<ArgumentOutOfRangeException>(() => initial.MarkIndexed(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => firstEvent.MarkPublished(1));
        Assert.False(published.IsPublishedThrough(new RefreshTarget(Guid.NewGuid(), target.EventGeneration)));
    }

    [Fact]
    public async Task Cache_round_trip_is_deterministic_and_reconstructs_domain_data()
    {
        var state = CreateState();
        var directory = CreateTemporaryDirectory();
        var firstPath = Path.Combine(directory, "first.cache.json");
        var secondPath = Path.Combine(directory, "second.cache.json");
        try
        {
            var store = new IncrementalCacheStore();
            await store.SaveAsync(firstPath, state);
            await store.SaveAsync(secondPath, state);

            Assert.Equal(await File.ReadAllTextAsync(firstPath), await File.ReadAllTextAsync(secondPath));

            var result = await store.LoadAsync(firstPath, state.Request);
            Assert.Equal(IncrementalCacheLoadStatus.Loaded, result.Status);
            Assert.NotNull(result.State);
            Assert.Equal(state.Request.Digest, result.State!.Request.Digest);
            Assert.Equal(state.Manifest.Select(entry => entry.Project.Key), result.State.Manifest.Select(entry => entry.Project.Key));
            Assert.Equal(
                state.Contributions[0].Graph.Nodes.Select(node => node.Id),
                result.State.Contributions[0].Graph.Nodes.Select(node => node.Id));
            Assert.Equal(
                state.Contributions[0].Graph.Edges.Select(edge => edge.DeduplicationKey),
                result.State.Contributions[0].Graph.Edges.Select(edge => edge.DeduplicationKey));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Cache_load_rejects_wrong_request_corrupt_json_and_incomplete_state()
    {
        var state = CreateState();
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "cache.json");
        try
        {
            var store = new IncrementalCacheStore();
            await store.SaveAsync(path, state);

            var legacyJson = (await File.ReadAllTextAsync(path)).Replace(
                RefreshRequestIdentity.CurrentCacheSchemaVersion,
                "graphify-csharp/incremental-cache/v2",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, legacyJson);
            var legacy = await store.LoadAsync(path, state.Request);
            Assert.Equal(IncrementalCacheLoadStatus.Incompatible, legacy.Status);

            await store.SaveAsync(path, state);

            var wrongRequest = new RefreshRequestIdentity(
                state.Request.InputPath,
                state.Request.RepositoryRoot,
                "Debug",
                state.Request.TargetFramework);
            var incompatible = await store.LoadAsync(path, wrongRequest);
            Assert.Equal(IncrementalCacheLoadStatus.Incompatible, incompatible.Status);

            await File.WriteAllTextAsync(path, "{ not valid json");
            var corruptJson = await store.LoadAsync(path, state.Request);
            Assert.Equal(IncrementalCacheLoadStatus.Corrupt, corruptJson.Status);

            await store.SaveAsync(path, state);
            var incompleteJson = (await File.ReadAllTextAsync(path)).Replace(
                "\"complete\": true",
                "\"complete\": false",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, incompleteJson);
            var incomplete = await store.LoadAsync(path, state.Request);
            Assert.Equal(IncrementalCacheLoadStatus.Corrupt, incomplete.Status);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Atomic_commit_failure_preserves_the_last_valid_cache()
    {
        var state = CreateState();
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "cache.json");
        try
        {
            var store = new IncrementalCacheStore();
            await store.SaveAsync(path, state);
            var before = await File.ReadAllTextAsync(path);

            var failingStore = new IncrementalCacheStore(new ThrowingCommitter());
            await Assert.ThrowsAsync<IOException>(() => failingStore.SaveAsync(path, state));

            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(directory, ".*.tmp"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static IncrementalCacheState CreateState()
    {
        var project = new ProjectIdentity("src/App/App.csproj", "net10.0");
        var fingerprint = new ProjectFingerprint(
            project,
            new SourceFingerprint("src/App/App.csproj", true, 12, 100),
            [new SourceFingerprint("src/App/App.cs", true, 20, 200)],
            ["project=src/Library/Library.csproj|tfm=net10.0"],
            [new SourceFingerprint("build/generator.props", true, 1, 1)]);
        var identity = new SymbolIdentity(project, "App", [new ContainingTypeIdentity("Program")], global::Graphify.CSharp.Domain.SymbolKind.Method, "Run");
        var node = GraphNode.ForSymbol(identity, [new SourceLocation("src/App/App.cs", 4, 5)]);
        var graph = GraphSnapshot.Create([node], []);
        var contribution = new ProjectContributionEnvelope(fingerprint, graph, ["diagnostic"]);
        var manifest = new IncrementalManifestEntry(fingerprint, contribution.ContributionKey);
        var request = new RefreshRequestIdentity(
            "/workspace/App.sln",
            "/workspace",
            "Release",
            "net10.0");
        var generation = new RefreshGeneration(
            Guid.Parse("b6d59f3c-1b2c-4c6d-a3c2-8f8c50311b5e"),
            eventGeneration: 2,
            indexedGeneration: 2,
            publishedGeneration: 2);
        return new IncrementalCacheState(request, [contribution], [manifest], generation);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "graphify-csharp-incremental-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class ThrowingCommitter : IAtomicCacheCommitter
    {
        public void Commit(string temporaryPath, string destinationPath) =>
            throw new IOException("Synthetic atomic commit failure.");
    }
}
