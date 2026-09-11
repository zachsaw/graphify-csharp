using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Roslyn;

internal sealed class SourceDeclarationCollector
{
    private readonly ExtractionParallelismOptions _parallelism;

    public SourceDeclarationCollector()
        : this(ExtractionParallelismOptions.Default)
    {
    }

    internal SourceDeclarationCollector(ExtractionParallelismOptions parallelism)
    {
        _parallelism = parallelism ?? throw new ArgumentNullException(nameof(parallelism));
    }

    public async Task<IReadOnlyList<ISymbol>> CollectAsync(
        AnalyzedProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var documents = project.Project.Documents
            .Select(document => new DocumentWork(
                document,
                DocumentKey(document),
                EstimateCost(document)))
            .OrderBy(document => document.Key, StringComparer.Ordinal)
            .Select((document, index) => document with { Index = index })
            .ToArray();
        var declarationsByDocument = new IReadOnlyList<ISymbol>[documents.Length];
        if (documents.Length == 0)
        {
            return [];
        }

        var documentByKey = documents.ToDictionary(document => document.Key, StringComparer.Ordinal);
        var batches = ExtractionBatchPlanner.Create(
            project.Identity.Key,
            documents.Select(document => new ExtractionDocument(document.Key, document.EstimatedCost)),
            _parallelism.MaxDegreeOfParallelism,
            _parallelism.TargetBatchesPerWorker,
            _parallelism.MinimumDocumentsPerBatch);
        if (batches.Length == 1)
        {
            await CollectBatchAsync(
                    project,
                    batches[0],
                    documentByKey,
                    declarationsByDocument,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await Parallel.ForEachAsync(
                    batches,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = _parallelism.MaxDegreeOfParallelism,
                    },
                    async (batch, token) =>
                    {
                        await CollectBatchAsync(
                                project,
                                batch,
                                documentByKey,
                                declarationsByDocument,
                                token)
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }

        var declarations = new List<ISymbol>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var documentDeclarations in declarationsByDocument)
        {
            foreach (var symbol in documentDeclarations)
            {
                if (seenSymbols.Add(symbol))
                {
                    declarations.Add(symbol);
                }
            }
        }

        return declarations;
    }

    private static async Task CollectBatchAsync(
        AnalyzedProject project,
        ExtractionBatch batch,
        IReadOnlyDictionary<string, DocumentWork> documentByKey,
        IList<IReadOnlyList<ISymbol>> declarationsByDocument,
        CancellationToken cancellationToken)
    {
        foreach (var document in batch.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var work = documentByKey[document.Key];
            declarationsByDocument[work.Index] = await CollectDocumentAsync(
                    project,
                    work.Document,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<ISymbol>> CollectDocumentAsync(
        AnalyzedProject project,
        Microsoft.CodeAnalysis.Document document,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        var semanticModel = project.Compilation.GetSemanticModel(root.SyntaxTree);
        var declarations = new List<ISymbol>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        new DeclarationSyntaxWalker(
                semanticModel,
                declarations,
                seenSymbols,
                cancellationToken)
            .Visit(root);
        return declarations;
    }

    private static string DocumentKey(Microsoft.CodeAnalysis.Document document) =>
        string.IsNullOrWhiteSpace(document.FilePath)
            ? document.Name
            : Path.GetFullPath(document.FilePath).Replace('\\', '/');

    private static long EstimateCost(Microsoft.CodeAnalysis.Document document)
    {
        if (document.FilePath is string path)
        {
            try
            {
                return Math.Max(1, new FileInfo(path).Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return Math.Max(1, document.Name.Length);
    }

    private sealed record DocumentWork(
        Microsoft.CodeAnalysis.Document Document,
        string Key,
        long EstimatedCost,
        int Index = -1);

    private sealed class DeclarationSyntaxWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;
        private readonly ICollection<ISymbol> _declarations;
        private readonly ISet<ISymbol> _seenSymbols;
        private readonly CancellationToken _cancellationToken;

        public DeclarationSyntaxWalker(
            SemanticModel semanticModel,
            ICollection<ISymbol> declarations,
            ISet<ISymbol> seenSymbols,
            CancellationToken cancellationToken)
        {
            _semanticModel = semanticModel;
            _declarations = declarations;
            _seenSymbols = seenSymbols;
            _cancellationToken = cancellationToken;
        }

        public override void Visit(SyntaxNode? node)
        {
            if (node is not null)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (IsDeclarationCandidate(node))
                {
                    AddDeclaredSymbols(node);
                }
            }

            base.Visit(node);
        }

        private void AddDeclaredSymbols(SyntaxNode node)
        {
            foreach (var symbol in DeclaredSymbols(_semanticModel, node, _cancellationToken))
            {
                if (_seenSymbols.Add(symbol))
                {
                    _declarations.Add(symbol);
                }
            }
        }
    }

    private static bool IsDeclarationCandidate(SyntaxNode node) => node is
        CompilationUnitSyntax
        or BaseFieldDeclarationSyntax
        or MemberDeclarationSyntax
        or LocalFunctionStatementSyntax
        or AccessorDeclarationSyntax
        or AnonymousObjectMemberDeclaratorSyntax
        or ArgumentSyntax
        or TupleElementSyntax
        or VariableDeclaratorSyntax
        or SingleVariableDesignationSyntax
        or LabeledStatementSyntax
        or SwitchLabelSyntax
        or UsingDirectiveSyntax
        or ExternAliasDirectiveSyntax
        or ParameterSyntax
        or TypeParameterSyntax
        or ForEachStatementSyntax
        or CatchDeclarationSyntax
        or JoinIntoClauseSyntax
        or QueryContinuationSyntax
        or FromClauseSyntax
        or LetClauseSyntax
        or JoinClauseSyntax;

    private static IEnumerable<ISymbol> DeclaredSymbols(
        SemanticModel semanticModel,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        switch (node)
        {
            case CompilationUnitSyntax compilationUnit:
                if (semanticModel.GetDeclaredSymbol(compilationUnit, cancellationToken) is { } entryPoint)
                {
                    yield return entryPoint;
                }

                break;
            case BaseFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    if (semanticModel.GetDeclaredSymbol(variable, cancellationToken) is { } fieldVariableSymbol)
                    {
                        yield return fieldVariableSymbol;
                    }
                }

                break;
            case MemberDeclarationSyntax member:
                if (semanticModel.GetDeclaredSymbol(member, cancellationToken) is { } memberSymbol)
                {
                    yield return memberSymbol;
                }

                break;
            case LocalFunctionStatementSyntax localFunction:
                if (semanticModel.GetDeclaredSymbol(localFunction, cancellationToken) is { } localFunctionSymbol)
                {
                    yield return localFunctionSymbol;
                }

                break;
            case AccessorDeclarationSyntax accessor:
                if (semanticModel.GetDeclaredSymbol(accessor, cancellationToken) is { } accessorSymbol)
                {
                    yield return accessorSymbol;
                }

                break;
            case AnonymousObjectMemberDeclaratorSyntax anonymousMember:
                if (semanticModel.GetDeclaredSymbol(anonymousMember, cancellationToken) is { } anonymousMemberSymbol)
                {
                    yield return anonymousMemberSymbol;
                }

                break;
            case ArgumentSyntax tupleArgument:
                if (semanticModel.GetDeclaredSymbol(tupleArgument, cancellationToken) is { } tupleElementSymbol)
                {
                    yield return tupleElementSymbol;
                }

                break;
            case TupleElementSyntax tupleElement:
                if (semanticModel.GetDeclaredSymbol(tupleElement, cancellationToken) is { } tupleElementDeclaration)
                {
                    yield return tupleElementDeclaration;
                }

                break;
            case VariableDeclaratorSyntax variable:
                if (semanticModel.GetDeclaredSymbol(variable, cancellationToken) is { } variableSymbol)
                {
                    yield return variableSymbol;
                }

                break;
            case SingleVariableDesignationSyntax designation:
                if (semanticModel.GetDeclaredSymbol(designation, cancellationToken) is { } designationSymbol)
                {
                    yield return designationSymbol;
                }

                break;
            case LabeledStatementSyntax labeledStatement:
                if (semanticModel.GetDeclaredSymbol(labeledStatement, cancellationToken) is { } labelSymbol)
                {
                    yield return labelSymbol;
                }

                break;
            case SwitchLabelSyntax switchLabel:
                if (semanticModel.GetDeclaredSymbol(switchLabel, cancellationToken) is { } switchLabelSymbol)
                {
                    yield return switchLabelSymbol;
                }

                break;
            case UsingDirectiveSyntax usingDirective:
                if (semanticModel.GetDeclaredSymbol(usingDirective, cancellationToken) is { } aliasSymbol)
                {
                    yield return aliasSymbol;
                }

                break;
            case ExternAliasDirectiveSyntax externAlias:
                if (semanticModel.GetDeclaredSymbol(externAlias, cancellationToken) is { } externAliasSymbol)
                {
                    yield return externAliasSymbol;
                }

                break;
            case ParameterSyntax parameter:
                if (semanticModel.GetDeclaredSymbol(parameter, cancellationToken) is { } parameterSymbol)
                {
                    yield return parameterSymbol;
                }

                break;
            case TypeParameterSyntax typeParameter:
                if (semanticModel.GetDeclaredSymbol(typeParameter, cancellationToken) is { } typeParameterSymbol)
                {
                    yield return typeParameterSymbol;
                }

                break;
            case ForEachStatementSyntax forEach:
                if (semanticModel.GetDeclaredSymbol(forEach) is { } forEachSymbol)
                {
                    yield return forEachSymbol;
                }

                break;
            case CatchDeclarationSyntax catchDeclaration:
                if (semanticModel.GetDeclaredSymbol(catchDeclaration) is { } catchSymbol)
                {
                    yield return catchSymbol;
                }

                break;
            case JoinIntoClauseSyntax joinInto:
                if (semanticModel.GetDeclaredSymbol(joinInto, cancellationToken) is { } joinIntoSymbol)
                {
                    yield return joinIntoSymbol;
                }

                break;
            case QueryContinuationSyntax continuation:
                if (semanticModel.GetDeclaredSymbol(continuation, cancellationToken) is { } continuationSymbol)
                {
                    yield return continuationSymbol;
                }

                break;
            case FromClauseSyntax from:
                if (FindRangeVariable(semanticModel, from.Identifier, from, cancellationToken) is { } fromSymbol)
                {
                    yield return fromSymbol;
                }

                break;
            case LetClauseSyntax let:
                if (FindRangeVariable(semanticModel, let.Identifier, let, cancellationToken) is { } letSymbol)
                {
                    yield return letSymbol;
                }

                break;
            case JoinClauseSyntax join:
                if (FindRangeVariable(semanticModel, join.Identifier, join, cancellationToken) is { } joinSymbol)
                {
                    yield return joinSymbol;
                }

                break;
        }
    }

    private static IRangeVariableSymbol? FindRangeVariable(
        SemanticModel semanticModel,
        SyntaxToken identifier,
        SyntaxNode scope,
        CancellationToken cancellationToken)
    {
        var positions = new[]
        {
            identifier.SpanStart,
            identifier.Span.End,
            identifier.Span.End + 1,
            scope.SpanStart,
            scope.Span.End,
            scope.Span.End + 1,
            scope.Parent?.Span.End ?? -1,
        }.Concat(scope.AncestorsAndSelf()
            .OfType<QueryExpressionSyntax>()
            .SelectMany(query => query.DescendantNodesAndTokens().Select(nodeOrToken => nodeOrToken.SpanStart)));
        var maxPosition = scope.SyntaxTree.GetRoot(cancellationToken).FullSpan.End;
        foreach (var position in positions.Distinct())
        {
            if (position < 0 || position > maxPosition)
            {
                continue;
            }

            var matches = semanticModel.LookupSymbols(position, name: identifier.ValueText)
                .OfType<IRangeVariableSymbol>()
                .Where(symbol => symbol.Locations.Any(location =>
                    location.IsInSource && location.SourceSpan.Start == identifier.SpanStart))
                .ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
        }

        return null;
    }
}
