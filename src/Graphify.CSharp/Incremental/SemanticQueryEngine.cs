using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Graphify.CSharp.Domain;
using Graphify.CSharp.Roslyn;
using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Incremental;

internal sealed class SemanticQueryEngine
{
    private const int MaximumDisplayedDiagnostics = 100;
    private const int MaximumDiagnosticCharacters = 4096;
    private readonly int _maximumResponseBytes;
    private readonly SemanticQueryExecutionMetrics? _metrics;

    public SemanticQueryEngine(
        int maximumResponseBytes = SemanticQueryProtocol.MaximumResponseBytes,
        SemanticQueryExecutionMetrics? metrics = null)
    {
        if (maximumResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseBytes),
                "The semantic response limit must be positive.");
        }

        _maximumResponseBytes = maximumResponseBytes;
        _metrics = metrics;
    }

    public SemanticQueryResponse Execute(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(view);
        SemanticQueryJsonParser.ValidateSpec(specification);
        ValidateSnapshotAndCursor(specification, view);

        return specification.Command switch
        {
            "symbols" => ExecuteSymbols(specification, view, cancellationToken),
            "signature" => ExecuteSignature(specification, view, cancellationToken),
            "usages" => ExecuteUsages(specification, view, callsOnly: false, cancellationToken),
            "callers" => ExecuteUsages(specification, view, callsOnly: true, cancellationToken),
            "hierarchy" => ExecuteHierarchy(specification, view, cancellationToken),
            "usage-summary" => ExecuteUsageSummary(specification, view, cancellationToken),
            "arguments" => ExecuteArguments(specification, view, cancellationToken),
            _ => throw new SemanticQueryException(
                "unsupported_capability",
                $"The semantic command '{specification.Command}' is not available in this query operation."),
        };
    }

    private SemanticQueryResponse ExecuteSymbols(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        CancellationToken cancellationToken)
    {
        var rows = view.Index.GetDeclarationsByDisplayName(cancellationToken)
            .Where(declaration => declaration.Node.Kind != GraphNodeKind.ExternalRoot)
            .Where(declaration =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return MatchesFilter(declaration, specification.EffectiveFilters, view);
            })
            .Where(declaration => specification.Search is null
                || declaration.Identity.DisplayName.Contains(specification.Search, StringComparison.OrdinalIgnoreCase))
            .Select(declaration => CreateRow(
                $"{declaration.Identity.DisplayName}\u001F{declaration.Node.Id}",
                () => SemanticQueryJson.ToElement(ToSymbol(declaration, view))));
        return CreatePagedResponse(specification, view, rows, cancellationToken);
    }

    private SemanticQueryResponse ExecuteSignature(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var declaration = FindExactDeclaration(specification, view);
        var item = SemanticQueryJson.ToElement(ToSignature(declaration, view, cancellationToken));
        return CreateSingleResponse(specification, view, item);
    }

    private SemanticQueryResponse ExecuteUsages(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        bool callsOnly,
        CancellationToken cancellationToken)
    {
        var target = FindExactDeclaration(specification, view);
        var rows = new List<SemanticQueryRow>();
        foreach (var edge in view.Index.Incoming.GetValueOrDefault(target.Node.Id, ImmutableArray<GraphEdge>.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (callsOnly && edge.Relation != GraphRelation.Calls)
            {
                continue;
            }

            if (!view.Index.DeclarationsById.TryGetValue(edge.SourceId, out var origin)
                || !MatchesFilter(origin, specification.EffectiveFilters, view))
            {
                continue;
            }

            AddEdgeRows(rows, edge, origin, target);
        }

        rows.Sort(SemanticQueryRowComparer.Instance);
        return CreatePagedResponse(specification, view, rows, cancellationToken);
    }

    private SemanticQueryResponse ExecuteHierarchy(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        CancellationToken cancellationToken)
    {
        var target = FindExactDeclaration(specification, view);
        var direction = specification.Direction ?? "all";
        var rows = new List<SemanticQueryRow>();
        if (direction is "base" or "all")
        {
            AddHierarchyEdges(
                rows,
                view,
                target,
                view.Index.Outgoing.GetValueOrDefault(target.Node.Id, []),
                "base",
                outgoing: true,
                [GraphRelation.Inherits],
                specification.EffectiveFilters,
                cancellationToken);
        }

        if (direction is "derived" or "all")
        {
            AddHierarchyEdges(
                rows,
                view,
                target,
                view.Index.Incoming.GetValueOrDefault(target.Node.Id, []),
                "derived",
                outgoing: false,
                [GraphRelation.Inherits],
                specification.EffectiveFilters,
                cancellationToken);
        }

        if (direction is "contracts" or "all")
        {
            AddHierarchyEdges(
                rows,
                view,
                target,
                view.Index.Outgoing.GetValueOrDefault(target.Node.Id, [])
                    .Where(edge => edge.Relation is GraphRelation.Implements or GraphRelation.Overrides),
                "contracts",
                outgoing: true,
                [GraphRelation.Implements, GraphRelation.Overrides],
                specification.EffectiveFilters,
                cancellationToken);
        }

        if (direction is "implementations" or "all")
        {
            AddHierarchyEdges(
                rows,
                view,
                target,
                view.Index.Incoming.GetValueOrDefault(target.Node.Id, [])
                    .Where(edge => edge.Relation is GraphRelation.Implements or GraphRelation.Overrides),
                "implementations",
                outgoing: false,
                [GraphRelation.Implements, GraphRelation.Overrides],
                specification.EffectiveFilters,
                cancellationToken);
        }

        rows = rows
            .GroupBy(row => row.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
        return CreatePagedResponse(specification, view, rows, cancellationToken);
    }

    private SemanticQueryResponse ExecuteUsageSummary(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        CancellationToken cancellationToken)
    {
        var summaries = GetSummaries(view.Index, specification.EffectiveGroupBy, cancellationToken);
        var cursor = SemanticQueryCursor.Read(specification.Cursor, view, specification);
        var rows = EnumerateSummaryRows(
            specification,
            view,
            summaries,
            cursor?.LastKey,
            cancellationToken);
        return CreatePagedResponse(specification, view, rows, cancellationToken);
    }

    private IEnumerable<SemanticQueryRow> EnumerateSummaryRows(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>> summaries,
        string? afterKey,
        CancellationToken cancellationToken)
    {
        var grouped = specification.EffectiveGroupBy.Count > 0;
        var declarations = view.Index.Declarations;
        var startIndex = SummaryStartIndex(declarations, afterKey, grouped);
        for (var index = startIndex; index < declarations.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = declarations[index];
            if (target.Node.Kind == GraphNodeKind.ExternalRoot
                || !MatchesFilter(target, specification.EffectiveFilters, view)
                || (specification.Search is not null
                    && !target.Identity.DisplayName.Contains(
                        specification.Search,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!grouped)
            {
                yield return CreateRow(
                    target.Node.Id,
                    () => SemanticQueryJson.ToElement(
                        ToSummary(target, summaries.GetValueOrDefault(target.Node.Id, []).FirstOrDefault(), view)));
                continue;
            }

            var groups = summaries.GetValueOrDefault(target.Node.Id, []);
            if (groups.Length == 0)
            {
                yield return CreateRow(
                    target.Node.Id + "\u001F<none>",
                    () => SemanticQueryJson.ToElement(
                        ToSummary(
                            target,
                            SemanticSummaryAggregate.Create(
                                Array.Empty<GraphEdge>(),
                                view.Index.DeclarationsById,
                                group: null),
                            view)));
                continue;
            }

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateRow(
                    string.Join(
                        '\u001F',
                        target.Node.Id,
                        group.OriginProject ?? string.Empty,
                        group.OriginNamespace ?? string.Empty,
                        group.OriginTargetFramework ?? string.Empty,
                        group.IsExternalRoot ? "1" : "0"),
                    () => SemanticQueryJson.ToElement(ToSummary(target, group, view)));
            }
        }
    }

    private static int SummaryStartIndex(
        ImmutableArray<SymbolDeclaration> declarations,
        string? afterKey,
        bool grouped)
    {
        if (afterKey is null)
        {
            return 0;
        }

        var separator = afterKey.IndexOf('\u001F');
        var targetId = grouped && separator >= 0
            ? afterKey[..separator]
            : afterKey;
        var low = 0;
        var high = declarations.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (string.CompareOrdinal(declarations[middle].Node.Id, targetId) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (!grouped
            && low < declarations.Length
            && string.Equals(declarations[low].Node.Id, targetId, StringComparison.Ordinal))
        {
            return low + 1;
        }

        return low;
    }

    private SemanticQueryResponse ExecuteArguments(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        CancellationToken cancellationToken)
    {
        var declaration = FindExactDeclaration(specification, view);
        if (declaration.Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor
            })
        {
            throw new SemanticQueryException(
                "unsupported_symbol",
                "Arguments requires a source method or constructor declaration.");
        }

        var cursor = SemanticQueryCursor.Read(specification.Cursor, view, specification);
        var extraction = SemanticArgumentExtractor
            .Extract(view, declaration, specification.EffectiveFilters, cursor?.LastKey, cancellationToken);
        var rows = extraction.Results
            .Select(row => CreateRow(row.SortKey, () => SemanticQueryJson.ToElement(row.Value)))
            .ToList();
        return CreatePagedResponse(
            specification,
            view,
            rows,
            cancellationToken,
            extraction.Diagnostics);
    }

    private void AddHierarchyEdges(
        ICollection<SemanticQueryRow> rows,
        SemanticEvidenceView view,
        SymbolDeclaration target,
        IEnumerable<GraphEdge> edges,
        string direction,
        bool outgoing,
        IReadOnlyList<GraphRelation> allowedRelations,
        SemanticQueryFilters filters,
        CancellationToken cancellationToken)
    {
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowedRelations.Contains(edge.Relation))
            {
                continue;
            }

            var neighborId = outgoing ? edge.TargetId : edge.SourceId;
            if (!view.Index.DeclarationsById.TryGetValue(neighborId, out var neighbor))
            {
                continue;
            }

            if (!MatchesFilter(neighbor, filters, view))
            {
                continue;
            }

            // Hierarchy is an adjacency query, not an occurrence query. An
            // edge may have several source locations, but it must produce one
            // deterministic neighbor row. Retain the first location only as
            // the representative location; usages and summaries preserve the
            // full occurrence information where that distinction matters.
            var location = LocationsOrNull(edge).FirstOrDefault();
            var item = new SemanticHierarchyItem(
                neighbor.Node.Id,
                ToSymbol(neighbor, view),
                edge.Relation.ToString().ToLowerInvariant(),
                direction,
                edge.Evidence.ToString().ToLowerInvariant(),
                edge.Confidence,
                ToLocation(location));
            rows.Add(CreateRow(
                string.Join(
                    '\u001F',
                    edge.Relation,
                    direction,
                    neighbor.Node.Id,
                    edge.Evidence),
                () => SemanticQueryJson.ToElement(item)));
        }
    }

    private void AddEdgeRows(
        ICollection<SemanticQueryRow> rows,
        GraphEdge edge,
        SymbolDeclaration origin,
        SymbolDeclaration target)
    {
        foreach (var location in LocationsOrNull(edge))
        {
            var item = new SemanticUsageItem(
                origin.Node.Id,
                target.Node.Id,
                edge.Relation.ToString().ToLowerInvariant(),
                edge.Evidence.ToString().ToLowerInvariant(),
                edge.Confidence,
                ToLocation(location),
                ToSymbol(origin, null),
                ToSymbol(target, null));
            rows.Add(CreateRow(
                string.Join(
                    '\u001F',
                    origin.Node.Id,
                    edge.Relation,
                    edge.Evidence,
                    LocationKey(location)),
                () => SemanticQueryJson.ToElement(item)));
        }
    }

    private SemanticQueryResponse CreateSingleResponse(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        JsonElement item)
    {
        var diagnostics = DisplayDiagnostics(view.Diagnostics, out var truncated);
        var response = SemanticQueryResponse.SuccessResponse(
            view.SessionId == Guid.Empty ? null : view.SessionId,
            view.Mode,
            specification.Command,
            CreateSnapshot(view),
            CreateScope(specification, view, Array.Empty<string>()),
            [item],
            new SemanticQueryPage(1, false, null),
            diagnostics,
            truncated);
        return SerializedSize(response) <= _maximumResponseBytes
            ? response
            : SemanticQueryResponse.Failure(
                view.SessionId == Guid.Empty ? null : view.SessionId,
                view.Mode,
                specification.Command,
                "response_too_large",
                "The semantic response exceeds the 4 MiB response limit.",
                view.AnalysisKey);
    }

    private SemanticQueryResponse CreatePagedResponse(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        IEnumerable<SemanticQueryRow> rows,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? operationDiagnostics = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        operationDiagnostics ??= Array.Empty<string>();
        var cursor = SemanticQueryCursor.Read(specification.Cursor, view, specification);
        var pageCandidates = rows
            .Where(row => cursor is null || string.CompareOrdinal(row.Key, cursor.LastKey) > 0)
            .Take(specification.Limit + 1)
            .ToArray();
        var pageRows = pageCandidates
            .Take(specification.Limit)
            .ToArray();
        var allDiagnostics = view.Diagnostics.Concat(operationDiagnostics).ToArray();
        var diagnostics = DisplayDiagnostics(allDiagnostics, out var diagnosticsTruncated);
        var scope = CreateScope(specification, view, operationDiagnostics);
        while (pageRows.Length > 0)
        {
            var pageHasMore = pageRows.Length < pageCandidates.Length;
            var candidateCursor = pageHasMore && view.Mode == "instance"
                ? SemanticQueryCursor.Create(view, specification, pageRows[^1].Key)
                : null;
            var candidate = SemanticQueryResponse.SuccessResponse(
                view.SessionId == Guid.Empty ? null : view.SessionId,
                view.Mode,
                specification.Command,
                CreateSnapshot(view),
                scope,
                pageRows.Select(row => row.Value).ToArray(),
                new SemanticQueryPage(
                    pageRows.Length,
                    pageHasMore,
                    candidateCursor,
                    pageHasMore && view.Mode == "cold"),
                diagnostics,
                diagnosticsTruncated);
            if (SerializedSize(candidate) <= _maximumResponseBytes)
            {
                break;
            }

            pageRows = pageRows[..^1];
        }

        if (pageRows.Length == 0 && pageCandidates.Length > 0)
        {
            return SemanticQueryResponse.Failure(
                view.SessionId == Guid.Empty ? null : view.SessionId,
                view.Mode,
                specification.Command,
                "response_too_large",
                "The first semantic result row exceeds the 4 MiB response limit.");
        }

        // A response may have been shortened by the byte budget. Continue
        // from the last returned row rather than silently dropping the rows
        // that were candidates but did not fit in this page.
        var hasMore = pageRows.Length < pageCandidates.Length;
        var nextCursor = hasMore && pageRows.Length > 0 && view.Mode == "instance"
            ? SemanticQueryCursor.Create(view, specification, pageRows[^1].Key)
            : null;
        var continuationUnavailable = hasMore && view.Mode == "cold";
        var response = SemanticQueryResponse.SuccessResponse(
            view.SessionId == Guid.Empty ? null : view.SessionId,
            view.Mode,
            specification.Command,
            CreateSnapshot(view),
            scope,
            pageRows.Select(row => row.Value).ToArray(),
            new SemanticQueryPage(pageRows.Length, hasMore, nextCursor, continuationUnavailable),
            diagnostics,
            diagnosticsTruncated);
        return SerializedSize(response) <= _maximumResponseBytes
            ? response
            : SemanticQueryResponse.Failure(
                view.SessionId == Guid.Empty ? null : view.SessionId,
                view.Mode,
                specification.Command,
                "response_too_large",
                "The semantic response exceeds the 4 MiB response limit.");
    }

    private static SymbolDeclaration FindExactDeclaration(
        SemanticQuerySpec specification,
        SemanticEvidenceView view)
    {
        if (specification.SymbolId is null
            || !view.Index.DeclarationsById.TryGetValue(specification.SymbolId, out var declaration))
        {
            throw new SemanticQueryException(
                "symbol_not_found",
                $"No source declaration was found for symbol '{specification.SymbolId ?? string.Empty}'.");
        }

        return declaration;
    }

    private static void ValidateSnapshotAndCursor(
        SemanticQuerySpec specification,
        SemanticEvidenceView view)
    {
        if (specification.SnapshotId is not null
            && !string.Equals(specification.SnapshotId, view.SnapshotId, StringComparison.Ordinal))
        {
            throw new SemanticQueryException(
                "stale_snapshot",
                "The requested snapshot is no longer the current semantic evidence revision.");
        }

        if (specification.Cursor is not null && view.Mode == "cold")
        {
            throw new SemanticQueryException(
                "invalid_cursor",
                "Cold queries do not retain a continuation cursor; start a watcher for paging.");
        }

        if (specification.Cursor is not null)
        {
            _ = SemanticQueryCursor.Read(specification.Cursor, view, specification);
        }
    }

    private static SemanticQueryScope CreateScope(
        SemanticQuerySpec specification,
        SemanticEvidenceView view,
        IReadOnlyList<string> operationDiagnostics)
    {
        var diagnostics = view.Diagnostics.Concat(operationDiagnostics).ToArray();
        var displayedOperationDiagnostics = DisplayDiagnostics(
                operationDiagnostics,
                out _)
            .Select(diagnostic => diagnostic.Message)
            .ToArray();
        return new SemanticQueryScope(
            view.AnalysisKey,
            specification.EffectiveFilters,
            "observed_static",
            view.InputSnapshot.InputDiscoveryComplete,
            diagnostics.Length > 0,
            diagnostics.Length == 0,
            displayedOperationDiagnostics);
    }

    private static SemanticQuerySnapshot CreateSnapshot(SemanticEvidenceView view) =>
        new(view.SnapshotId, view.TargetEventGeneration, view.Generation.IndexedGeneration, view.Generation.EventGeneration);

    private static int SerializedSize(SemanticQueryResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, SemanticQueryJson.SerializerOptions).Length;

    internal static IReadOnlyList<SemanticQueryDiagnostic> DisplayDiagnostics(
        IEnumerable<string> diagnostics,
        out bool truncated)
    {
        var values = diagnostics.ToArray();
        truncated = values.Length > MaximumDisplayedDiagnostics
            || values.Any(diagnostic => diagnostic.Length > MaximumDiagnosticCharacters);
        return values
            .Take(MaximumDisplayedDiagnostics)
            .Select(diagnostic => new SemanticQueryDiagnostic(
                "semantic",
                diagnostic.Length <= MaximumDiagnosticCharacters
                    ? diagnostic
                    : diagnostic[..MaximumDiagnosticCharacters] + "…"))
            .ToArray();
    }

    private static bool MatchesFilter(
        SymbolDeclaration declaration,
        SemanticQueryFilters filters,
        SemanticEvidenceView view)
    {
        if (filters.Path is not null && !MatchesPath(declaration, filters.Path, view.Solution.RepositoryRoot))
        {
            return false;
        }

        if (filters.Project is not null
            && !string.Equals(
                CanonicalProjectPath(declaration.Identity.Project.RelativePath, view.Solution.RepositoryRoot),
                IncrementalPaths.CanonicalAbsolutePath(filters.Project),
                IncrementalPaths.PathComparison))
        {
            return false;
        }

        var namespaceName = declaration.Identity.Namespace;
        if (filters.Namespace is not null
            && (filters.Namespace.Length == 0
                ? namespaceName.Length != 0
                : !(string.Equals(namespaceName, filters.Namespace, StringComparison.Ordinal)
                    || namespaceName.StartsWith(filters.Namespace + ".", StringComparison.Ordinal))))
        {
            return false;
        }

        if (filters.Kind is null)
        {
            return true;
        }

        var actualKind = GetDeclarationKind(declaration);
        return string.Equals(actualKind, filters.Kind, StringComparison.Ordinal)
            || filters.Kind == "userdefinedoperator" && actualKind == "operator";
    }

    private static bool MatchesPath(
        SymbolDeclaration declaration,
        string path,
        string repositoryRoot)
    {
        var canonicalFilter = IncrementalPaths.CanonicalAbsolutePath(path);
        var isDirectory = Directory.Exists(canonicalFilter);
        return declaration.Node.SourceLocations.Any(location =>
        {
            var absolute = IncrementalPaths.CanonicalAbsolutePath(Path.Combine(repositoryRoot, location.FilePath));
            return isDirectory
                ? IncrementalPaths.IsPathOrUnder(absolute, canonicalFilter)
                : string.Equals(absolute, canonicalFilter, IncrementalPaths.PathComparison);
        });
    }

    private static string CanonicalProjectPath(string relativePath, string repositoryRoot) =>
        IncrementalPaths.CanonicalAbsolutePath(
            Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string GetDeclarationKind(SymbolDeclaration declaration)
    {
        var rawKind = declaration.Node.Properties.GetValueOrDefault("declaration_kind")
            ?? declaration.Node.Kind.ToString().ToLowerInvariant();
        return rawKind switch
        {
            "ordinary" => "method",
            "staticconstructor" => "static_constructor",
            "userdefinedoperator" => "operator",
            _ when declaration.Node.Kind == GraphNodeKind.Method => "method",
            _ when declaration.Node.Kind == GraphNodeKind.Constructor => "constructor",
            _ => rawKind,
        };
    }

    private static SemanticSymbolItem ToSymbol(
        SymbolDeclaration declaration,
        SemanticEvidenceView? view)
    {
        var location = declaration.Node.SourceLocations.FirstOrDefault();
        return new SemanticSymbolItem(
            declaration.Node.Id,
            declaration.Identity.DisplayName,
            GetDeclarationKind(declaration),
            declaration.Identity.Namespace,
            declaration.Identity.Project.RelativePath,
            declaration.Identity.Project.TargetFramework,
            ToLocation(location),
            declaration.Node.Properties.GetValueOrDefault("is_entry_point") == "true");
    }

    private static SemanticSignatureItem ToSignature(
        SymbolDeclaration declaration,
        SemanticEvidenceView view,
        CancellationToken cancellationToken)
    {
        var symbol = declaration.Symbol;
        var parameters = symbol switch
        {
            IMethodSymbol method => method.Parameters.Select(ToParameter).ToArray(),
            IPropertySymbol property => property.Parameters.Select(ToParameter).ToArray(),
            _ => Array.Empty<SemanticParameterItem>(),
        };
        var typeParameters = symbol switch
        {
            INamedTypeSymbol type => type.TypeParameters.Select(ToTypeParameter).ToArray(),
            IMethodSymbol method => method.TypeParameters.Select(ToTypeParameter).ToArray(),
            _ => Array.Empty<SemanticTypeParameterItem>(),
        };
        var returnType = symbol switch
        {
            IMethodSymbol method when method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor)
                => DisplayType(method.ReturnType),
            IPropertySymbol property => DisplayType(property.Type),
            IFieldSymbol field => DisplayType(field.Type),
            IEventSymbol @event => DisplayType(@event.Type),
            _ => null,
        };
        var containing = symbol.ContainingSymbol?.ToDisplayString(SemanticTypeDisplay.Format);
        var modifiers = GetModifiers(symbol);
        cancellationToken.ThrowIfCancellationRequested();
        return new SemanticSignatureItem(
            ToSymbol(declaration, view),
            symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            modifiers,
            containing,
            symbol is INamedTypeSymbol namedType ? DisplayType(namedType) : null,
            returnType,
            parameters,
            typeParameters,
            symbol is ITypeSymbol nullableType
                ? nullableType.NullableAnnotation.ToString().ToLowerInvariant()
                : "none");
    }

    private static SemanticParameterItem ToParameter(IParameterSymbol parameter) =>
        new(
            parameter.Ordinal,
            parameter.Name,
            DisplayType(parameter.Type),
            parameter.RefKind.ToString().ToLowerInvariant(),
            parameter.IsParams,
            parameter.IsOptional,
            parameter.HasExplicitDefaultValue,
            DefaultValue(parameter),
            parameter.NullableAnnotation.ToString().ToLowerInvariant());

    private static SemanticTypeParameterItem ToTypeParameter(ITypeParameterSymbol parameter) =>
        new(
            parameter.Ordinal,
            parameter.Name,
            parameter.HasReferenceTypeConstraint,
            parameter.HasValueTypeConstraint,
            parameter.HasNotNullConstraint,
            parameter.HasUnmanagedTypeConstraint,
            parameter.HasConstructorConstraint,
            parameter.ConstraintTypes.Select(DisplayType).OrderBy(value => value, StringComparer.Ordinal).ToArray());

    private static JsonElement? DefaultValue(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
        {
            return null;
        }

        var value = parameter.ExplicitDefaultValue;
        if (value is null)
        {
            return SemanticQueryJson.ToElement<object?>(null);
        }

        return value switch
        {
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or string or char
                => SemanticQueryJson.ToElement(value),
            _ => SemanticQueryJson.ToElement(value.ToString()),
        };
    }

    private static string DisplayType(ITypeSymbol type) =>
        SemanticTypeDisplay.FormatType(type);

    private static IReadOnlyList<string> GetModifiers(ISymbol symbol)
    {
        var modifiers = new List<string>();
        switch (symbol)
        {
            case IMethodSymbol method:
                Add(method.IsStatic, "static");
                Add(method.IsAbstract, "abstract");
                Add(method.IsVirtual, "virtual");
                Add(method.IsOverride, "override");
                Add(method.IsSealed, "sealed");
                Add(method.IsAsync, "async");
                Add(method.IsExtern, "extern");
                Add(method.IsReadOnly, "readonly");
                break;
            case IPropertySymbol property:
                Add(property.IsStatic, "static");
                Add(property.IsAbstract, "abstract");
                Add(property.IsVirtual, "virtual");
                Add(property.IsOverride, "override");
                Add(property.IsSealed, "sealed");
                Add(property.IsReadOnly, "readonly");
                break;
            case IFieldSymbol field:
                Add(field.IsStatic, "static");
                Add(field.IsReadOnly, "readonly");
                Add(field.IsConst, "const");
                Add(field.IsVolatile, "volatile");
                break;
            case IEventSymbol @event:
                Add(@event.IsStatic, "static");
                Add(@event.IsAbstract, "abstract");
                Add(@event.IsVirtual, "virtual");
                Add(@event.IsOverride, "override");
                Add(@event.IsSealed, "sealed");
                break;
            case INamedTypeSymbol type:
                Add(type.IsStatic, "static");
                Add(type.IsAbstract, "abstract");
                Add(type.IsSealed, "sealed");
                break;
        }

        return modifiers;

        void Add(bool condition, string value)
        {
            if (condition)
            {
                modifiers.Add(value);
            }
        }
    }

    private static SemanticSummaryItem ToSummary(
        SymbolDeclaration target,
        SemanticSummaryAggregate? aggregate,
        SemanticEvidenceView view)
    {
        aggregate ??= SemanticSummaryAggregate.Create(
            Array.Empty<GraphEdge>(),
            view.Index.DeclarationsById,
            group: null);
        return new SemanticSummaryItem(
            ToSymbol(target, view),
            aggregate.Calls,
            aggregate.References,
            aggregate.Inherits,
            aggregate.Implements,
            aggregate.Overrides,
            aggregate.DistinctOriginCount,
            aggregate.OriginProject,
            aggregate.OriginNamespace,
            aggregate.OriginTargetFramework,
            aggregate.IsExternalRoot);
    }

    private static ImmutableDictionary<string, ImmutableArray<SemanticSummaryAggregate>> GetSummaries(
        SemanticEvidenceIndex index,
        IReadOnlyList<string> groupBy,
        CancellationToken cancellationToken)
    {
        var grouping = groupBy.Count switch
        {
            0 => SummaryGrouping.None,
            1 when groupBy[0] == "project" => SummaryGrouping.Project,
            1 when groupBy[0] == "namespace" => SummaryGrouping.Namespace,
            _ => SummaryGrouping.Project | SummaryGrouping.Namespace,
        };

        return index.GetSummaries(grouping, cancellationToken);
    }

    private static IEnumerable<SourceLocation?> LocationsOrNull(GraphEdge edge)
    {
        if (edge.SourceLocations.Length == 0)
        {
            yield return null;
            yield break;
        }

        foreach (var location in edge.SourceLocations)
        {
            yield return location;
        }
    }

    private static SemanticLocation? ToLocation(SourceLocation? location) => location is null
        ? null
        : new SemanticLocation(location.FilePath, location.Line, location.Column);

    private static string LocationKey(SourceLocation? location) => location is null
        ? string.Empty
        : string.Join(
            ':',
            location.FilePath,
            location.Line.ToString(CultureInfo.InvariantCulture),
            location.Column.ToString(CultureInfo.InvariantCulture));

    private SemanticQueryRow CreateRow(string key, Func<JsonElement> valueFactory)
    {
        _metrics?.RecordRowCreated();
        return new SemanticQueryRow(key, valueFactory);
    }

    private sealed class SemanticQueryRow
    {
        private readonly Func<JsonElement> _valueFactory;
        private JsonElement _value;
        private bool _valueCreated;

        public SemanticQueryRow(string key, Func<JsonElement> valueFactory)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            _valueFactory = valueFactory ?? throw new ArgumentNullException(nameof(valueFactory));
        }

        public string Key { get; }

        public JsonElement Value
        {
            get
            {
                if (!_valueCreated)
                {
                    _value = _valueFactory();
                    _valueCreated = true;
                }

                return _value;
            }
        }
    }

    private sealed class SemanticQueryRowComparer : IComparer<SemanticQueryRow>
    {
        public static SemanticQueryRowComparer Instance { get; } = new();

        public int Compare(SemanticQueryRow? x, SemanticQueryRow? y) =>
            string.CompareOrdinal(x?.Key, y?.Key);
    }

}

