using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Graphify.CSharp.Roslyn;

public sealed class SemanticOperationWalker : OperationWalker
{
    private readonly DeclarationCatalog _catalog;
    private readonly SemanticCallerResolver _callerResolver;
    private readonly SemanticModel _semanticModel;
    private readonly SourceLocationFactory _locations;
    private readonly ICollection<GraphEdge> _edges;

    public SemanticOperationWalker(
        DeclarationCatalog catalog,
        SemanticModel semanticModel,
        SourceLocationFactory locations,
        ICollection<GraphEdge> edges)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _callerResolver = new SemanticCallerResolver(_catalog);
        _semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _edges = edges ?? throw new ArgumentNullException(nameof(edges));
    }

    public override void Visit(IOperation? operation)
    {
        if (operation is not null)
        {
            base.Visit(operation);
        }
    }

    public override void VisitInvocation(IInvocationOperation operation)
    {
        AddMethodEdge(operation.TargetMethod, GraphRelation.Calls, operation);
        base.VisitInvocation(operation);
    }

    public override void VisitObjectCreation(IObjectCreationOperation operation)
    {
        AddMethodEdge(operation.Constructor, GraphRelation.Calls, operation);
        base.VisitObjectCreation(operation);
    }

    public override void VisitMethodReference(IMethodReferenceOperation operation)
    {
        AddMethodEdge(operation.Method, GraphRelation.References, operation);
        base.VisitMethodReference(operation);
    }

    public override void VisitPropertyReference(IPropertyReferenceOperation operation)
    {
        AddSymbolEdge(operation.Property, GraphRelation.References, operation);
        base.VisitPropertyReference(operation);
    }

    public override void VisitFieldReference(IFieldReferenceOperation operation)
    {
        AddSymbolEdge(operation.Field, GraphRelation.References, operation);
        base.VisitFieldReference(operation);
    }

    public override void VisitEventReference(IEventReferenceOperation operation)
    {
        AddSymbolEdge(operation.Event, GraphRelation.References, operation);
        base.VisitEventReference(operation);
    }

    public override void VisitTypeOf(ITypeOfOperation operation)
    {
        AddSymbolEdge(operation.TypeOperand, GraphRelation.References, operation);
        base.VisitTypeOf(operation);
    }

    internal void AddReference(ISymbol callerSymbol, ISymbol targetSymbol, Location location)
    {
        ArgumentNullException.ThrowIfNull(callerSymbol);
        ArgumentNullException.ThrowIfNull(targetSymbol);
        ArgumentNullException.ThrowIfNull(location);

        var target = FindDeclaration(targetSymbol);
        var caller = ResolveCaller(callerSymbol);
        if (target is null || caller is null)
        {
            return;
        }

        AddReference(caller, target, location);
    }

    internal void AddReference(SymbolDeclaration caller, ISymbol targetSymbol, Location location)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(targetSymbol);
        ArgumentNullException.ThrowIfNull(location);

        if (FindDeclaration(targetSymbol) is { } target)
        {
            AddReference(caller, target, location);
        }
    }

    internal SymbolDeclaration? ResolveCaller(int position) =>
        _callerResolver.Resolve(_semanticModel, position);

    private void AddReference(SymbolDeclaration caller, SymbolDeclaration target, Location location)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(location);

        var sourceLocation = _locations.Create(location);
        if (sourceLocation is null)
        {
            return;
        }

        _edges.Add(new GraphEdge(
            caller.Node.Id,
            target.Node.Id,
            GraphRelation.References,
            EvidenceKind.Extracted,
            confidence: 1.0,
            [sourceLocation]));
    }

    private SymbolDeclaration? ResolveCaller(ISymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (_catalog.TryGet(current, out var declaration))
            {
                return declaration;
            }
        }

        return null;
    }

    private void AddMethodEdge(IMethodSymbol? target, GraphRelation relation, IOperation operation)
    {
        if (target is not null)
        {
            AddSymbolEdge(target, relation, operation);
        }
    }

    private void AddSymbolEdge(ISymbol target, GraphRelation relation, IOperation operation)
    {
        var targetDeclaration = FindDeclaration(target);
        if (targetDeclaration is null)
        {
            return;
        }

        var caller = _callerResolver.Resolve(_semanticModel, operation.Syntax.SpanStart);
        if (caller is null)
        {
            return;
        }

        var location = _locations.Create(operation.Syntax.GetLocation());
        if (location is null)
        {
            return;
        }

        _edges.Add(new GraphEdge(
            caller.Node.Id,
            targetDeclaration.Node.Id,
            relation,
            EvidenceKind.Extracted,
            confidence: 1.0,
            [location]));
    }

    private SymbolDeclaration? FindDeclaration(ISymbol symbol)
    {
        if (_catalog.TryGet(symbol, out var declaration) || _catalog.TryGetReference(symbol, out declaration))
        {
            return declaration;
        }

        var originalDefinition = symbol.OriginalDefinition;
        return _catalog.TryGet(originalDefinition, out declaration)
            || _catalog.TryGetReference(originalDefinition, out declaration)
            ? declaration
            : null;
    }
}
