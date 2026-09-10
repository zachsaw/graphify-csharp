using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticSyntaxWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly SemanticOperationWalker _operationWalker;
    private readonly ISet<SyntaxNode> _visitedOperationRoots =
        new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);

    public SemanticSyntaxWalker(
        SemanticModel semanticModel,
        SemanticOperationWalker operationWalker)
    {
        _semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
        _operationWalker = operationWalker ?? throw new ArgumentNullException(nameof(operationWalker));
    }

    public override void Visit(SyntaxNode? node)
    {
        if (node is null)
        {
            return;
        }

        var operation = !HasVisitedOperationRoot(node)
            ? _semanticModel.GetOperation(node)
            : null;
        if (operation is not null && ShouldVisitOperationRoot(operation.Syntax))
        {
            _operationWalker.Visit(operation);
        }

        base.Visit(node);
    }

    private bool ShouldVisitOperationRoot(SyntaxNode syntax)
    {
        for (var parent = syntax.Parent; parent is not null; parent = parent.Parent)
        {
            if (_visitedOperationRoots.Contains(parent))
            {
                return false;
            }
        }

        return _visitedOperationRoots.Add(syntax);
    }

    private bool HasVisitedOperationRoot(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (_visitedOperationRoots.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        VisitSymbol(node);
        base.VisitAttribute(node);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        VisitSymbol(node);
        base.VisitGenericName(node);
    }

    public override void VisitQualifiedName(QualifiedNameSyntax node)
    {
        VisitSymbol(node);
        base.VisitQualifiedName(node);
    }

    public override void VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
    {
        VisitSymbol(node);
        base.VisitAliasQualifiedName(node);
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        VisitSymbol(node);
        base.VisitIdentifierName(node);
    }

    private void VisitSymbol(SyntaxNode node)
    {
        var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol is IMethodSymbol && IsInvocationTarget(node))
        {
            return;
        }

        var caller = _operationWalker.ResolveCaller(node.SpanStart);
        if (caller is null)
        {
            return;
        }

        if (symbol is not null)
        {
            _operationWalker.AddReference(caller, symbol, node.GetLocation());
        }

        if (node is IdentifierNameSyntax identifier
            && _semanticModel.GetAliasInfo(identifier) is { } alias)
        {
            _operationWalker.AddReference(caller, alias, identifier.GetLocation());
        }
    }

    private static bool IsInvocationTarget(SyntaxNode node)
    {
        return node.Parent switch
        {
            InvocationExpressionSyntax invocation when invocation.Expression == node => true,
            MemberAccessExpressionSyntax memberAccess
                when memberAccess.Name == node
                    && memberAccess.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == memberAccess => true,
            MemberBindingExpressionSyntax memberBinding
                when memberBinding.Name == node
                    && memberBinding.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == memberBinding => true,
            _ => false,
        };
    }

}