internal sealed class SemanticQueryExecutionMetrics
{
    private int _rowsCreated;

    public int RowsCreated => Volatile.Read(ref _rowsCreated);

    internal void RecordRowCreated() => Interlocked.Increment(ref _rowsCreated);
}

internal static class SemanticTypeDisplay
{
    public static readonly SymbolDisplayFormat Format =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string FormatType(ITypeSymbol type) => type.ToDisplayString(Format);
}

internal sealed record SemanticLocation(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column);

internal sealed record SemanticSymbolItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("declaration_kind")] string DeclarationKind,
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("target_framework")] string TargetFramework,
    [property: JsonPropertyName("location")] SemanticLocation? Location,
    [property: JsonPropertyName("is_entry_point")] bool IsEntryPoint);

internal sealed record SemanticSignatureItem(
    [property: JsonPropertyName("symbol")] SemanticSymbolItem Symbol,
    [property: JsonPropertyName("accessibility")] string Accessibility,
    [property: JsonPropertyName("modifiers")] IReadOnlyList<string> Modifiers,
    [property: JsonPropertyName("containing")] string? Containing,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("return_type")] string? ReturnType,
    [property: JsonPropertyName("parameters")] IReadOnlyList<SemanticParameterItem> Parameters,
    [property: JsonPropertyName("generic_parameters")] IReadOnlyList<SemanticTypeParameterItem> GenericParameters,
    [property: JsonPropertyName("nullable_annotation")] string NullableAnnotation);

