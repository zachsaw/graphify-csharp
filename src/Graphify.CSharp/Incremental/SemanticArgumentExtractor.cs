using System.Globalization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Graphify.CSharp.Roslyn;
using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Graphify.CSharp.Incremental;

internal static class SemanticArgumentExtractor
{
    public static SemanticArgumentExtraction Extract(
        SemanticEvidenceView view,
        SymbolDeclaration targetDeclaration,
        SemanticQueryFilters filters,
        string? afterKey,
        CancellationToken cancellationToken)
    {
        var candidateCallSites = view.Index.Incoming
            .GetValueOrDefault(targetDeclaration.Node.Id, [])
            .Where(edge => edge.Relation == GraphRelation.Calls)
            .SelectMany(edge => edge.SourceLocations.Select(location => new CandidateCallSite(
                edge.SourceId,
                view.Index.DeclarationsById.TryGetValue(edge.SourceId, out var caller)
                    ? caller.Identity.Project.Key
                    : string.Empty,
                location)))
            .ToHashSet();
        if (candidateCallSites.Count == 0)
        {
            return new SemanticArgumentExtraction(
                Array.Empty<SemanticArgumentExtractionResult>(),
                Array.Empty<string>());
        }

        var candidateFiles = candidateCallSites
            .Select(site => site.Location.FilePath)
            .ToHashSet(StringComparer.Ordinal);
        var scopedCandidateFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in view.Solution.Projects)
        {
            foreach (var document in project.Project.Documents)
            {
                if (document.FilePath is { } filePath
                    && candidateFiles.Contains(RelativePath(filePath, view.Solution.RepositoryRoot))
                    && MatchesDocumentFilter(filePath, project, filters, view.Solution.RepositoryRoot))
                {
                    scopedCandidateFiles.Add(RelativePath(filePath, view.Solution.RepositoryRoot));
                }
            }
        }

        var scopedCandidateCallSites = candidateCallSites
            .Where(site => scopedCandidateFiles.Contains(site.Location.FilePath))
            .Where(site => string.IsNullOrEmpty(site.ProjectKey)
                || !IsBeforeCursor(
                    site.ProjectKey,
                    site.Location.FilePath,
                    afterKey))
            .ToHashSet();
        var results = new List<SemanticArgumentExtractionResult>();
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var representedCallSites = new HashSet<CandidateCallSite>();
        var resolver = new SemanticCallerResolver(view.Catalog);
        var locations = new SourceLocationFactory(view.Solution.RepositoryRoot);
        foreach (var project in view.Solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
        {
            foreach (var document in project.Project.Documents
                .Where(document => document.FilePath is not null)
                .OrderBy(document => document.FilePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.FilePath is not { } filePath
                    || !candidateFiles.Contains(RelativePath(filePath, view.Solution.RepositoryRoot))
                    || IsBeforeCursor(
                        project.Identity.Key,
                        RelativePath(filePath, view.Solution.RepositoryRoot),
                        afterKey)
                    || !MatchesDocumentFilter(filePath, project, filters, view.Solution.RepositoryRoot))
                {
                    continue;
                }

                var root = document.GetSyntaxRootAsync(cancellationToken).GetAwaiter().GetResult();
                if (root is null)
                {
                    continue;
                }

                var model = project.Compilation.GetSemanticModel(root.SyntaxTree);
                var walker = new CallWalker(
                    view,
                    targetDeclaration,
                    resolver,
                    locations,
                    model,
                    results,
                    diagnostics,
                    representedCallSites,
                    cancellationToken);
                try
                {
                    new OperationCallSyntaxWalker(model, walker, cancellationToken).Visit(root);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    diagnostics.Add(
                        $"Arguments: could not bind call-site details in '{RelativePath(filePath, view.Solution.RepositoryRoot)}': {exception.Message}");
                }
            }
        }

        foreach (var candidate in scopedCandidateCallSites
            .Where(candidate => !representedCallSites.Contains(candidate))
            .OrderBy(candidate => candidate.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Location.Line)
            .ThenBy(candidate => candidate.Location.Column)
            .ThenBy(candidate => candidate.CallerId, StringComparer.Ordinal))
        {
            var caller = view.Index.DeclarationsById.TryGetValue(candidate.CallerId, out var declaration)
                ? declaration.Identity.DisplayName
                : candidate.CallerId;
            diagnostics.Add(
                $"Arguments: semantic evidence contains a call to '{targetDeclaration.Identity.DisplayName}' "
                + $"from '{caller}' at {candidate.Location.FilePath}:{candidate.Location.Line}:{candidate.Location.Column}, "
                + "but Roslyn did not expose bindable argument details.");
        }

        return new SemanticArgumentExtraction(
            results
                .OrderBy(result => result.SortKey, StringComparer.Ordinal)
                .ToArray(),
            diagnostics.OrderBy(diagnostic => diagnostic, StringComparer.Ordinal).ToArray());
    }

