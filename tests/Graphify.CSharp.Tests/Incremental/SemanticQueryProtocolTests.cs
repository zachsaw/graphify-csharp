using System.Buffers.Binary;
using System.Text.Json;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class SemanticQueryProtocolTests
{
    [Fact]
    public async Task Actual_named_pipe_accepts_split_frames_and_dispatches_a_query()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var analysis = CreateAnalysis(root);
        var observed = new TaskCompletionSource<SemanticQuerySpec>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new SemanticQueryServer(
            SemanticQueryProtocol.ForSession(sessionId, root),
            sessionId,
            analysis,
            (specification, _) =>
            {
                observed.TrySetResult(specification);
                return Task.FromResult(Success(sessionId, specification.Command));
            },
            (_, _) => Task.FromResult(Success(sessionId, "export")));

        try
        {
            await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await using var client = await ConnectClientAsync(server.PipeName);
            await WriteFrameInChunksAsync(client, CreatePayload(sessionId, analysis, "symbols"));

            var responsePayload = await SemanticQueryServer.ReadFrameAsync(
                    client,
                    CancellationToken.None,
                    SemanticQueryProtocol.MaximumResponseBytes)
                .WaitAsync(TimeSpan.FromSeconds(5));
            using var response = JsonDocument.Parse(responsePayload);
            var specification = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("symbols", response.RootElement.GetProperty("command").GetString());
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("symbols", specification.Command);
            Assert.Equal("Thing", specification.Search);
            Assert.Equal(1, specification.Limit);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Actual_named_pipe_dispatches_refresh_without_a_query_payload()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var analysis = CreateAnalysis(root);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new SemanticQueryServer(
            SemanticQueryProtocol.ForSession(sessionId, root),
            sessionId,
            analysis,
            (_, _) => Task.FromResult(Success(sessionId, "symbols")),
            (_, _) => Task.FromResult(Success(sessionId, "export")),
            refresh: (rebuild, _) =>
            {
                observed.TrySetResult(rebuild);
                return Task.FromResult(Success(sessionId, "refresh"));
            });

        try
        {
            await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var response = await SendAsync(
                server,
                CreatePayload(sessionId, analysis, "refresh", rebuild: true));

            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("refresh", response.RootElement.GetProperty("command").GetString());
            Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Identity_errors_are_structured_and_do_not_kill_the_endpoint()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var analysis = CreateAnalysis(root);
        var dispatched = 0;
        await using var server = CreateServer(
            root,
            sessionId,
            analysis,
            (_, _) =>
            {
                Interlocked.Increment(ref dispatched);
                return Task.FromResult(Success(sessionId, "symbols"));
            });

        try
        {
            await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

            using var wrongSession = await SendAsync(
                server,
                CreatePayload(Guid.NewGuid(), analysis, "symbols"));
            Assert.Equal("session_mismatch", ErrorCode(wrongSession));

            using var wrongProtocol = await SendAsync(
                server,
                CreatePayload(sessionId, analysis, "symbols", protocolVersion: 99));
            Assert.Equal("incompatible_protocol", ErrorCode(wrongProtocol));

            using var wrongAnalysis = await SendAsync(
                server,
                CreatePayload(
                    sessionId,
                    new RefreshRequestIdentity(
                        analysis.InputPath,
                        analysis.RepositoryRoot,
                        "Debug",
                        analysis.TargetFramework),
                    "symbols"));
            Assert.Equal("session_mismatch", ErrorCode(wrongAnalysis));

            using var valid = await SendAsync(server, CreatePayload(sessionId, analysis, "symbols"));
            Assert.True(valid.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(1, Volatile.Read(ref dispatched));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Extra_client_bytes_cancel_the_operation_as_a_protocol_error()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var analysis = CreateAnalysis(root);
        await using var server = CreateServer(
            root,
            sessionId,
            analysis,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Success(sessionId, "symbols");
            });

        try
        {
            await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var response = await SendAsync(
                server,
                CreatePayload(sessionId, analysis, "symbols"),
                extraBytes: [0x7f]);

            Assert.Equal("invalid_request", ErrorCode(response));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Admission_limit_returns_server_busy_without_unbounded_waiters()
    {
        var root = CreateTemporaryDirectory();
        var sessionId = Guid.NewGuid();
        var analysis = CreateAnalysis(root);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new SemanticQueryServer(
            SemanticQueryProtocol.ForSession(sessionId, root),
            sessionId,
            analysis,
            async (_, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return Success(sessionId, "symbols");
            },
            (_, _) => Task.FromResult(Success(sessionId, "export")),
            maximumConcurrentRequests: 1,
            transportTimeout: TimeSpan.FromSeconds(2));

        try
        {
            await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var first = SendAsync(server, CreatePayload(sessionId, analysis, "symbols"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var busy = await ReadAdmissionResponseAsync(server);
            Assert.Equal("server_busy", ErrorCode(busy));

            release.TrySetResult(true);
            using var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(completed.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            release.TrySetResult(true);
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Parser_rejects_unknown_fields_invalid_kinds_and_unsupported_options()
    {
        var sessionId = Guid.NewGuid();
        var analysis = CreateAnalysis(CreateTemporaryDirectory());

        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                Json("""{"protocol_version":1,"session_id":"00000000-0000-0000-0000-000000000001","command":"symbols","analysis_key":"x","query":{"unknown":true}}"""))).Code);
        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                Json("""{"protocol_version":1,"session_id":"00000000-0000-0000-0000-000000000001","command":"symbols","analysis_key":"x","analysis_key":"y"}"""))).Code);
        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                Json("""{"protocol_version":1,"session_id":"00000000-0000-0000-0000-000000000001","command":"symbols","analysis_key":"x","query":{"filters":{"kind":"method","kind":"class"}}}"""))).Code);
        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                CreateCustomPayload(
                    sessionId,
                    analysis,
                    "symbols",
                    new { filters = new { kind = "not-a-kind" } }))).Code);
        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                CreateCustomPayload(
                    sessionId,
                    analysis,
                    "signature",
                    new { symbol_id = "id", limit = 1 }))).Code);
        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                CreateCustomPayload(
                    sessionId,
                    analysis,
                    "symbols",
                    query: null,
                    outputPath: "out.json"))).Code);

        var refresh = SemanticQueryJsonParser.Parse(
            CreateCustomPayload(
                sessionId,
                analysis,
                "refresh",
                query: null,
                rebuild: true));
        Assert.Equal("refresh", refresh.Command);
        Assert.True(refresh.Spec.Rebuild);
        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                CreateCustomPayload(
                    sessionId,
                    analysis,
                    "refresh",
                    query: new { limit = 1 }))).Code);
        Assert.Equal(
            "invalid_request",
            Assert.Throws<SemanticQueryException>(() => SemanticQueryJsonParser.Parse(
                CreateCustomPayload(
                    sessionId,
                    analysis,
                    "symbols",
                    query: null,
                    rebuild: true))).Code);
    }

    [Fact]
    public void Explicit_global_namespace_has_a_distinct_query_identity()
    {
        var omitted = new SemanticQuerySpec("symbols");
        var global = new SemanticQuerySpec(
            "symbols",
            Filters: new SemanticQueryFilters(Namespace: string.Empty));

        Assert.NotEqual(omitted.QueryHash, global.QueryHash);
    }

    [Fact]
    public void Scope_filters_use_the_documented_snake_case_wire_names()
    {
        var json = JsonSerializer.Serialize(
            new SemanticQueryScope(
                "analysis",
                new SemanticQueryFilters("src/Thing.cs", "src/Thing.csproj", "Example.Namespace", "class"),
                "observed_static",
                true,
                false,
                true,
                []),
            SemanticQueryJson.SerializerOptions);

        Assert.Contains("\"path\"", json, StringComparison.Ordinal);
        Assert.Contains("\"project\"", json, StringComparison.Ordinal);
        Assert.Contains("\"namespace\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Path\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Project\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_accepts_search_for_usage_summary()
    {
        var root = CreateTemporaryDirectory();
        var analysis = CreateAnalysis(root);
        var request = SemanticQueryJsonParser.Parse(
            CreateCustomPayload(
                Guid.NewGuid(),
                analysis,
                "usage-summary",
                new
                {
                    search = "Called(int)",
                    filters = new { kind = "method" },
                    group_by = new[] { "project", "namespace" },
                    limit = 10,
                }));

        Assert.Equal("usage-summary", request.Command);
        Assert.Equal("Called(int)", request.Spec.Search);
        Assert.Equal(["project", "namespace"], request.Spec.EffectiveGroupBy);
        Assert.Equal("method", request.Spec.EffectiveFilters.Kind);
        DeleteTemporaryDirectory(root);
    }

    private static SemanticQueryServer CreateServer(
        string root,
        Guid sessionId,
        RefreshRequestIdentity analysis,
        Func<SemanticQuerySpec, CancellationToken, Task<SemanticQueryResponse>> query) =>
        new(
            SemanticQueryProtocol.ForSession(sessionId, root),
            sessionId,
            analysis,
            query,
            (_, _) => Task.FromResult(Success(sessionId, "export")));

    private static RefreshRequestIdentity CreateAnalysis(string root) =>
        new(Path.Combine(root, "Test.csproj"), root, "Release", "net10.0");

    private static Task<Stream> ConnectClientAsync(string pipeName) =>
        LocalIpcTransport.ConnectAsync(pipeName, TimeSpan.FromSeconds(5));

    private static async Task<JsonDocument> SendAsync(
        SemanticQueryServer server,
        byte[] payload,
        byte[]? extraBytes = null)
    {
        await using var client = await ConnectClientAsync(server.PipeName);
        await WriteFrameInChunksAsync(client, payload, extraBytes);
        var responsePayload = await SemanticQueryServer.ReadFrameAsync(
                client,
                CancellationToken.None,
                SemanticQueryProtocol.MaximumResponseBytes)
            .WaitAsync(TimeSpan.FromSeconds(5));
        return JsonDocument.Parse(responsePayload);
    }

    private static async Task<JsonDocument> ReadAdmissionResponseAsync(SemanticQueryServer server)
    {
        await using var client = await ConnectClientAsync(server.PipeName);
        var responsePayload = await SemanticQueryServer.ReadFrameAsync(
                client,
                CancellationToken.None,
                SemanticQueryProtocol.MaximumResponseBytes)
            .WaitAsync(TimeSpan.FromSeconds(5));
        return JsonDocument.Parse(responsePayload);
    }

    private static async Task WriteFrameInChunksAsync(
        Stream stream,
        byte[] payload,
        byte[]? extraBytes = null)
    {
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header.AsMemory(0, 1));
        await stream.WriteAsync(header.AsMemory(1, 3));
        for (var offset = 0; offset < payload.Length; offset += 3)
        {
            var count = Math.Min(3, payload.Length - offset);
            await stream.WriteAsync(payload.AsMemory(offset, count));
        }

        if (extraBytes is not null)
        {
            await stream.WriteAsync(extraBytes);
        }

        await stream.FlushAsync();
    }

    private static byte[] CreatePayload(
        Guid sessionId,
        RefreshRequestIdentity analysis,
        string command,
        int protocolVersion = SemanticQueryProtocol.CurrentVersion,
        bool rebuild = false) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                protocol_version = protocolVersion,
                session_id = sessionId,
                command,
                analysis_key = analysis.CanonicalKey,
                timeout_ms = 5000,
                query = command is "export" or "refresh"
                    ? null
                    : new
                    {
                        search = "Thing",
                        limit = 1,
                    },
                rebuild = command == "refresh" ? rebuild : (bool?)null,
            },
            SemanticQueryJson.SerializerOptions);

    private static byte[] CreateCustomPayload(
        Guid sessionId,
        RefreshRequestIdentity analysis,
        string command,
        object? query,
        string? outputPath = null,
        bool? rebuild = null) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                protocol_version = SemanticQueryProtocol.CurrentVersion,
                session_id = sessionId,
                command,
                analysis_key = analysis.CanonicalKey,
                timeout_ms = 5000,
                query,
                output_path = outputPath,
                rebuild,
            },
            SemanticQueryJson.SerializerOptions);

    private static SemanticQueryResponse Success(Guid sessionId, string command) =>
        SemanticQueryResponse.SuccessResponse(
            sessionId,
            "instance",
            command,
            new SemanticQuerySnapshot("snapshot", 0, 0, 0),
            new SemanticQueryScope(
                "analysis",
                new SemanticQueryFilters(),
                "observed_static",
                true,
                false,
                true,
                []),
            [],
            new SemanticQueryPage(0, false, null),
            []);

    private static string? ErrorCode(JsonDocument document) =>
        document.RootElement.GetProperty("error").GetProperty("code").GetString();

    private static byte[] Json(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "graphify-csharp-semantic-protocol", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
