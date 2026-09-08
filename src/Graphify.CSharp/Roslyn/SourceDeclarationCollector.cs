using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Roslyn;

internal sealed class SourceDeclarationCollector
{
    public async Task<IReadOnlyList<ISymbol>> CollectAsync(
        AnalyzedProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var declarations = new List<ISymbol>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var document in project.Project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            var semanticModel = project.Compilation.GetSemanticModel(root.SyntaxTree);
            foreach (var node in root.DescendantNodesAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var symbol in DeclaredSymbols(semanticModel, node, cancellationToken))
                {
                    if (seenSymbols.Add(symbol))
                    {
                        declarations.Add(symbol);
                    }
                }
            }
        }

        return declarations;
    }

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
