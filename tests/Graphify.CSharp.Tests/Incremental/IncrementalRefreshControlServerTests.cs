using System.IO.Pipes;
using System.Text.Json;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class IncrementalRefreshControlServerTests
{
    [Fact]
    public async Task Rejects_analysis_identity_mismatch_before_running_refresh()
    {
        var expectedRequest = CreateRequest("Release");
        var outputPath = Path.Combine(Path.GetTempPath(), "graphify-csharp-control", "canonical.json");
        var refreshCalls = 0;
        var pipeName = $"gcf-test-{Guid.NewGuid():N}";
        await using var server = new IncrementalRefreshControlServer(
            pipeName,
            expectedRequest,
            outputPath,
            (_, _) =>
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromException<IncrementalRefreshResult>(
                    new InvalidOperationException("The refresh callback must not run."));
            });
        server.Start();

        var response = await SendRequestAsync(
            pipeName,
            new
            {
                command = "refresh",
                request_digest = CreateRequest("Debug").Digest,
                output_path_identity = IncrementalRefreshControlChannel.OutputPathIdentity(outputPath),
            });

        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal("configuration_mismatch", response.GetProperty("error_code").GetString());
        Assert.Equal(0, Volatile.Read(ref refreshCalls));
    }

    [Fact]
    public async Task Rejects_output_identity_mismatch_before_running_refresh()
    {
        var expectedRequest = CreateRequest("Release");
        var outputPath = Path.Combine(Path.GetTempPath(), "graphify-csharp-control", "canonical.json");
        var alternateOutputPath = Path.Combine(Path.GetTempPath(), "graphify-csharp-control", "alternate.json");
        var refreshCalls = 0;
        var pipeName = $"gcf-test-{Guid.NewGuid():N}";
        await using var server = new IncrementalRefreshControlServer(
            pipeName,
            expectedRequest,
            outputPath,
            (_, _) =>
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromException<IncrementalRefreshResult>(
                    new InvalidOperationException("The refresh callback must not run."));
            });
        server.Start();

        var response = await SendRequestAsync(
            pipeName,
            new
            {
                command = "refresh",
                request_digest = expectedRequest.Digest,
                output_path_identity = IncrementalRefreshControlChannel.OutputPathIdentity(alternateOutputPath),
            });

        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal("output_mismatch", response.GetProperty("error_code").GetString());
        Assert.Equal(0, Volatile.Read(ref refreshCalls));
    }

    private static RefreshRequestIdentity CreateRequest(string configuration) =>
        new(
            Path.Combine(Path.GetTempPath(), "graphify-csharp-control", "App.csproj"),
            Path.Combine(Path.GetTempPath(), "graphify-csharp-control"),
            configuration,
            "net10.0");

    private static async Task<JsonElement> SendRequestAsync(string pipeName, object request)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        using var writer = new StreamWriter(
            client,
            System.Text.Encoding.UTF8,
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var reader = new StreamReader(
            client,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrWhiteSpace(line));
        using var document = JsonDocument.Parse(line!);
        return document.RootElement.Clone();
    }
}
