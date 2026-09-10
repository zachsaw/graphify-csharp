using System.IO.Pipes;
using System.Text.Json;

namespace Graphify.CSharp.Incremental;

internal sealed class IncrementalRefreshControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public async Task<IncrementalRefreshControlServer.ControlResponse?> TryRefreshAsync(
        RefreshRequestIdentity request,
        string outputPath,
        bool rebuild,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outputPathIdentity = IncrementalRefreshControlChannel.OutputPathIdentity(outputPath);
        var pipeName = IncrementalRefreshControlChannel.ForRequest(request, outputPath);
        var leasePath = WatcherLease.ForOutput(outputPath, request);
        var firstAttempt = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IncrementalRefreshControlServer.ControlResponse? response;
            try
            {
                response = await TryConnectAndRequestAsync(
                        pipeName,
                        request.Digest,
                        outputPathIdentity,
                        rebuild,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (WatcherLease.IsHeld(leasePath))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                firstAttempt = false;
                continue;
            }
            if (response is not null)
            {
                if (!response.Success)
                {
                    var message = response.Message ?? "The watcher rejected the refresh request.";
                    throw new InvalidOperationException(
                        response.ErrorCode is null
                            ? message
                            : $"Watcher request failed ({response.ErrorCode}): {message}");
                }

                if (!string.Equals(response.RequestDigest, request.Digest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The watcher returned a response for a different analysis configuration.");
                }

                if (!string.Equals(response.OutputPathIdentity, outputPathIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The watcher returned a response for a different output path.");
                }

                return response;
            }

            if (!firstAttempt && !WatcherLease.IsHeld(leasePath))
            {
                return null;
            }

            firstAttempt = false;
            if (!WatcherLease.IsHeld(leasePath))
            {
                // Give a watcher that is just acquiring its lease one short
                // opportunity before falling back to a standalone refresh.
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                if (!WatcherLease.IsHeld(leasePath))
                {
                    return null;
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IncrementalRefreshControlServer.ControlResponse?> TryConnectAndRequestAsync(
        string pipeName,
        string requestDigest,
        string outputPathIdentity,
        bool rebuild,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        using var writer = new StreamWriter(
            pipe,
            System.Text.Encoding.UTF8,
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var reader = new StreamReader(
            pipe,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var command = rebuild ? "rebuild" : "refresh";
        await writer.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    command,
                    request_digest = requestDigest,
                    output_path_identity = outputPathIdentity,
                },
                JsonOptions))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("The watcher closed the refresh control channel without a response.");
        }

        return JsonSerializer.Deserialize<IncrementalRefreshControlServer.ControlResponse>(line, JsonOptions)
            ?? throw new InvalidDataException("The watcher returned an empty refresh response.");
    }
}