internal sealed record SemanticParameterItem(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ref_kind")] string RefKind,
    [property: JsonPropertyName("is_params")] bool IsParams,
    [property: JsonPropertyName("is_optional")] bool IsOptional,
    [property: JsonPropertyName("has_default")] bool HasDefault,
    [property: JsonPropertyName("default")] JsonElement? Default,
    [property: JsonPropertyName("nullable_annotation")] string NullableAnnotation);

internal sealed record SemanticTypeParameterItem(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("reference_type_constraint")] bool ReferenceTypeConstraint,
    [property: JsonPropertyName("value_type_constraint")] bool ValueTypeConstraint,
    [property: JsonPropertyName("not_null_constraint")] bool NotNullConstraint,
    [property: JsonPropertyName("unmanaged_type_constraint")] bool UnmanagedTypeConstraint,
    [property: JsonPropertyName("constructor_constraint")] bool ConstructorConstraint,
    [property: JsonPropertyName("constraint_types")] IReadOnlyList<string> ConstraintTypes);

internal sealed record SemanticUsageItem(
    [property: JsonPropertyName("origin_id")] string OriginId,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("location")] SemanticLocation? Location,
    [property: JsonPropertyName("origin")] SemanticSymbolItem Origin,
    [property: JsonPropertyName("target")] SemanticSymbolItem Target);

