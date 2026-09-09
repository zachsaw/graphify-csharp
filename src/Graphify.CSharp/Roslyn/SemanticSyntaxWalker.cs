using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticSyntaxWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly SemanticOperationWalker _operationWalker;

    public SemanticSyntaxWalker(
        SemanticModel semanticModel,
        SemanticOperationWalker operationWalker)
    {
        _semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
        _operationWalker = operationWalker ?? throw new ArgumentNullException(nameof(operationWalker));
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitCollectionExpression(CollectionExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitCollectionExpression(node);
    }

    public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitTypeOfExpression(node);
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        VisitSymbol(node);
        VisitOperation(node);
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

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitMemberAccessExpression(node);
    }

    public override void VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitMemberBindingExpression(node);
    }

    public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitElementAccessExpression(node);
    }

    public override void VisitBreakStatement(BreakStatementSyntax node)
    {
        VisitOperation(node);
        base.VisitBreakStatement(node);
    }

    public override void VisitContinueStatement(ContinueStatementSyntax node)
    {
        VisitOperation(node);
        base.VisitContinueStatement(node);
    }

    public override void VisitFixedStatement(FixedStatementSyntax node)
    {
        VisitOperation(node);
        base.VisitFixedStatement(node);
    }

    public override void VisitSizeOfExpression(SizeOfExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitSizeOfExpression(node);
    }

#if NET11_0_OR_GREATER
#pragma warning disable RSEXPERIMENTAL006
    public override void VisitUnsafeExpression(UnsafeExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitUnsafeExpression(node);
    }
#pragma warning restore RSEXPERIMENTAL006
#endif

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        VisitSymbol(node);
        VisitOperation(node);
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

    private void VisitOperation(SyntaxNode node)
    {
        var operation = _semanticModel.GetOperation(node);
        _operationWalker.Visit(operation);
    }
}
