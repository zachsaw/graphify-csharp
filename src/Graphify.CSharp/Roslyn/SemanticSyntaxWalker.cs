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

    public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
    {
        VisitOperation(node);
        base.VisitTypeOfExpression(node);
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        VisitOperation(node);
        base.VisitAttribute(node);
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

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        VisitOperation(node);
        base.VisitIdentifierName(node);
    }

    private void VisitOperation(SyntaxNode node)
    {
        var operation = _semanticModel.GetOperation(node);
        _operationWalker.Visit(operation);
    }
}
