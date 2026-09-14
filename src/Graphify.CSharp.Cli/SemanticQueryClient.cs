using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Cli;

internal sealed class SemanticQueryClient
{
    public async Task<SemanticQueryResponse> SendAsync(
        WatcherSessionDescriptor descriptor,
        SemanticQuerySpec specification,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(specification);
        if (descriptor.SemanticEndpoint is null
            || descriptor.SemanticProtocolVersion != SemanticQueryProtocol.CurrentVersion)
        {
            throw new SemanticClientException(
                "unsupported_capability",
                "The selected watcher does not expose the current semantic query endpoint.");
        }

        SemanticQueryJsonParser.ValidateSpec(specification);
        var analysis = new RefreshRequestIdentity(
            descriptor.InputPath,
            descriptor.RepositoryRoot,
            descriptor.Configuration,
            descriptor.TargetFramework);
        var payload = CreateRequestPayload(descriptor, analysis, specification, timeout);
        if (payload.Length > SemanticQueryProtocol.MaximumRequestBytes)
        {
            throw new SemanticClientException(
                "invalid_request",
                "The semantic request exceeds the 64 KiB request limit.");
        }

        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            descriptor.SemanticEndpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(
                    (int)Math.Min(
                        SemanticQueryProtocol.TransportTimeoutMilliseconds,
                        Math.Max(1, timeout.TotalMilliseconds)),
                    overallTimeout.Token)
                .ConfigureAwait(false);
            await WriteRequestFrameAsync(pipe, payload, overallTimeout.Token).ConfigureAwait(false);
            var responsePayload = await SemanticQueryServer.ReadFrameAsync(
                    pipe,
                    overallTimeout.Token,
                    SemanticQueryProtocol.MaximumResponseBytes)
                .ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SemanticQueryResponse>(
                responsePayload,
                SemanticQueryJson.SerializerOptions);
            if (response is null)
            {
                throw new SemanticClientException(
                    "invalid_response",
                    "The semantic endpoint returned an empty response.");
            }

            ValidateResponse(response, descriptor, specification.Command);
            return response;
        }
        catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new SemanticClientException(
                "timeout",
                "The semantic query request exceeded its deadline.");
        }
        catch (TimeoutException exception)
        {
            throw new SemanticClientException(
                "timeout",
                "The semantic endpoint did not respond within its deadline.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SemanticClientException(
                "unreachable",
                "The semantic endpoint could not be reached.",
                exception);
        }
        catch (IOException exception)
        {
            throw new SemanticClientException(
                "unreachable",
                "The semantic endpoint could not be reached.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new SemanticClientException(
                "invalid_response",
                "The semantic endpoint returned invalid JSON.",
                exception);
        }
        catch (InvalidDataException exception)
        {
            throw new SemanticClientException(
                "invalid_response",
                exception.Message,
                exception);
        }
    }

    private static byte[] CreateRequestPayload(
        WatcherSessionDescriptor descriptor,
        RefreshRequestIdentity analysis,
        SemanticQuerySpec specification,
        TimeSpan timeout)
    {
        object? query = specification.Command == "export"
            ? null
            : new
            {
                search = specification.Search,
                symbol_id = specification.SymbolId,
                filters = specification.Filters is null
                    ? null
                    : new
                    {
                        path = specification.Filters.Path,
                        project = specification.Filters.Project,
                        @namespace = specification.Filters.Namespace,
                        kind = specification.Filters.Kind,
                    },
                direction = specification.Direction,
                group_by = specification.GroupBy,
                limit = specification.Command == "signature"
                    ? (int?)null
                    : specification.Limit,
                cursor = specification.Cursor,
                snapshot_id = specification.SnapshotId,
            };
        var envelope = new
        {
            protocol_version = SemanticQueryProtocol.CurrentVersion,
            session_id = descriptor.SessionId,
            command = specification.Command,
            analysis_key = analysis.CanonicalKey,
            timeout_ms = (int)Math.Clamp(
                Math.Ceiling(timeout.TotalMilliseconds),
                1,
                SemanticQueryProtocol.MaximumTimeoutMilliseconds),
            query,
            output_path = specification.OutputPath,
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, SemanticQueryJson.SerializerOptions);
    }

    private static async Task WriteRequestFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > SemanticQueryProtocol.MaximumRequestBytes)
        {
            throw new InvalidDataException("The semantic request frame is outside the supported size limit.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateResponse(
        SemanticQueryResponse response,
        WatcherSessionDescriptor descriptor,
        string command)
    {
        if (response.SchemaVersion != SemanticQueryProtocol.SchemaVersion
            || response.ProtocolVersion != SemanticQueryProtocol.CurrentVersion)
        {
            throw new SemanticClientException(
                "incompatible_protocol",
                "The semantic endpoint returned an unsupported protocol version.");
        }

        if (response.SessionId != descriptor.SessionId)
        {
            throw new SemanticClientException(
                "session_mismatch",
                "The semantic endpoint returned a response for a different session.");
        }

        if (!string.Equals(response.Command, command, StringComparison.Ordinal)
            && !(response.Command == "unknown"
                && !response.Success
                && response.Error?.Code == "server_busy"))
        {
            throw new SemanticClientException(
                "invalid_response",
                "The semantic endpoint returned a response for a different command.");
        }
    }
}

internal sealed class SemanticClientException : Exception
{
    public SemanticClientException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
