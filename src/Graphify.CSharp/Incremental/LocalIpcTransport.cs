using System.IO.Pipes;
using System.Net.Sockets;

namespace Graphify.CSharp.Incremental;

/// <summary>
/// Provides a current-user local transport without relying on multiple
/// same-name <see cref="NamedPipeServerStream"/> instances on Unix. The
/// Unix implementation uses one domain-socket listener and can accept
/// connections concurrently; Windows keeps the native named-pipe transport.
/// </summary>
internal static class LocalIpcTransport
{
    private const string UnixSocketDirectoryPrefix = "gcs-";

    public static LocalIpcListener CreateListener(string endpointName) =>
        OperatingSystem.IsWindows()
            ? new NamedPipeIpcListener(ValidateEndpointName(endpointName))
            : new UnixDomainSocketIpcListener(ValidateEndpointName(endpointName));

    public static async Task<Stream> ConnectAsync(
        string endpointName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        endpointName = ValidateEndpointName(endpointName);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The IPC connection timeout must be positive.");
        }

        if (OperatingSystem.IsWindows())
        {
            var client = new NamedPipeClientStream(
                ".",
                endpointName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(ToTimeoutMilliseconds(timeout), cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(GetUnixSocketPath(endpointName)),
                        timeoutSource.Token)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception) when (IsTransientConnectFailure(exception.SocketErrorCode))
            {
                socket.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutSource.IsCancellationRequested)
                {
                    throw new TimeoutException("The local IPC connection timed out.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The local IPC connection timed out.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new TimeoutException("The local IPC connection timed out.");
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                throw new IOException("The local IPC connection failed.", exception);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    internal static string GetUnixSocketPath(string endpointName) =>
        Path.Combine(GetUnixSocketDirectory(), ValidateEndpointName(endpointName));

    private static string GetUnixSocketDirectory() =>
        Path.Combine(
            "/tmp",
            UnixSocketDirectoryPrefix
                + IncrementalHashing.Sha256(
                    string.IsNullOrWhiteSpace(Environment.UserName)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        : Environment.UserName)[..16]);

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        (int)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);

    private static bool IsTransientConnectFailure(SocketError error) =>
        error is SocketError.AddressNotAvailable
            or SocketError.ConnectionRefused;

    private static string ValidateEndpointName(string endpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        if (endpointName.Length > 64
            || endpointName.Any(static character =>
                !(character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_')))
        {
            throw new ArgumentException("The local IPC endpoint name contains unsupported characters.", nameof(endpointName));
        }

        return endpointName;
    }
}

internal abstract class LocalIpcListener : IDisposable
{
    public abstract Task<Stream> AcceptAsync(CancellationToken cancellationToken);

    public abstract void Dispose();
}

internal sealed class NamedPipeIpcListener : LocalIpcListener
{
    private readonly string _endpointName;
    private readonly object _gate = new();
    private NamedPipeServerStream? _pendingServer;
    private bool _disposed;

    public NamedPipeIpcListener(string endpointName)
    {
        _endpointName = endpointName;
    }

    public override async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var server = new NamedPipeServerStream(
            _endpointName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        lock (_gate)
        {
            if (_disposed)
            {
                server.Dispose();
                throw new ObjectDisposedException(nameof(NamedPipeIpcListener));
            }

            _pendingServer = server;
        }

        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingServer, server))
                {
                    _pendingServer = null;
                }
            }
        }
    }

    public override void Dispose()
    {
        NamedPipeServerStream? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pendingServer;
        }

        pending?.Dispose();
    }
}

internal sealed class UnixDomainSocketIpcListener : LocalIpcListener
{
    private readonly string _socketPath;
    private readonly Socket _listener;
    private int _disposed;

    public UnixDomainSocketIpcListener(string endpointName)
    {
        _socketPath = LocalIpcTransport.GetUnixSocketPath(endpointName);
        var directory = Path.GetDirectoryName(_socketPath)
            ?? throw new InvalidOperationException("The local IPC socket directory could not be resolved.");
        Directory.CreateDirectory(directory);
#pragma warning disable CA1416 // This listener is only constructed on non-Windows platforms.
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        RemoveStaleSocket(_socketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var bound = false;
        try
        {
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            bound = true;
            _listener.Listen(backlog: 128);
#pragma warning disable CA1416 // This listener is only constructed on non-Windows platforms.
            File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
        }
        catch (SocketException exception)
        {
            _listener.Dispose();
            if (bound)
            {
                TryDeleteSocket(_socketPath);
            }

            throw new IOException("The local IPC listener could not be created.", exception);
        }
        catch
        {
            _listener.Dispose();
            if (bound)
            {
                TryDeleteSocket(_socketPath);
            }

            throw;
        }
    }

    public override async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            var socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException exception)
        {
            throw new IOException("The local IPC listener failed.", exception);
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _listener.Dispose();
        TryDeleteSocket(_socketPath);
    }

    private static void RemoveStaleSocket(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            throw new IOException($"The local IPC endpoint '{path}' is already in use.");
        }
        catch (SocketException exception) when (exception.SocketErrorCode is
            SocketError.AddressNotAvailable or SocketError.ConnectionRefused)
        {
            TryDeleteSocket(path);
        }
    }

    private static void TryDeleteSocket(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