internal sealed record SemanticHierarchyItem(
    [property: JsonPropertyName("neighbor_id")] string NeighborId,
    [property: JsonPropertyName("neighbor")] SemanticSymbolItem Neighbor,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("location")] SemanticLocation? Location);

internal sealed record SemanticRelationCounts(
    [property: JsonPropertyName("edge_count")] int EdgeCount,
    [property: JsonPropertyName("occurrence_count")] int OccurrenceCount);

internal sealed record SemanticSummaryItem(
    [property: JsonPropertyName("target")] SemanticSymbolItem Target,
    [property: JsonPropertyName("calls")] SemanticRelationCounts Calls,
    [property: JsonPropertyName("references")] SemanticRelationCounts References,
    [property: JsonPropertyName("inherits")] SemanticRelationCounts Inherits,
    [property: JsonPropertyName("implements")] SemanticRelationCounts Implements,
    [property: JsonPropertyName("overrides")] SemanticRelationCounts Overrides,
    [property: JsonPropertyName("distinct_origin_count")] int DistinctOriginCount,
    [property: JsonPropertyName("origin_project")] string? OriginProject,
    [property: JsonPropertyName("origin_namespace")] string? OriginNamespace,
    [property: JsonPropertyName("origin_target_framework")] string? OriginTargetFramework,
    [property: JsonPropertyName("is_external_root")] bool IsExternalRoot);
