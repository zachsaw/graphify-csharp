using System.Text.Json;
using System.Text.Json.Serialization;
using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Incremental;

internal enum IncrementalCacheLoadStatus
{
    Loaded,
    Missing,
    Incompatible,
    Corrupt,
    Unavailable,
}

internal sealed class IncrementalCacheLoadResult
{
    private IncrementalCacheLoadResult(
        IncrementalCacheLoadStatus status,
        IncrementalCacheState? state,
        string? message)
    {
        Status = status;
        State = state;
        Message = message;
    }

    public IncrementalCacheLoadStatus Status { get; }

    public IncrementalCacheState? State { get; }

    public string? Message { get; }

    public static IncrementalCacheLoadResult Loaded(IncrementalCacheState state) =>
        new(IncrementalCacheLoadStatus.Loaded, state ?? throw new ArgumentNullException(nameof(state)), null);

    public static IncrementalCacheLoadResult Unusable(IncrementalCacheLoadStatus status, string message) =>
        new(status, null, message);
}

internal interface IAtomicCacheCommitter
{
    void Commit(string temporaryPath, string destinationPath);
}

internal sealed class FileAtomicCacheCommitter : IAtomicCacheCommitter
{
    public void Commit(string temporaryPath, string destinationPath)
    {
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }
}

