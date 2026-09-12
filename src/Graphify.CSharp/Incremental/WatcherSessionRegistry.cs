using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graphify.CSharp.Incremental;

internal sealed record WatcherSessionDescriptorReadResult(
    string Path,
    WatcherSessionDescriptor? Descriptor,
    string? ErrorCode,
    string? Diagnostic)
{
    public bool IsValid => Descriptor is not null;
}

internal sealed class WatcherSessionRegistry
{
    public const string DescriptorSchemaVersion = "graphify-csharp/session/v1";
    private const string StateDirectoryEnvironmentVariable = "GRAPHIFY_CSHARP_STATE_DIR";
    private const int MaximumDescriptorBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _stateDirectory;

    public WatcherSessionRegistry(string stateDirectory)
    {
        _stateDirectory = IncrementalPaths.CanonicalAbsolutePath(stateDirectory);
    }

    public string StateDirectory => _stateDirectory;

    public string DescriptorPath(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session descriptor requires a session ID.", nameof(sessionId));
        }

        return Path.Combine(_stateDirectory, $"{sessionId:D}.json");
    }

    public void Register(WatcherSessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor);
        Directory.CreateDirectory(_stateDirectory);

        var destination = DescriptorPath(descriptor.SessionId);
        var temporary = Path.Combine(
            _stateDirectory,
            $".{descriptor.SessionId:D}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(descriptor, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaximumDescriptorBytes)
            {
                throw new InvalidDataException("The watcher session descriptor exceeds the size limit.");
            }

            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void Remove(Guid sessionId)
    {
        var path = DescriptorPath(sessionId);
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

    public IReadOnlyList<WatcherSessionDescriptorReadResult> ReadAll()
    {
        string[] paths;
        try
        {
            paths = Directory
                .EnumerateFiles(_stateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, IncrementalPaths.PathComparer)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<WatcherSessionDescriptorReadResult>();
        }

        var results = new List<WatcherSessionDescriptorReadResult>(paths.Length);
        foreach (var path in paths)
        {
            results.Add(ReadOne(path));
        }

        return results;
    }

    public static string ResolveStateDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(StateDirectoryEnvironmentVariable);
        if (configured is not null)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{StateDirectoryEnvironmentVariable}' cannot be empty.");
            }

            return IncrementalPaths.CanonicalAbsolutePath(configured);
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "The per-user application-data directory could not be resolved. Set GRAPHIFY_CSHARP_STATE_DIR explicitly.");
        }

        return Path.Combine(localApplicationData, "Graphify.CSharp", "sessions");
    }

    private static WatcherSessionDescriptorReadResult ReadOne(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!Guid.TryParseExact(stem, "D", out var fileSessionId))
        {
            return Invalid(path, "invalid_filename", "The descriptor filename is not a session GUID.");
        }

        try
        {
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > MaximumDescriptorBytes)
            {
                return Invalid(path, "invalid_descriptor", "The descriptor is empty or exceeds the size limit.");
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var descriptor = JsonSerializer.Deserialize<WatcherSessionDescriptor>(json, JsonOptions);
            if (descriptor is null)
            {
                return Invalid(path, "invalid_descriptor", "The descriptor is empty.");
            }

            ValidateDescriptor(descriptor);
            if (descriptor.SessionId != fileSessionId)
            {
                return Invalid(path, "invalid_descriptor", "The descriptor session ID does not match its filename.");
            }

            return new WatcherSessionDescriptorReadResult(path, descriptor, null, null);
        }
        catch (JsonException exception)
        {
            return Invalid(path, "invalid_descriptor", $"The descriptor JSON is invalid: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            return Invalid(path, "invalid_descriptor", exception.Message);
        }
        catch (FileNotFoundException exception)
        {
            return Invalid(path, "unreadable_descriptor", $"The descriptor could not be read: {exception.Message}");
        }
        catch (DirectoryNotFoundException exception)
        {
            return Invalid(path, "unreadable_descriptor", $"The descriptor could not be read: {exception.Message}");
        }
        catch (IOException exception)
        {
            return Invalid(path, "unreadable_descriptor", $"The descriptor could not be read: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Invalid(path, "unreadable_descriptor", $"The descriptor could not be read: {exception.Message}");
        }
    }

    private static void ValidateDescriptor(WatcherSessionDescriptor descriptor)
    {
        if (!string.Equals(descriptor.SchemaVersion, DescriptorSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The descriptor schema '{descriptor.SchemaVersion}' is not supported.");
        }

        if (descriptor.SessionId == Guid.Empty
            || descriptor.ProcessId <= 0
            || descriptor.ProtocolVersion != WatcherManagementProtocol.CurrentVersion
            || string.IsNullOrWhiteSpace(descriptor.ManagementEndpoint)
            || string.IsNullOrWhiteSpace(descriptor.InputPath)
            || string.IsNullOrWhiteSpace(descriptor.RepositoryRoot)
            || string.IsNullOrWhiteSpace(descriptor.Configuration)
            || string.IsNullOrWhiteSpace(descriptor.OutputPath)
            || string.IsNullOrWhiteSpace(descriptor.ToolVersion))
        {
            throw new InvalidDataException("The watcher session descriptor is incomplete.");
        }

        if (!WatcherManagementProtocol.IsValidEndpoint(descriptor.ManagementEndpoint))
        {
            throw new InvalidDataException("The watcher session descriptor has an invalid management endpoint.");
        }

        if (descriptor.TargetFramework is not null && string.IsNullOrWhiteSpace(descriptor.TargetFramework))
        {
            throw new InvalidDataException("The watcher session descriptor has an empty target framework.");
        }
    }

    private static WatcherSessionDescriptorReadResult Invalid(string path, string errorCode, string diagnostic) =>
        new(path, null, errorCode, diagnostic);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