    private static bool MatchesDocumentFilter(
        string filePath,
        AnalyzedProject project,
        SemanticQueryFilters filters,
        string repositoryRoot)
    {
        if (filters.Project is not null
            && !string.Equals(
                IncrementalPaths.CanonicalAbsolutePath(
                    project.Project.FilePath
                    ?? Path.Combine(
                        repositoryRoot,
                        project.Identity.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                IncrementalPaths.CanonicalAbsolutePath(filters.Project),
                IncrementalPaths.PathComparison))
        {
            return false;
        }

        if (filters.Path is null)
        {
            return true;
        }

        var requested = IncrementalPaths.CanonicalAbsolutePath(filters.Path);
        var actual = IncrementalPaths.CanonicalAbsolutePath(filePath);
        return Directory.Exists(requested)
            ? IncrementalPaths.IsPathOrUnder(actual, requested)
            : string.Equals(actual, requested, IncrementalPaths.PathComparison);
    }

    private static string RelativePath(string filePath, string repositoryRoot) =>
        ProjectIdentity.FromPath(filePath, repositoryRoot).RelativePath;

    private static bool IsBeforeCursor(
        string projectKey,
        string relativeFilePath,
        string? afterKey)
    {
        if (afterKey is null)
        {
            return false;
        }

        var documentPrefix = string.Join(
            '\u001F',
            projectKey,
            relativeFilePath,
            string.Empty);
        // The cursor's last key is inside its boundary document. Revisit that
        // document and let the row-level comparison below seek past the last
        // emitted argument; only documents strictly before it can be skipped.
        return string.CompareOrdinal(documentPrefix, afterKey) < 0
            && !afterKey.StartsWith(documentPrefix, StringComparison.Ordinal);
    }

    private sealed class CallWalker : OperationWalker
    {
        private readonly SemanticEvidenceView _view;
        private readonly SymbolDeclaration _targetDeclaration;
        private readonly SemanticCallerResolver _resolver;
        private readonly SourceLocationFactory _locations;
        private readonly SemanticModel _model;
        private readonly ICollection<SemanticArgumentExtractionResult> _results;
        private readonly ISet<string> _diagnostics;
        private readonly ISet<CandidateCallSite> _representedCallSites;
        private readonly CancellationToken _cancellationToken;

        public CallWalker(
            SemanticEvidenceView view,
            SymbolDeclaration targetDeclaration,
            SemanticCallerResolver resolver,
            SourceLocationFactory locations,
            SemanticModel model,
            ICollection<SemanticArgumentExtractionResult> results,
            ISet<string> diagnostics,
            ISet<CandidateCallSite> representedCallSites,
            CancellationToken cancellationToken)
        {
            _view = view;
            _targetDeclaration = targetDeclaration;
            _resolver = resolver;
            _locations = locations;
            _model = model;
            _results = results;
            _diagnostics = diagnostics;
            _representedCallSites = representedCallSites;
            _cancellationToken = cancellationToken;
        }

        public override void VisitInvocation(IInvocationOperation operation)
        {
            AddCall(operation.TargetMethod, operation.Arguments, operation.Instance, operation.Syntax);
            base.VisitInvocation(operation);
        }

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            if (operation.Constructor is not null)
            {
                AddCall(operation.Constructor, operation.Arguments, receiver: null, operation.Syntax);
            }

            base.VisitObjectCreation(operation);
        }

        private void AddCall(
            IMethodSymbol? method,
            ImmutableArray<IArgumentOperation> arguments,
            IOperation? receiver,
            SyntaxNode syntax)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (method is null || !TargetsSelectedMethod(method))
            {
                return;
            }

            var caller = _resolver.Resolve(_model, syntax.SpanStart);
            if (caller is null)
            {
                return;
            }

            var callSiteId = CreateCallSiteId(caller, syntax);
            var receiverMethod = method.ReducedFrom;
            var receiverOperation = ResolveReceiver(method, receiver, syntax);
            var receiverParameter = ResolveReceiverParameter(method, receiverMethod);
            var ordinalByParameter = new Dictionary<string, int>(StringComparer.Ordinal);
            if (receiverOperation is not null
                && IsExtensionBlockInstanceMember(method)
                && receiverParameter is null)
            {
                AddDiagnostic(
                    $"Arguments: could not bind the receiver parameter for extension member '{method.ToDisplayString()}'.");
            }

            if (receiverOperation is not null && receiverParameter is not null)
            {
                AddArgument(
                    caller,
                    method,
                    syntax,
                    callSiteId,
                    receiverParameter,
                    receiverOperation,
                    "extension_receiver",
                    isImplicit: false,
                    ordinalByParameter);
            }

            if (arguments.Length == 0)
            {
                if (receiverParameter is null)
                {
                    AddNoArguments(caller, method, syntax, callSiteId);
                }

                return;
            }

            foreach (var argument in arguments)
            {
                var parameter = argument.Parameter;
                if (parameter is null)
                {
                    AddDiagnostic(
                        $"Arguments: Roslyn did not provide a formal parameter for an argument to '{method.ToDisplayString()}'.");
                }

                // For an invocation such as Params(0, 1, 2), Roslyn binds the
                // expanded values as one implicit array-creation operation.
                // Unpack its initializer so callers can see the same
                // source-level argument-to-parameter edges they would see for
                // an ordinary parameter. An explicitly supplied array (for
                // example Params(0, new[] { 1, 2 })) remains one argument.
                if (parameter?.IsParams == true
                    && argument.IsImplicit
                    && TryGetExpandedParamsElements(argument.Value) is { } expandedElements)
                {
                    foreach (var element in expandedElements)
                    {
                        AddArgument(
                            caller,
                            method,
                            syntax,
                            callSiteId,
                            parameter,
                            element,
                            "params_expansion",
                            isImplicit: false,
                            ordinalByParameter);
                    }

                    if (expandedElements.Count == 0
                        && arguments.All(candidate => candidate.IsImplicit)
                        && receiverParameter is null)
                    {
                        AddNoArguments(caller, method, syntax, callSiteId);
                    }

                    continue;
                }

                var argumentKind = argument.ArgumentKind.ToString().ToLowerInvariant();
                if (parameter?.IsParams == true
                    && arguments.Count(candidate => candidate.Parameter?.Ordinal == parameter.Ordinal) > 1)
                {
                    argumentKind = "params_expansion";
                }
                AddArgument(
                    caller,
                    method,
                    syntax,
                    callSiteId,
                    parameter,
                    argument.Value,
                    argumentKind,
                    argument.IsImplicit,
                    ordinalByParameter);
            }
        }

        private static IReadOnlyList<IOperation>? TryGetExpandedParamsElements(IOperation value)
        {
            return value switch
            {
                IArrayCreationOperation { Initializer: { } initializer }
                    => initializer.ElementValues,
                ICollectionExpressionOperation collection
                    => collection.Elements,
                _ => null,
            };
        }

        private void AddDiagnostic(string message) => _diagnostics.Add(message);

        private bool TargetsSelectedMethod(IMethodSymbol method)
        {
            foreach (var candidate in MethodCandidates(method))
            {
                if (TryGetDeclaration(candidate) is { } declaration
                    && declaration.Node.Id == _targetDeclaration.Node.Id)
                {
                    return true;
                }
            }

            return false;
        }

        private SymbolDeclaration? TryGetDeclaration(ISymbol symbol)
        {
            if (_view.Catalog.TryGet(symbol, out var declaration))
            {
                return declaration;
            }

            return _view.Catalog.TryGetReference(symbol, out declaration) ? declaration : null;
        }

        private static IEnumerable<IMethodSymbol> MethodCandidates(IMethodSymbol method)
        {
            yield return method;
            if (method.OriginalDefinition is IMethodSymbol original
                && !SymbolEqualityComparer.Default.Equals(original, method))
            {
                yield return original;
            }

            if (method.ReducedFrom is { } reduced)
            {
                yield return reduced;
                if (reduced.OriginalDefinition is IMethodSymbol reducedOriginal
                    && !SymbolEqualityComparer.Default.Equals(reducedOriginal, reduced))
                {
                    yield return reducedOriginal;
                }
            }
        }

        private IOperation? ResolveReceiver(
            IMethodSymbol method,
            IOperation? receiver,
            SyntaxNode syntax)
        {
            if (receiver is not null)
            {
                return receiver;
            }

            // Roslyn represents an instance member in a C# 14 extension block
            // as a method with no ordinary parameters and no ReducedFrom
            // symbol. Recover the receiver expression from the invocation
            // syntax instead of inventing an ordinary argument.
            if (!IsExtensionBlockInstanceMember(method)
                || syntax is not InvocationExpressionSyntax invocation)
            {
                return null;
            }

            var receiverSyntax = invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
                MemberBindingExpressionSyntax memberBinding => memberBinding,
                _ => null,
            };
            return receiverSyntax is null
                ? null
                : _model.GetOperation(receiverSyntax, _cancellationToken);
        }