internal sealed class IncrementalCacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IAtomicCacheCommitter _committer;

    public IncrementalCacheStore(IAtomicCacheCommitter? committer = null)
    {
        _committer = committer ?? new FileAtomicCacheCommitter();
    }

    public async Task<IncrementalCacheLoadResult> LoadAsync(
        string path,
        RefreshRequestIdentity expectedRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedRequest);

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<IncrementalCacheDocumentDto>(json, SerializerOptions);
            if (document is null)
            {
                return IncrementalCacheLoadResult.Unusable(
                    IncrementalCacheLoadStatus.Corrupt,
                    "The incremental cache document is empty.");
            }

            if (!string.Equals(document.SchemaVersion, RefreshRequestIdentity.CurrentCacheSchemaVersion, StringComparison.Ordinal))
            {
                return IncrementalCacheLoadResult.Unusable(
                    IncrementalCacheLoadStatus.Incompatible,
                    $"The incremental cache schema '{document.SchemaVersion ?? "<missing>"}' is not supported.");
            }

            var state = document.ToModel();
            if (!expectedRequest.Matches(state.Request))
            {
                return IncrementalCacheLoadResult.Unusable(
                    IncrementalCacheLoadStatus.Incompatible,
                    "The incremental cache belongs to a different refresh request.");
            }

            return IncrementalCacheLoadResult.Loaded(state);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Missing,
                "The incremental cache file does not exist.");
        }
        catch (DirectoryNotFoundException)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Missing,
                "The incremental cache directory does not exist.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Unavailable,
                $"The incremental cache could not be read: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Corrupt,
                $"The incremental cache is invalid: {exception.Message}");
        }
        catch (IOException exception)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Unavailable,
                $"The incremental cache could not be read: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Corrupt,
                $"The incremental cache contains invalid JSON: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Corrupt,
                $"The incremental cache is invalid: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return IncrementalCacheLoadResult.Unusable(
                IncrementalCacheLoadStatus.Corrupt,
                $"The incremental cache uses unsupported data: {exception.Message}");
        }
    }

    public async Task SaveAsync(
        string path,
        IncrementalCacheState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);

        var destinationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"Cache path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    IncrementalCacheDocumentDto.FromModel(state),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _committer.Commit(temporaryPath, destinationPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The destination has not been touched if cleanup fails. The next
            // cache load ignores these temporary files by using the exact path.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }

    private sealed class IncrementalCacheDocumentDto
    {
        [JsonPropertyName("schema_version")]
        public string? SchemaVersion { get; init; }

        [JsonPropertyName("request")]
        public RequestIdentityDto? Request { get; init; }

        [JsonPropertyName("generation")]
        public GenerationDto? Generation { get; init; }

        [JsonPropertyName("manifest")]
        public List<ManifestEntryDto>? Manifest { get; init; }

        [JsonPropertyName("contributions")]
        public List<ContributionDto>? Contributions { get; init; }

        [JsonPropertyName("diagnostics")]
        public List<string>? Diagnostics { get; init; }

        [JsonPropertyName("published_output_path")]
        public string? PublishedOutputPath { get; init; }

        [JsonPropertyName("published_output_digest")]
        public string? PublishedOutputDigest { get; init; }

        public IncrementalCacheState ToModel()
        {
            if (Request is null || Generation is null || Manifest is null || Contributions is null || Diagnostics is null)
            {
                throw new InvalidDataException("The incremental cache is missing a required section.");
            }

            var request = Request.ToModel();
            var generation = Generation.ToModel();
            var contributions = Contributions
                .Select(contribution => contribution.ToModel())
                .ToArray();
            var manifest = Manifest
                .Select(entry => entry.ToModel())
                .ToArray();
            return new IncrementalCacheState(
                request,
                contributions,
                manifest,
                generation,
                Diagnostics,
                PublishedOutputPath,
                PublishedOutputDigest);
        }

        public static IncrementalCacheDocumentDto FromModel(IncrementalCacheState state) => new()
        {
            SchemaVersion = RefreshRequestIdentity.CurrentCacheSchemaVersion,
            Request = RequestIdentityDto.FromModel(state.Request),
            Generation = GenerationDto.FromModel(state.Generation),
            Manifest = state.Manifest.Select(ManifestEntryDto.FromModel).ToList(),
            Contributions = state.Contributions.Select(ContributionDto.FromModel).ToList(),
            Diagnostics = state.Diagnostics.ToList(),
            PublishedOutputPath = state.PublishedOutputPath,
            PublishedOutputDigest = state.PublishedOutputDigest,
        };
    }

    private sealed class RequestIdentityDto
    {
        [JsonPropertyName("input_path")]
        public string? InputPath { get; init; }

        [JsonPropertyName("repository_root")]
        public string? RepositoryRoot { get; init; }

        [JsonPropertyName("configuration")]
        public string? Configuration { get; init; }

        [JsonPropertyName("target_framework")]
        public string? TargetFramework { get; init; }

        [JsonPropertyName("graph_schema_version")]
        public string? GraphSchemaVersion { get; init; }

        [JsonPropertyName("extractor_version")]
        public string? ExtractorVersion { get; init; }

        [JsonPropertyName("cache_schema_version")]
        public string? CacheSchemaVersion { get; init; }

        [JsonPropertyName("canonical_key")]
        public string? CanonicalKey { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        public RefreshRequestIdentity ToModel()
        {
            if (InputPath is null
                || RepositoryRoot is null
                || Configuration is null
                || GraphSchemaVersion is null
                || ExtractorVersion is null
                || CacheSchemaVersion is null)
            {
                throw new InvalidDataException("The incremental cache request identity is incomplete.");
            }

            var model = new RefreshRequestIdentity(
                InputPath,
                RepositoryRoot,
                Configuration,
                TargetFramework,
                GraphSchemaVersion,
                ExtractorVersion,
                CacheSchemaVersion);
            if (CanonicalKey is null
                || Digest is null
                || !string.Equals(CanonicalKey, model.CanonicalKey, StringComparison.Ordinal)
                || !string.Equals(Digest, model.Digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The incremental cache request identity digest is invalid.");
            }

            return model;
        }

        public static RequestIdentityDto FromModel(RefreshRequestIdentity model) => new()
        {
            InputPath = model.InputPath,
            RepositoryRoot = model.RepositoryRoot,
            Configuration = model.Configuration,
            TargetFramework = model.TargetFramework,
            GraphSchemaVersion = model.GraphSchemaVersion,
            ExtractorVersion = model.ExtractorVersion,
            CacheSchemaVersion = model.CacheSchemaVersion,
            CanonicalKey = model.CanonicalKey,
            Digest = model.Digest,
        };
    }

    private sealed class GenerationDto
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("event_generation")]
        public long EventGeneration { get; init; }

        [JsonPropertyName("indexed_generation")]
        public long IndexedGeneration { get; init; }

        [JsonPropertyName("published_generation")]
        public long PublishedGeneration { get; init; }

        public RefreshGeneration ToModel()
        {
            if (SessionId is null || !Guid.TryParse(SessionId, out var sessionId))
            {
                throw new InvalidDataException("The incremental cache generation has an invalid session id.");
            }

            return new RefreshGeneration(sessionId, EventGeneration, IndexedGeneration, PublishedGeneration);
        }

        public static GenerationDto FromModel(RefreshGeneration model) => new()
        {
            SessionId = model.SessionId.ToString("D"),
            EventGeneration = model.EventGeneration,
            IndexedGeneration = model.IndexedGeneration,
            PublishedGeneration = model.PublishedGeneration,
        };
    }

    private sealed class ManifestEntryDto
    {
        [JsonPropertyName("fingerprint")]
        public ProjectFingerprintDto? Fingerprint { get; init; }

        [JsonPropertyName("contribution_key")]
        public string? ContributionKey { get; init; }

        [JsonPropertyName("complete")]
        public bool Complete { get; init; }

        public IncrementalManifestEntry ToModel()
        {
            if (Fingerprint is null || ContributionKey is null)
            {
                throw new InvalidDataException("The incremental cache manifest entry is incomplete.");
            }

            return new IncrementalManifestEntry(Fingerprint.ToModel(), ContributionKey, Complete);
        }

        public static ManifestEntryDto FromModel(IncrementalManifestEntry model) => new()
        {
            Fingerprint = ProjectFingerprintDto.FromModel(model.Fingerprint),
            ContributionKey = model.ContributionKey,
            Complete = model.IsComplete,
        };
    }

    private sealed class ContributionDto
    {
        [JsonPropertyName("fingerprint")]
        public ProjectFingerprintDto? Fingerprint { get; init; }

        [JsonPropertyName("contribution_key")]
        public string? ContributionKey { get; init; }

        [JsonPropertyName("complete")]
        public bool Complete { get; init; }

        [JsonPropertyName("diagnostics")]
        public List<string>? Diagnostics { get; init; }

        [JsonPropertyName("nodes")]
        public List<NodeDto>? Nodes { get; init; }

        [JsonPropertyName("edges")]
        public List<EdgeDto>? Edges { get; init; }

        public ProjectContributionEnvelope ToModel()
        {
            if (Fingerprint is null
                || ContributionKey is null
                || Diagnostics is null
                || Nodes is null
                || Edges is null)
            {
                throw new InvalidDataException("The incremental cache contribution is incomplete.");
            }

            var fingerprint = Fingerprint.ToModel();
            var graph = GraphSnapshot.Create(
                Nodes.Select(node => node.ToModel()),
                Edges.Select(edge => edge.ToModel()));
            var contribution = new ProjectContributionEnvelope(fingerprint, graph, Diagnostics, Complete);
            if (!string.Equals(ContributionKey, contribution.ContributionKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The contribution key for project '{contribution.Project.Key}' is invalid.");
            }

            return contribution;
        }

        public static ContributionDto FromModel(ProjectContributionEnvelope model) => new()
        {
            Fingerprint = ProjectFingerprintDto.FromModel(model.Fingerprint),
            ContributionKey = model.ContributionKey,
            Complete = model.IsComplete,
            Diagnostics = model.Diagnostics.ToList(),
            Nodes = model.Graph.Nodes.Select(NodeDto.FromModel).ToList(),
            Edges = model.Graph.Edges.Select(EdgeDto.FromModel).ToList(),
        };
    }

    private sealed class ProjectFingerprintDto
    {
        [JsonPropertyName("project")]
        public ProjectIdentityDto? Project { get; init; }

        [JsonPropertyName("project_file")]
        public SourceFingerprintDto? ProjectFile { get; init; }

        [JsonPropertyName("source_files")]
        public List<SourceFingerprintDto>? SourceFiles { get; init; }

        [JsonPropertyName("dependency_files")]
        public List<SourceFingerprintDto>? DependencyFiles { get; init; }

        [JsonPropertyName("dependency_discovery_complete")]
        public bool? DependencyDiscoveryComplete { get; init; }

        [JsonPropertyName("compilation_options_key")]
        public string? CompilationOptionsKey { get; init; }

        [JsonPropertyName("project_references")]
        public List<string>? ProjectReferences { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        public ProjectFingerprint ToModel()
        {
            if (Project is null
                || SourceFiles is null
                || DependencyFiles is null
                || DependencyDiscoveryComplete is null
                || CompilationOptionsKey is null
                || ProjectReferences is null
                || Digest is null)
            {
                throw new InvalidDataException("The incremental cache project fingerprint is incomplete.");
            }

            var model = new ProjectFingerprint(
                Project.ToModel(),
                ProjectFile?.ToModel(),
                SourceFiles.Select(source => source.ToModel()),
                ProjectReferences,
                DependencyFiles.Select(dependency => dependency.ToModel()),
                DependencyDiscoveryComplete.Value,
                CompilationOptionsKey);
            if (!string.Equals(Digest, model.Digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The fingerprint for project '{model.Project.Key}' is invalid.");
            }

            return model;
        }

        public static ProjectFingerprintDto FromModel(ProjectFingerprint model) => new()
        {
            Project = ProjectIdentityDto.FromModel(model.Project),
            ProjectFile = model.ProjectFile is null ? null : SourceFingerprintDto.FromModel(model.ProjectFile),
            SourceFiles = model.SourceFiles.Select(SourceFingerprintDto.FromModel).ToList(),
            DependencyFiles = model.DependencyFiles.Select(SourceFingerprintDto.FromModel).ToList(),
            DependencyDiscoveryComplete = model.DependencyDiscoveryComplete,
            CompilationOptionsKey = model.CompilationOptionsKey,
            ProjectReferences = model.ProjectReferenceKeys.ToList(),
            Digest = model.Digest,
        };
    }

    private sealed class ProjectIdentityDto
    {
        [JsonPropertyName("relative_path")]
        public string? RelativePath { get; init; }

        [JsonPropertyName("target_framework")]
        public string? TargetFramework { get; init; }

        public ProjectIdentity ToModel()
        {
            if (RelativePath is null || TargetFramework is null)
            {
                throw new InvalidDataException("The incremental cache project identity is incomplete.");
            }

            return new ProjectIdentity(RelativePath, TargetFramework);
        }

        public static ProjectIdentityDto FromModel(ProjectIdentity model) => new()
        {
            RelativePath = model.RelativePath,
            TargetFramework = model.TargetFramework,
        };
    }

    private sealed class SourceFingerprintDto
    {
        [JsonPropertyName("relative_path")]
        public string? RelativePath { get; init; }

        [JsonPropertyName("exists")]
        public bool Exists { get; init; }

        [JsonPropertyName("length")]
        public long Length { get; init; }

        [JsonPropertyName("last_write_time_utc_ticks")]
        public long LastWriteTimeUtcTicks { get; init; }

        [JsonPropertyName("content_sha256")]
        public string? ContentSha256 { get; init; }

        public SourceFingerprint ToModel()
        {
            if (RelativePath is null)
            {
                throw new InvalidDataException("The incremental cache source fingerprint has no path.");
            }

            return new SourceFingerprint(RelativePath, Exists, Length, LastWriteTimeUtcTicks, ContentSha256);
        }

        public static SourceFingerprintDto FromModel(SourceFingerprint model) => new()
        {
            RelativePath = model.RelativePath,
            Exists = model.Exists,
            Length = model.Length,
            LastWriteTimeUtcTicks = model.LastWriteTimeUtcTicks,
            ContentSha256 = model.ContentSha256,
        };
    }

    private sealed class NodeDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("symbol_key")]
        public string? SymbolKey { get; init; }

        [JsonPropertyName("source_locations")]
        public List<SourceLocationDto>? SourceLocations { get; init; }

        [JsonPropertyName("properties")]
        public List<PropertyDto>? Properties { get; init; }

        public GraphNode ToModel()
        {
            if (Id is null || Kind is null || Label is null || SymbolKey is null || SourceLocations is null || Properties is null)
            {
                throw new InvalidDataException("The incremental cache node is incomplete.");
            }

            if (!Enum.TryParse<GraphNodeKind>(Kind, ignoreCase: false, out var kind)
                || !Enum.IsDefined(kind))
            {
                throw new InvalidDataException($"The incremental cache contains unknown node kind '{Kind}'.");
            }

            return new GraphNode(
                Id,
                kind,
                Label,
                SymbolKey,
                SourceLocations.Select(location => location.ToModel()),
                Properties.Select(property => property.ToModel()));
        }

        public static NodeDto FromModel(GraphNode model) => new()
        {
            Id = model.Id,
            Kind = model.Kind.ToString(),
            Label = model.Label,
            SymbolKey = model.SymbolKey,
            SourceLocations = model.SourceLocations.Select(SourceLocationDto.FromModel).ToList(),
            Properties = model.Properties
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(PropertyDto.FromModel)
                .ToList(),
        };
    }

    private sealed class EdgeDto
    {
        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("target")]
        public string? Target { get; init; }

        [JsonPropertyName("relation")]
        public string? Relation { get; init; }

        [JsonPropertyName("evidence")]
        public string? Evidence { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("source_locations")]
        public List<SourceLocationDto>? SourceLocations { get; init; }

        public GraphEdge ToModel()
        {
            if (Source is null || Target is null || Relation is null || Evidence is null || SourceLocations is null)
            {
                throw new InvalidDataException("The incremental cache edge is incomplete.");
            }

            if (!Enum.TryParse<GraphRelation>(Relation, ignoreCase: false, out var relation)
                || !Enum.IsDefined(relation))
            {
                throw new InvalidDataException($"The incremental cache contains unknown graph relation '{Relation}'.");
            }

            if (!Enum.TryParse<EvidenceKind>(Evidence, ignoreCase: false, out var evidence)
                || !Enum.IsDefined(evidence))
            {
                throw new InvalidDataException($"The incremental cache contains unknown evidence kind '{Evidence}'.");
            }

            return new GraphEdge(
                Source,
                Target,
                relation,
                evidence,
                Confidence,
                SourceLocations.Select(location => location.ToModel()));
        }

        public static EdgeDto FromModel(GraphEdge model) => new()
        {
            Source = model.SourceId,
            Target = model.TargetId,
            Relation = model.Relation.ToString(),
            Evidence = model.Evidence.ToString(),
            Confidence = model.Confidence,
            SourceLocations = model.SourceLocations.Select(SourceLocationDto.FromModel).ToList(),
        };
    }

    private sealed class SourceLocationDto
    {
        [JsonPropertyName("file")]
        public string? File { get; init; }

        [JsonPropertyName("line")]
        public int Line { get; init; }

        [JsonPropertyName("column")]
        public int Column { get; init; }

        public SourceLocation ToModel()
        {
            if (File is null)
            {
                throw new InvalidDataException("The incremental cache source location has no file.");
            }

            return new SourceLocation(File, Line, Column);
        }

        public static SourceLocationDto FromModel(SourceLocation model) => new()
        {
            File = model.FilePath,
            Line = model.Line,
            Column = model.Column,
        };
    }

    private sealed class PropertyDto
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }

        public KeyValuePair<string, string> ToModel()
        {
            if (Key is null || Value is null)
            {
                throw new InvalidDataException("The incremental cache node property is incomplete.");
            }

            return new KeyValuePair<string, string>(Key, Value);
        }

        public static PropertyDto FromModel(KeyValuePair<string, string> model) => new()
        {
            Key = model.Key,
            Value = model.Value,
        };
    }
}
