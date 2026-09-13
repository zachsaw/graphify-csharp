using System.Text;
using System.Text.Json;
using Graphify.CSharp.Incremental;

namespace Graphify.CSharp.Tests.Incremental;

public sealed class WatcherSessionRegistryTests
{
    [Fact]
    public void Registers_and_reads_independent_session_descriptors()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var registry = new WatcherSessionRegistry(root);
            var first = CreateDescriptor(Guid.NewGuid());
            var second = CreateDescriptor(Guid.NewGuid());

            registry.Register(first);
            registry.Register(second);

            var records = registry.ReadAll();
            Assert.Equal(2, records.Count);
            Assert.Equal(
                new[] { first.SessionId, second.SessionId }.OrderBy(id => id),
                records.Select(record => record.Descriptor!.SessionId).OrderBy(id => id));
            Assert.All(records, record => Assert.True(record.IsValid));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Replaces_one_descriptor_atomically_without_affecting_another()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var registry = new WatcherSessionRegistry(root);
            var first = CreateDescriptor(Guid.NewGuid());
            var second = CreateDescriptor(Guid.NewGuid());
            registry.Register(first);
            registry.Register(second);

            var replacement = first with { OutputPath = "/tmp/replacement.json" };
            registry.Register(replacement);
            registry.Remove(first.SessionId);

            var records = registry.ReadAll();
            var remaining = Assert.Single(records);
            Assert.Equal(second.SessionId, remaining.Descriptor!.SessionId);
            Assert.False(File.Exists(registry.DescriptorPath(first.SessionId)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Reports_malformed_oversized_and_filename_mismatch_records_without_discarding_valid_records()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var registry = new WatcherSessionRegistry(root);
            var valid = CreateDescriptor(Guid.NewGuid());
            registry.Register(valid);
            var malformedPath = Path.Combine(root, $"{Guid.NewGuid():D}.json");
            await File.WriteAllTextAsync(malformedPath, "not json");
            var mismatchPath = Path.Combine(root, $"{Guid.NewGuid():D}.json");
            await File.WriteAllTextAsync(
                mismatchPath,
                $$"""{"schema_version":"graphify-csharp/session/v1","session_id":"{{valid.SessionId:D}}","process_id":1,"management_endpoint":"gcm-test","input_path":"/tmp/input.csproj","repository_root":"/tmp","configuration":"Debug","output_path":"/tmp/out.json","tool_version":"test","protocol_version":1}""");
            var oversizedPath = Path.Combine(root, $"{Guid.NewGuid():D}.json");
            await File.WriteAllTextAsync(oversizedPath, new string('x', 64 * 1024 + 1), Encoding.UTF8);

            var records = registry.ReadAll();

            Assert.Contains(records, record => record.Descriptor?.SessionId == valid.SessionId);
            Assert.Equal(3, records.Count(record => !record.IsValid));
            Assert.All(
                records.Where(record => !record.IsValid),
                record => Assert.Contains(record.ErrorCode, new[] { "invalid_descriptor" }));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Reports_invalid_endpoint_without_discarding_valid_records()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var registry = new WatcherSessionRegistry(root);
            var valid = CreateDescriptor(Guid.NewGuid());
            registry.Register(valid);

            var invalid = CreateDescriptor(Guid.NewGuid()) with { ManagementEndpoint = "\0" };
            await File.WriteAllTextAsync(
                registry.DescriptorPath(invalid.SessionId),
                JsonSerializer.Serialize(invalid));

            var records = registry.ReadAll();

            Assert.Contains(records, record => record.Descriptor?.SessionId == valid.SessionId);
            var invalidRecord = Assert.Single(
                records,
                record => record.Path == registry.DescriptorPath(invalid.SessionId));
            Assert.False(invalidRecord.IsValid);
            Assert.Equal("invalid_descriptor", invalidRecord.ErrorCode);
            Assert.Contains("endpoint", invalidRecord.Diagnostic!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Removing_session_a_cannot_remove_session_b()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var registry = new WatcherSessionRegistry(root);
            var first = CreateDescriptor(Guid.NewGuid());
            var second = CreateDescriptor(Guid.NewGuid());
            registry.Register(first);
            registry.Register(second);

            registry.Remove(first.SessionId);

            Assert.False(File.Exists(registry.DescriptorPath(first.SessionId)));
            Assert.True(File.Exists(registry.DescriptorPath(second.SessionId)));
            Assert.Equal(second.SessionId, Assert.Single(registry.ReadAll()).Descriptor!.SessionId);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Missing_state_directory_is_an_empty_registry()
    {
        var root = Path.Combine(Path.GetTempPath(), "graphify-csharp-registry-tests", Guid.NewGuid().ToString("N"));
        var registry = new WatcherSessionRegistry(root);

        Assert.Empty(registry.ReadAll());
    }

    private static WatcherSessionDescriptor CreateDescriptor(Guid sessionId) =>
        new(
            WatcherSessionRegistry.DescriptorSchemaVersion,
            sessionId,
            Environment.ProcessId,
            WatcherProcessIdentity.CurrentStartTimeUtcTicks(),
            WatcherManagementProtocol.ForSession(sessionId, Path.GetTempPath()),
            "/tmp/input.csproj",
            "/tmp",
            "Debug",
            "net10.0",
            "/tmp/output.json",
            "test",
            WatcherManagementProtocol.CurrentVersion);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "graphify-csharp-registry-tests", Guid.NewGuid().ToString("N"));
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