        private IParameterSymbol? ResolveReceiverParameter(
            IMethodSymbol method,
            IMethodSymbol? reducedMethod)
        {
            if (reducedMethod?.Parameters.FirstOrDefault() is { } reducedParameter)
            {
                return reducedParameter;
            }

            if (!IsExtensionBlockInstanceMember(method)
                || method.ContainingType is not { } extensionBlock)
            {
                return null;
            }

            return _view.Catalog.Declarations
                .Select(declaration => declaration.Symbol)
                .OfType<IParameterSymbol>()
                .Where(parameter => SymbolEqualityComparer.Default.Equals(
                    parameter.ContainingSymbol,
                    extensionBlock)
                    || SymbolEqualityComparer.Default.Equals(
                        parameter.ContainingSymbol?.OriginalDefinition,
                        extensionBlock.OriginalDefinition))
                .OrderBy(parameter => parameter.Ordinal)
                .FirstOrDefault();
        }

        private static bool IsExtensionBlockInstanceMember(IMethodSymbol method) =>
            !method.IsStatic
            && method.ContainingType?.TypeKind == TypeKind.Extension;

        private void AddNoArguments(
            SymbolDeclaration caller,
            IMethodSymbol method,
            SyntaxNode syntax,
            string callSiteId)
        {
            var location = _locations.Create(syntax.GetLocation());
            if (location is null)
            {
                return;
            }

            _representedCallSites.Add(new CandidateCallSite(
                caller.Node.Id,
                caller.Identity.Project.Key,
                location));

            var value = new SemanticArgumentItem(
                caller.Node.Id,
                _targetDeclaration.Node.Id,
                callSiteId,
                new SemanticSpan(syntax.SpanStart, syntax.Span.Length),
                null,
                null,
                null,
                "no_arguments",
                false,
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                new SemanticLocation(location.FilePath, location.Line, location.Column));
            _results.Add(new SemanticArgumentExtractionResult(
                CreateSortKey(
                    caller.Identity.Project.Key,
                    location,
                    syntax.SpanStart,
                    syntax.Span.Length,
                    -1,
                    -1,
                    syntax.SpanStart),
                value));
        }

