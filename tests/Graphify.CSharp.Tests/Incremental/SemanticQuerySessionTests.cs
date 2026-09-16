using System.Text.Json;
using Graphify.CSharp.Incremental;
using Graphify.CSharp.Roslyn;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class SemanticQuerySessionTests
{
    [Fact]
    public async Task Semantic_queries_bind_overloads_relationships_summaries_and_arguments()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var symbols = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "Called",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 100));
            Assert.True(symbols.Success, symbols.Error?.Message);
            Assert.Equal(2, symbols.Items.Count);
            var intMethod = FindItem(symbols, "ReferenceFixture.Production.Service.Called(int)");
            var stringMethod = FindItem(symbols, "ReferenceFixture.Production.Service.Called(string)");
            Assert.NotEqual(Id(intMethod), Id(stringMethod));

            var signature = await QueryAsync(
                session,
                new SemanticQuerySpec("signature", SymbolId: Id(intMethod)));
            Assert.True(signature.Success, signature.Error?.Message);
            Assert.Equal(
                "int",
                signature.Items[0]
                    .GetProperty("parameters")[0]
                    .GetProperty("type")
                    .GetString());

            var callers = await QueryAsync(
                session,
                new SemanticQuerySpec("callers", SymbolId: Id(intMethod), Limit: 100));
            Assert.True(callers.Success, callers.Error?.Message);
            var callerLabels = callers.Items
                .Select(item => item.GetProperty("origin").GetProperty("label").GetString())
                .ToArray();
            Assert.Contains("ReferenceFixture.Production.ProductionCaller.Run", callerLabels);
            Assert.Contains("ReferenceFixture.Tests.TestCaller.Run", callerLabels);
            Assert.Contains("ReferenceFixture.AllDeclarations.DeclarationHost`1.Execute(ref:int, out:int, in:int, params:int[])", callerLabels);

            var arguments = await QueryAsync(
                session,
                new SemanticQuerySpec("arguments", SymbolId: Id(intMethod), Limit: 100));
            Assert.True(arguments.Success, arguments.Error?.Message);
            Assert.NotEmpty(arguments.Items);
            Assert.All(
                arguments.Items,
                item => Assert.Equal(Id(intMethod), item.GetProperty("callee_id").GetString()));
            Assert.Contains(
                arguments.Items,
                item => item.GetProperty("formal_parameter_name").GetString() == "value"
                    && item.GetProperty("expression_preview").GetString() == "1");

            var summary = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "usage-summary",
                    Search: "Called(int)",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 100));
            Assert.True(summary.Success, summary.Error?.Message);
            var intSummary = Assert.Single(summary.Items);
            Assert.Equal(3, intSummary.GetProperty("calls").GetProperty("edge_count").GetInt32());
            Assert.Equal(3, intSummary.GetProperty("calls").GetProperty("occurrence_count").GetInt32());
            Assert.Equal(3, intSummary.GetProperty("distinct_origin_count").GetInt32());

            var interfaceMethod = await QuerySymbolsAsync(
                session,
                "ReferenceFixture.Production.IContract.Execute",
                "method");
            var implementation = await QuerySymbolsAsync(
                session,
                "ReferenceFixture.Production.Contract.Execute",
                "method");
            var hierarchy = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "hierarchy",
                    SymbolId: Id(interfaceMethod),
                    Direction: "implementations",
                    Limit: 100));
            Assert.True(hierarchy.Success, hierarchy.Error?.Message);
            Assert.Contains(
                hierarchy.Items,
                item => item.GetProperty("neighbor").GetProperty("label").GetString()
                    == "ReferenceFixture.Production.Contract.Execute(int)");

            var overrides = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "hierarchy",
                    SymbolId: Id(implementation),
                    Direction: "contracts",
                    Limit: 100));
            Assert.True(overrides.Success, overrides.Error?.Message);
            Assert.Contains(
                overrides.Items,
                item => item.GetProperty("relation").GetString() is "implements" or "overrides");
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Semantic_evidence_retains_project_contribution_diagnostics()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            using var loaded = await new RoslynWorkspaceLoader().LoadAsync(fixture.Request);
            var catalog = await new DeclarationCatalogBuilder().BuildAsync(loaded);
            var fingerprints = new IncrementalProjectFingerprintBuilder().BuildAll(loaded);
            var extracted = await new SemanticReferenceExtractor()
                .ExtractContributionsAsync(loaded, catalog);
            var contributions = extracted
                .Select(contribution => new ProjectContributionEnvelope(
                    fingerprints[contribution.Project.Key],
                    contribution.Graph,
                    ["synthetic contribution extraction diagnostic"]))
                .ToArray();
            var index = SemanticEvidenceIndex.Create(catalog, contributions);
            var snapshot = WatcherInputSnapshot.CreateForTests(
                loaded.Projects
                    .SelectMany(project => project.Project.Documents)
                    .Where(document => document.FilePath is not null)
                    .Select(document => document.FilePath!),
                [],
                [],
                [fixture.Root],
                outputPath: null,
                cachePath: null);
            var sessionId = Guid.NewGuid();
            var view = new SemanticEvidenceView(
                sessionId,
                "instance",
                new RefreshRequestIdentity(
                    fixture.Request.InputPath,
                    fixture.Request.RepositoryRoot,
                    fixture.Request.Configuration,
                    fixture.Request.TargetFramework).CanonicalKey,
                loaded,
                catalog,
                index,
                snapshot,
                new RefreshGeneration(sessionId),
                TargetEventGeneration: 0,
                EvidenceRevision: 1,
                CursorSecret: sessionId.ToByteArray(),
                GlobalDiagnostics: [],
                ContributionDiagnostics: contributions
                    .SelectMany(contribution => contribution.Diagnostics)
                    .ToArray());

            var response = new SemanticQueryEngine().Execute(
                new SemanticQuerySpec("usage-summary", Limit: 10),
                view);

            Assert.True(response.Success, response.Error?.Message);
            Assert.True(response.Scope!.HasDiagnostics);
            Assert.False(response.Scope.OperationComplete);
            Assert.Contains(
                response.Diagnostics,
                diagnostic => diagnostic.Message.Contains(
                    "synthetic contribution extraction diagnostic",
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Query_only_sessions_reuse_evidence_and_page_without_creating_graph_output()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var first = await QueryAsync(
                session,
                new SemanticQuerySpec("symbols", Limit: 1));
            Assert.True(first.Success, first.Error?.Message);
            Assert.True(first.Page!.HasMore);
            Assert.NotNull(first.Page.NextCursor);
            var revision = first.Snapshot!.Id;

            var second = await QueryAsync(
                session,
                new SemanticQuerySpec("symbols", Limit: 1, Cursor: first.Page.NextCursor));
            Assert.True(second.Success, second.Error?.Message);
            Assert.NotEqual(
                first.Items[0].GetProperty("id").GetString(),
                second.Items[0].GetProperty("id").GetString());
            Assert.Equal(revision, second.Snapshot!.Id);
            Assert.Equal(0, session.Generation.PublishedGeneration);
            Assert.False(File.Exists(fixture.OutputPath));
            Assert.False(File.Exists(IncrementalCachePath.ForOutput(fixture.OutputPath)));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Warm_semantic_queries_do_not_build_compatibility_graphs_in_either_worker_mode()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var outputSession = new IncrementalIndexSession(
                fixture.Request,
                fixture.OutputPath,
                semanticMode: "instance");
            await outputSession.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            await QueryAsync(outputSession, new SemanticQuerySpec("symbols", Limit: 1));
            await QueryAsync(outputSession, new SemanticQuerySpec("symbols", Limit: 1));
            Assert.Equal(0, outputSession.CompatibilityGraphBuildCount);

            await using var queryOnlySession = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await queryOnlySession.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await QueryAsync(queryOnlySession, new SemanticQuerySpec("symbols", Limit: 1));
            await QueryAsync(queryOnlySession, new SemanticQuerySpec("symbols", Limit: 1));
            Assert.Equal(0, queryOnlySession.CompatibilityGraphBuildCount);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Semantic_refresh_reconciles_without_building_or_publishing_the_compatibility_graph()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var result = await session.RefreshSemanticAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var response = result.SemanticRefreshResponse
                ?? throw new Xunit.Sdk.XunitException(
                    "The semantic refresh did not capture its response in the worker.");

            Assert.True(response.Success, response.Error?.Message);
            Assert.Equal("refresh", response.Command);
            Assert.NotNull(response.Refresh);
            Assert.False(response.Refresh!.Rebuild);
            Assert.Empty(result.Graph.Nodes);
            Assert.Empty(result.Graph.Edges);
            Assert.Equal(0, session.CompatibilityGraphBuildCount);
            Assert.False(File.Exists(fixture.OutputPath));
            Assert.False(File.Exists(IncrementalCachePath.ForOutput(fixture.OutputPath)));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Semantic_refresh_response_keeps_its_target_when_a_later_event_arrives_during_reconciliation()
    {
        var fixture = await CreateFixtureAsync();
        var loader = new BlockingReloadLoader(new RoslynWorkspaceLoader());
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                projectLoader: loader,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            session.ReportFileChanged(fixture.Request.InputPath);
            var targetGeneration = session.EventGeneration;
            var refresh = session.RefreshSemanticAsync();
            await loader.ReloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(60));

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class ArrivedAfterRefreshTarget { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            Assert.Equal(targetGeneration + 1, session.EventGeneration);
            loader.ReleaseReload();

            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(60));
            var response = result.SemanticRefreshResponse
                ?? throw new Xunit.Sdk.XunitException(
                    "The semantic refresh did not capture its response in the worker.");
            var snapshot = Assert.IsType<SemanticQuerySnapshot>(response.Snapshot);

            Assert.Equal(targetGeneration, snapshot.TargetGeneration);
            Assert.Equal(targetGeneration, snapshot.IndexedGeneration);
            Assert.True(snapshot.EventGeneration > snapshot.TargetGeneration);
        }
        finally
        {
            loader.ReleaseReload();
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Summary_pages_create_only_the_bounded_candidate_rows_and_seek_on_continuation()
    {
        var fixture = await CreateFixtureAsync();
        var metrics = new SemanticQueryExecutionMetrics();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance",
                semanticQueryEngine: new SemanticQueryEngine(metrics: metrics));
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var first = await QueryAsync(
                session,
                new SemanticQuerySpec("usage-summary", Limit: 1));
            Assert.True(first.Success, first.Error?.Message);
            Assert.True(first.Page!.HasMore);
            Assert.InRange(metrics.RowsCreated, 1, 2);

            var rowsAfterFirstPage = metrics.RowsCreated;
            var second = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "usage-summary",
                    Limit: 1,
                    Cursor: first.Page.NextCursor));
            Assert.True(second.Success, second.Error?.Message);
            Assert.NotEqual(
                first.Items[0].GetProperty("target").GetProperty("id").GetString(),
                second.Items[0].GetProperty("target").GetProperty("id").GetString());
            Assert.InRange(metrics.RowsCreated - rowsAfterFirstPage, 1, 2);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Byte_limited_pages_return_a_continuation_instead_of_dropping_candidates()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance",
                semanticQueryEngine: new SemanticQueryEngine(maximumResponseBytes: 4096));
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var allIds = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var pageCount = 0;
            SemanticQueryResponse? first = null;
            do
            {
                var response = await QueryAsync(
                    session,
                    new SemanticQuerySpec(
                        "symbols",
                        Limit: 1000,
                        Cursor: cursor));
                Assert.True(response.Success, response.Error?.Message);
                first ??= response;
                foreach (var item in response.Items)
                {
                    Assert.True(allIds.Add(item.GetProperty("id").GetString()!));
                }

                pageCount++;
                if (!response.Page!.HasMore)
                {
                    break;
                }

                Assert.NotNull(response.Page.NextCursor);
                cursor = response.Page.NextCursor;
                Assert.True(pageCount < 100, "The semantic cursor did not converge.");
            }
            while (true);

            Assert.NotNull(first);
            Assert.True(first!.Page!.HasMore);
            Assert.True(pageCount > 1);
            Assert.True(allIds.Count > first.Items.Count);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Replaced_evidence_invalidates_the_previous_snapshot()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var before = await QueryAsync(
                session,
                new SemanticQuerySpec("symbols", Search: "Added", Limit: 10));
            var called = await QuerySymbolsAsync(session, "Called(int)", "method");
            var beforeSignature = await QueryAsync(
                session,
                new SemanticQuerySpec("signature", SymbolId: Id(called)));

            await File.AppendAllTextAsync(
                fixture.SourcePath,
                "\npublic sealed class AddedAfterSemanticQuery { }\n");
            session.ReportFileChanged(fixture.SourcePath);
            var after = await QueryAsync(
                session,
                new SemanticQuerySpec("symbols", Search: "Added", Limit: 100));

            Assert.True(after.Success, after.Error?.Message);
            Assert.Contains(
                after.Items,
                item => item.GetProperty("label").GetString()
                    == "ReferenceFixture.Production.AddedAfterSemanticQuery");
            Assert.NotEqual(before.Snapshot!.Id, after.Snapshot!.Id);

            var stale = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "signature",
                    SymbolId: Id(called),
                    SnapshotId: beforeSignature.Snapshot!.Id));
            Assert.False(stale.Success);
            Assert.Equal("stale_snapshot", stale.Error!.Code);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Summary_grouping_keeps_origins_outside_the_target_filter_and_returns_zero_rows()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var grouped = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "usage-summary",
                    Search: "Called(int)",
                    Filters: new SemanticQueryFilters(
                        Namespace: "ReferenceFixture.Production",
                        Kind: "method"),
                    GroupBy: ["project", "namespace"],
                    Limit: 100));
            Assert.True(grouped.Success, grouped.Error?.Message);
            Assert.Equal(3, grouped.Items.Count);
            Assert.Contains(
                grouped.Items,
                item => item.GetProperty("origin_namespace").GetString() == "ReferenceFixture.Tests");
            Assert.All(
                grouped.Items,
                item => Assert.Equal(
                    1,
                    item.GetProperty("calls").GetProperty("edge_count").GetInt32()));

            var zero = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "usage-summary",
                    Search: "Unused",
                    Filters: new SemanticQueryFilters(Kind: "property"),
                    Limit: 100));
            Assert.True(zero.Success, zero.Error?.Message);
            var zeroItem = Assert.Single(zero.Items);
            Assert.Equal(0, zeroItem.GetProperty("calls").GetProperty("edge_count").GetInt32());
            Assert.Equal(0, zeroItem.GetProperty("references").GetProperty("edge_count").GetInt32());
            Assert.Equal(0, zeroItem.GetProperty("distinct_origin_count").GetInt32());
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Arguments_bind_the_receiver_of_a_CSharp14_extension_block()
    {
        var repositoryRoot = RepositoryRoot();
        var request = new ProjectLoadRequest(
            Path.Combine(repositoryRoot, "tests", "Fixtures", "CSharp14Fixture", "CSharp14Fixture.csproj"),
            repositoryRoot,
            "Release",
            "net10.0");
        await using var session = new IncrementalIndexSession(
            request,
            outputPath: null,
            semanticMode: "instance");
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var symbols = await QueryAsync(
            session,
            new SemanticQuerySpec(
                "symbols",
                Search: "FirstValue",
                Filters: new SemanticQueryFilters(Kind: "method"),
                Limit: 10));
        var target = Assert.Single(symbols.Items);
        var arguments = await QueryAsync(
            session,
            new SemanticQuerySpec(
                "arguments",
                SymbolId: Id(target),
                Limit: 10));

        Assert.True(arguments.Success, arguments.Error?.Message);
        var receiver = Assert.Single(arguments.Items);
        Assert.Equal("extension_receiver", receiver.GetProperty("argument_kind").GetString());
        Assert.Equal("values", receiver.GetProperty("formal_parameter_name").GetString());
        Assert.NotNull(receiver.GetProperty("formal_parameter_id").GetString());
        Assert.Equal("values", receiver.GetProperty("expression_preview").GetString());
        Assert.True(arguments.Scope!.OperationComplete);
    }

    [Fact]
    public async Task Arguments_report_known_implicit_calls_as_incomplete_instead_of_empty_complete_evidence()
    {
        var fixture = await CreateFixtureAsync(
            "LanguageSurfaceFixture",
            "LanguageSurfaceFixture.csproj",
            "Surface.cs");
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var symbols = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "SurfaceEnumerator.MoveNext",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 10));
            var target = FindItem(
                symbols,
                "LanguageSurfaceFixture.SurfaceEnumerator.MoveNext");

            var callers = await QueryAsync(
                session,
                new SemanticQuerySpec("callers", SymbolId: Id(target), Limit: 10));
            Assert.Contains(
                callers.Items,
                item => item.GetProperty("origin").GetProperty("label").GetString()
                    ?.StartsWith(
                        "LanguageSurfaceFixture.SurfaceConsumer.Enumeration(",
                        StringComparison.Ordinal) == true);

            var arguments = await QueryAsync(
                session,
                new SemanticQuerySpec("arguments", SymbolId: Id(target), Limit: 10));

            Assert.True(arguments.Success, arguments.Error?.Message);
            Assert.Empty(arguments.Items);
            Assert.False(arguments.Scope!.OperationComplete);
            Assert.Contains(
                arguments.Scope.OperationDiagnostics,
                diagnostic => diagnostic.Contains(
                    "did not expose bindable argument details",
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Argument_continuations_do_not_reclassify_consumed_documents_as_missing()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var symbols = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "Called(int)",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 10));
            var target = FindItem(
                symbols,
                "ReferenceFixture.Production.Service.Called(int)");

            var rows = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var pageCount = 0;
            while (true)
            {
                var page = await QueryAsync(
                    session,
                    new SemanticQuerySpec(
                        "arguments",
                        SymbolId: Id(target),
                        Limit: 1,
                        Cursor: cursor));
                Assert.True(page.Success, page.Error?.Message);
                Assert.DoesNotContain(
                    page.Scope!.OperationDiagnostics,
                    diagnostic => diagnostic.Contains(
                        "did not expose bindable argument details",
                        StringComparison.Ordinal));
                Assert.Single(page.Items);
                foreach (var item in page.Items)
                {
                    Assert.True(rows.Add(item.GetRawText()));
                }

                pageCount++;
                if (!page.Page!.HasMore)
                {
                    break;
                }

                cursor = Assert.IsType<string>(page.Page.NextCursor);
                Assert.True(pageCount < 10, "The argument cursor did not converge.");
            }

            Assert.Equal(3, pageCount);
            Assert.Equal(3, rows.Count);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Argument_continuations_preserve_implicit_gaps_when_root_differs_from_process_directory()
    {
        var fixture = await CreateFixtureAsync(
            "LanguageSurfaceFixture",
            "LanguageSurfaceFixture.csproj",
            "Surface.cs");
        try
        {
            Assert.False(
                string.Equals(
                    Path.GetFullPath(Directory.GetCurrentDirectory()),
                    Path.GetFullPath(fixture.Root),
                    IncrementalPaths.PathComparison));
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Root, "ContinuationCalls.cs"),
                """
                namespace LanguageSurfaceFixture;

                public static class ContinuationConsumer
                {
                    public static int Run(SurfaceEnumerator enumerator)
                    {
                        enumerator.MoveNext();
                        enumerator.MoveNext();
                        var total = 0;
                        foreach (var value in enumerator)
                        {
                            total += value;
                        }

                        return total;
                    }
                }
                """);

            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var symbols = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "SurfaceEnumerator.MoveNext",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 10));
            var target = FindItem(
                symbols,
                "LanguageSurfaceFixture.SurfaceEnumerator.MoveNext");

            var first = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "arguments",
                    SymbolId: Id(target),
                    Limit: 1));
            Assert.True(first.Success, first.Error?.Message);
            Assert.False(first.Scope!.OperationComplete);
            Assert.Contains(
                first.Scope.OperationDiagnostics,
                diagnostic => diagnostic.Contains(
                    "did not expose bindable argument details",
                    StringComparison.Ordinal));
            Assert.True(first.Page!.HasMore);

            var second = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "arguments",
                    SymbolId: Id(target),
                    Limit: 1,
                    Cursor: first.Page.NextCursor));
            Assert.True(second.Success, second.Error?.Message);
            Assert.False(second.Scope!.OperationComplete);
            Assert.Contains(
                second.Scope.OperationDiagnostics,
                diagnostic => diagnostic.Contains(
                    "did not expose bindable argument details",
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    [Fact]
    public async Task Arguments_cover_zero_argument_instance_calls_initializers_and_params_expansion()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Root, "ArgumentCoverage.cs"),
                """
                namespace ReferenceFixture.ArgumentCoverage;

                public class ArgumentBase
                {
                    public ArgumentBase(int value) { }
                }

                public sealed class ArgumentDerived : ArgumentBase
                {
                    public ArgumentDerived(int value) : base(value) { }

                    public void Ping() { }

                    public static void Use(ArgumentDerived value)
                    {
                        value.Ping();
                        _ = new ArgumentDerived(2);
                        Params(0, 1, 2);
                    }

                    public static void Params(int first, params int[] rest) { }
                }

                public sealed class PrimaryArgumentDerived(int value) : ArgumentBase(value)
                {
                }
                """);

            await using var session = new IncrementalIndexSession(
                fixture.Request,
                outputPath: null,
                semanticMode: "instance");
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var pingSymbols = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "Ping",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 10));
            var ping = Assert.Single(pingSymbols.Items);
            var pingArguments = await QueryAsync(
                session,
                new SemanticQuerySpec("arguments", SymbolId: Id(ping), Limit: 10));
            var noArguments = Assert.Single(pingArguments.Items);
            Assert.Equal("no_arguments", noArguments.GetProperty("argument_kind").GetString());

            var baseConstructorSymbols = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "ArgumentBase(int)",
                    Filters: new SemanticQueryFilters(Kind: "constructor"),
                    Limit: 10));
            var baseConstructor = Assert.Single(baseConstructorSymbols.Items);
            var initializerArguments = await QueryAsync(
                session,
                new SemanticQuerySpec("arguments", SymbolId: Id(baseConstructor), Limit: 10));
            Assert.Equal(2, initializerArguments.Items.Count);
            Assert.All(
                initializerArguments.Items,
                item => Assert.Equal("value", item.GetProperty("formal_parameter_name").GetString()));

            var paramsSymbols = await QueryAsync(
                session,
                new SemanticQuerySpec(
                    "symbols",
                    Search: "Params(int, params:int[])",
                    Filters: new SemanticQueryFilters(Kind: "method"),
                    Limit: 10));
            var paramsMethod = Assert.Single(paramsSymbols.Items);
            var paramsArguments = await QueryAsync(
                session,
                new SemanticQuerySpec("arguments", SymbolId: Id(paramsMethod), Limit: 10));
            var expanded = paramsArguments.Items
                .Where(item => item.GetProperty("formal_parameter_name").GetString() == "rest")
                .ToArray();
            Assert.Equal(2, expanded.Length);
            Assert.All(
                expanded,
                item => Assert.Equal("params_expansion", item.GetProperty("argument_kind").GetString()));

            var pagedArgumentRows = new HashSet<string>(StringComparer.Ordinal);
            string? argumentCursor = null;
            var argumentPageCount = 0;
            do
            {
                var page = await QueryAsync(
                    session,
                    new SemanticQuerySpec(
                        "arguments",
                        SymbolId: Id(paramsMethod),
                        Limit: 1,
                        Cursor: argumentCursor));
                Assert.True(page.Success, page.Error?.Message);
                foreach (var item in page.Items)
                {
                    Assert.True(pagedArgumentRows.Add(item.GetRawText()));
                }

                argumentPageCount++;
                if (!page.Page!.HasMore)
                {
                    break;
                }

                Assert.NotNull(page.Page.NextCursor);
                argumentCursor = page.Page.NextCursor;
                Assert.True(argumentPageCount < 10, "The argument cursor did not converge.");
            }
            while (true);

            Assert.Equal(paramsArguments.Items.Count, pagedArgumentRows.Count);
            Assert.True(argumentPageCount > 1);
        }
        finally
        {
            DeleteTemporaryDirectory(fixture.Root);
        }
    }

    private static async Task<SemanticQueryResponse> QueryAsync(
        IncrementalIndexSession session,
        SemanticQuerySpec specification) =>
        await session.ExecuteSemanticQueryAsync(specification)
            .WaitAsync(TimeSpan.FromSeconds(60));

    private static async Task<JsonElement> QuerySymbolsAsync(
        IncrementalIndexSession session,
        string search,
        string kind)
    {
        var response = await QueryAsync(
            session,
            new SemanticQuerySpec(
                "symbols",
                Search: search,
                Filters: new SemanticQueryFilters(Kind: kind),
                Limit: 100));
        Assert.True(response.Success, response.Error?.Message);
        return Assert.Single(response.Items);
    }

    private static JsonElement FindItem(SemanticQueryResponse response, string label) =>
        Assert.Single(
            response.Items,
            item => string.Equals(
                item.GetProperty("label").GetString(),
                label,
                StringComparison.Ordinal));

    private static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

    private sealed class BlockingReloadLoader : IProjectLoader
    {
        private readonly IProjectLoader _inner;
        private readonly TaskCompletionSource<bool> _releaseReload =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;

        public BlockingReloadLoader(IProjectLoader inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource<bool> ReloadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LoadedSolution> LoadAsync(
            ProjectLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _loadCount) == 2)
            {
                ReloadEntered.TrySetResult(true);
                await _releaseReload.Task.WaitAsync(cancellationToken);
            }

            return await _inner.LoadAsync(request, cancellationToken);
        }

        public void ReleaseReload() => _releaseReload.TrySetResult(true);
    }

    private static Task<Fixture> CreateFixtureAsync() => CreateFixtureAsync(
        "ReferenceFixture",
        "ReferenceFixture.csproj",
        "ReferenceTypes.cs");

    private static Task<Fixture> CreateFixtureAsync(
        string fixtureName,
        string projectFileName,
        string sourceFileName)
    {
        var repositoryRoot = RepositoryRoot();
        var root = Path.Combine(
            Path.GetTempPath(),
            "graphify-csharp-semantic-session-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceRoot = Path.Combine(repositoryRoot, "tests", "Fixtures", fixtureName);
        foreach (var sourcePath in Directory.GetFiles(sourceRoot, "*.cs"))
        {
            File.Copy(sourcePath, Path.Combine(root, Path.GetFileName(sourcePath)));
        }

        var projectPath = Path.Combine(root, projectFileName);
        File.Copy(Path.Combine(sourceRoot, projectFileName), projectPath);
        return Task.FromResult(new Fixture(
            root,
            Path.Combine(root, sourceFileName),
            Path.Combine(root, "graphify-out", "csharp.json"),
            new ProjectLoadRequest(projectPath, root, "Release", "net10.0")));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Graphify.CSharp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record Fixture(
        string Root,
        string SourcePath,
        string OutputPath,
        ProjectLoadRequest Request);
}