        private void AddArgument(
            SymbolDeclaration caller,
            IMethodSymbol method,
            SyntaxNode invocation,
            string callSiteId,
            IParameterSymbol? parameter,
            IOperation valueOperation,
            string argumentKind,
            bool isImplicit,
            IDictionary<string, int> ordinalByParameter)
        {
            var location = _locations.Create(invocation.GetLocation());
            if (location is null)
            {
                return;
            }

            _representedCallSites.Add(new CandidateCallSite(
                caller.Node.Id,
                caller.Identity.Project.Key,
                location));

            var expressionSyntax = valueOperation.Syntax;
            var expressionText = expressionSyntax.SyntaxTree
                .GetText(_cancellationToken)
                .ToString(expressionSyntax.Span);
            var expressionTruncated = expressionText.Length > 512;
            if (expressionTruncated)
            {
                expressionText = expressionText[..512] + "…";
            }

            var parameterDeclaration = parameter is null ? null : TryGetDeclaration(parameter);
            var parameterKey = parameterDeclaration?.Node.Id ?? parameter?.Name ?? string.Empty;
            var expansionIndex = ordinalByParameter.TryGetValue(parameterKey, out var currentExpansionIndex)
                ? currentExpansionIndex
                : 0;
            ordinalByParameter[parameterKey] = expansionIndex + 1;
            var expressionLocation = _locations.Create(expressionSyntax.GetLocation());
            var constant = valueOperation.ConstantValue.HasValue
                ? ToConstant(valueOperation.ConstantValue.Value)
                : null;
            var value = new SemanticArgumentItem(
                caller.Node.Id,
                _targetDeclaration.Node.Id,
                callSiteId,
                new SemanticSpan(invocation.SpanStart, invocation.Span.Length),
                parameterDeclaration?.Node.Id,
                parameter?.Ordinal,
                parameter?.Name,
                argumentKind,
                isImplicit,
                expansionIndex,
                expressionLocation is null
                    ? null
                    : new SemanticSpan(expressionSyntax.SpanStart, expressionSyntax.Span.Length),
                expressionText,
                expressionTruncated,
                valueOperation.Type is { } valueType
                    ? SemanticTypeDisplay.FormatType(valueType)
                    : null,
                parameter is { } formalParameter
                    ? SemanticTypeDisplay.FormatType(formalParameter.Type)
                    : null,
                constant,
                new SemanticLocation(location.FilePath, location.Line, location.Column));
            _results.Add(new SemanticArgumentExtractionResult(
                CreateSortKey(
                    caller.Identity.Project.Key,
                    location,
                    invocation.SpanStart,
                    invocation.Span.Length,
                    parameter?.Ordinal ?? -1,
                    expansionIndex,
                    expressionSyntax.SpanStart),
                value));
        }

        private static JsonElement? ToConstant(object? value) =>
            value is null
                ? SemanticQueryJson.ToElement<object?>(null)
                : SemanticQueryJson.ToElement(value switch
                {
                    char character => character.ToString(),
                    _ => value,
                });

        private string CreateCallSiteId(SymbolDeclaration caller, SyntaxNode syntax) =>
            "call-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                '\u001F',
                _view.SnapshotId,
                caller.Identity.Project.Key,
                syntax.SyntaxTree.FilePath ?? string.Empty,
                syntax.SpanStart.ToString(CultureInfo.InvariantCulture),
                syntax.Span.Length.ToString(CultureInfo.InvariantCulture))))).ToLowerInvariant();

        private static string CreateSortKey(
            string projectKey,
            SourceLocation location,
            int invocationStart,
            int invocationLength,
            int parameterOrdinal,
            int expansionIndex,
            int expressionStart) =>
            string.Join(
                '\u001F',
                projectKey,
                location.FilePath,
                invocationStart.ToString("D10", CultureInfo.InvariantCulture),
                invocationLength.ToString("D10", CultureInfo.InvariantCulture),
                parameterOrdinal.ToString("D5", CultureInfo.InvariantCulture),
                expansionIndex.ToString("D5", CultureInfo.InvariantCulture),
                expressionStart.ToString("D10", CultureInfo.InvariantCulture));
    }

    private sealed class OperationCallSyntaxWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _model;
        private readonly CallWalker _operationWalker;
        private readonly CancellationToken _cancellationToken;

        public OperationCallSyntaxWalker(
            SemanticModel model,
            CallWalker operationWalker,
            CancellationToken cancellationToken)
        {
            _model = model;
            _operationWalker = operationWalker;
            _cancellationToken = cancellationToken;
        }

        public override void Visit(SyntaxNode? node)
        {
            if (node is null)
            {
                return;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            // Only these syntax forms can be roots for the call operations we
            // need. Asking Roslyn for an operation on every syntax node made
            // an arguments query needlessly approach a full semantic-model
            // walk. Once a root is found, OperationWalker traverses nested
            // calls and object creations itself, so the syntax walk can skip
            // that subtree without losing nested call sites.
            if (node is InvocationExpressionSyntax
                or ObjectCreationExpressionSyntax
                or ImplicitObjectCreationExpressionSyntax
                or ConstructorInitializerSyntax
                or PrimaryConstructorBaseTypeSyntax)
            {
                var operation = _model.GetOperation(node, _cancellationToken);
                if (operation is not null
                    && (operation is IInvocationOperation
                        || operation is IObjectCreationOperation
                        || node is ConstructorInitializerSyntax
                        || node is PrimaryConstructorBaseTypeSyntax))
                {
                    _operationWalker.Visit(operation);
                    return;
                }
            }

            base.Visit(node);
        }
    }
}

internal sealed record CandidateCallSite(
    string CallerId,
    string ProjectKey,
    SourceLocation Location);

internal sealed record SemanticArgumentExtraction(
    IReadOnlyList<SemanticArgumentExtractionResult> Results,
    IReadOnlyList<string> Diagnostics);

internal sealed record SemanticArgumentExtractionResult(
    string SortKey,
    SemanticArgumentItem Value);

internal sealed record SemanticSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("length")] int Length);

internal sealed record SemanticArgumentItem(
    [property: JsonPropertyName("caller_id")] string CallerId,
    [property: JsonPropertyName("callee_id")] string CalleeId,
    [property: JsonPropertyName("call_site_id")] string CallSiteId,
    [property: JsonPropertyName("invocation_span")] SemanticSpan InvocationSpan,
    [property: JsonPropertyName("formal_parameter_id")] string? FormalParameterId,
    [property: JsonPropertyName("formal_parameter_ordinal")] int? FormalParameterOrdinal,
    [property: JsonPropertyName("formal_parameter_name")] string? FormalParameterName,
    [property: JsonPropertyName("argument_kind")] string ArgumentKind,
    [property: JsonPropertyName("is_implicit")] bool IsImplicit,
    [property: JsonPropertyName("expansion_index")] int? ExpansionIndex,
    [property: JsonPropertyName("expression_span")] SemanticSpan? ExpressionSpan,
    [property: JsonPropertyName("expression_preview")] string? ExpressionPreview,
    [property: JsonPropertyName("expression_truncated")] bool ExpressionTruncated,
    [property: JsonPropertyName("expression_type")] string? ExpressionType,
    [property: JsonPropertyName("converted_type")] string? ConvertedType,
    [property: JsonPropertyName("constant_value")] JsonElement? ConstantValue,
    [property: JsonPropertyName("location")] SemanticLocation Location);
