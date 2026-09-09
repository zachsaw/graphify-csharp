using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
            if (operation is IRangeOperation range)
            {
                AddMethodEdge(range.Method, GraphRelation.Calls, range);
            }

            if (operation.Syntax is FixedStatementSyntax fixedStatement)
            {
                AddFixedPatternReferences(fixedStatement, operation);
            }

            if (operation is ICaseClauseOperation caseClause)
            {
                AddSymbolEdge(caseClause.Label, GraphRelation.References, operation);
            }

            base.Visit(operation);
        }
    }

    public override void VisitInvocation(IInvocationOperation operation)
    {
        AddMethodEdge(operation.TargetMethod, GraphRelation.Calls, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitInvocation(operation);
    }

    public override void VisitObjectCreation(IObjectCreationOperation operation)
    {
        AddMethodEdge(operation.Constructor, GraphRelation.Calls, operation);
        base.VisitObjectCreation(operation);
    }

    public override void VisitCollectionExpression(ICollectionExpressionOperation operation)
    {
        AddMethodEdge(operation.ConstructMethod, GraphRelation.Calls, operation);
        base.VisitCollectionExpression(operation);
    }

    public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
    {
        AddMethodEdge(operation.OperatorMethod, GraphRelation.Calls, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitCompoundAssignment(operation);
    }

    public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
    {
        AddMethodEdge(operation.OperatorMethod, GraphRelation.Calls, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitIncrementOrDecrement(operation);
    }

    public override void VisitBinaryOperator(IBinaryOperation operation)
    {
        AddMethodEdge(operation.OperatorMethod, GraphRelation.Calls, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitBinaryOperator(operation);
    }

    public override void VisitUnaryOperator(IUnaryOperation operation)
    {
        AddMethodEdge(operation.OperatorMethod, GraphRelation.Calls, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitUnaryOperator(operation);
    }

    public override void VisitConversion(IConversionOperation operation)
    {
        AddMethodEdge(operation.OperatorMethod, GraphRelation.Calls, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitConversion(operation);
    }

    public override void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation)
    {
        AddDeconstructionMethod(operation);
        base.VisitDeconstructionAssignment(operation);
    }

    public override void VisitForEachLoop(IForEachLoopOperation operation)
    {
        AddForEachPatternReferences(operation);
        base.VisitForEachLoop(operation);
    }

    public override void VisitAwait(IAwaitOperation operation)
    {
        AddAwaitPatternReferences(operation.Operation, operation.Syntax);
        base.VisitAwait(operation);
    }

    public override void VisitUsing(IUsingOperation operation)
    {
        foreach (var resource in ResourceOperations(operation.Resources))
        {
            AddDisposePatternReference(resource, operation.IsAsynchronous, operation.Syntax);
        }

        base.VisitUsing(operation);
    }

    public override void VisitUsingDeclaration(IUsingDeclarationOperation operation)
    {
        foreach (var resource in ResourceOperations(operation.DeclarationGroup))
        {
            AddDisposePatternReference(resource, operation.IsAsynchronous, operation.Syntax);
        }

        base.VisitUsingDeclaration(operation);
    }

    public override void VisitWith(IWithOperation operation)
    {
        AddMethodEdge(operation.CloneMethod, GraphRelation.Calls, operation);
        base.VisitWith(operation);
    }

    public override void VisitRecursivePattern(IRecursivePatternOperation operation)
    {
        AddMethodEdge(operation.DeconstructSymbol as IMethodSymbol, GraphRelation.Calls, operation);
        base.VisitRecursivePattern(operation);
    }

    public override void VisitListPattern(IListPatternOperation operation)
    {
        AddSymbolEdge(operation.LengthSymbol, GraphRelation.References, operation);
        AddSymbolEdge(operation.IndexerSymbol, GraphRelation.References, operation);
        base.VisitListPattern(operation);
    }

    public override void VisitSlicePattern(ISlicePatternOperation operation)
    {
        AddSymbolEdge(operation.SliceSymbol, GraphRelation.Calls, operation);
        base.VisitSlicePattern(operation);
    }

    public override void VisitPropertySubpattern(IPropertySubpatternOperation operation)
    {
        if (operation.Member is IMemberReferenceOperation memberReference)
        {
            AddSymbolEdge(memberReference.Member, GraphRelation.References, operation);
        }

        base.VisitPropertySubpattern(operation);
    }

    public override void VisitMethodReference(IMethodReferenceOperation operation)
    {
        AddMethodEdge(operation.Method, GraphRelation.References, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitMethodReference(operation);
    }

    public override void VisitFieldReference(IFieldReferenceOperation operation)
    {
        AddSymbolEdge(operation.Field, GraphRelation.References, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitFieldReference(operation);
    }

    public override void VisitEventReference(IEventReferenceOperation operation)
    {
        AddSymbolEdge(operation.Event, GraphRelation.References, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        base.VisitEventReference(operation);
    }

    public override void VisitLocalReference(ILocalReferenceOperation operation)
    {
        if (!operation.IsDeclaration)
        {
            AddSymbolEdge(operation.Local, GraphRelation.References, operation);
        }

        base.VisitLocalReference(operation);
    }

    public override void VisitParameterReference(IParameterReferenceOperation operation)
    {
        AddSymbolEdge(operation.Parameter, GraphRelation.References, operation);
        base.VisitParameterReference(operation);
    }

    public override void VisitArgument(IArgumentOperation operation)
    {
        AddSymbolEdge(operation.Parameter, GraphRelation.References, operation);
        base.VisitArgument(operation);
    }

    public override void VisitEventAssignment(IEventAssignmentOperation operation)
    {
        if (operation.EventReference is not IEventReferenceOperation eventReference)
        {
            base.VisitEventAssignment(operation);
            return;
        }

        var accessor = operation.Adds
            ? eventReference.Event.AddMethod
            : eventReference.Event.RemoveMethod;
        AddMethodEdge(accessor, GraphRelation.Calls, operation);
        base.VisitEventAssignment(operation);
    }

    public override void VisitBranch(IBranchOperation operation)
    {
        AddSymbolEdge(operation.Target, GraphRelation.References, operation);
        base.VisitBranch(operation);
    }

    public override void VisitImplicitIndexerReference(IImplicitIndexerReferenceOperation operation)
    {
        AddSymbolEdge(operation.IndexerSymbol, GraphRelation.References, operation);
        AddSymbolEdge(operation.LengthSymbol, GraphRelation.References, operation);
        base.VisitImplicitIndexerReference(operation);
    }

    public override void VisitTypeOf(ITypeOfOperation operation)
    {
        AddSymbolEdge(operation.TypeOperand, GraphRelation.References, operation);
        base.VisitTypeOf(operation);
    }

    public override void VisitSizeOf(ISizeOfOperation operation)
    {
        AddSymbolEdge(operation.TypeOperand, GraphRelation.References, operation);
        base.VisitSizeOf(operation);
    }

    public override void VisitIsType(IIsTypeOperation operation)
    {
        AddSymbolEdge(operation.TypeOperand, GraphRelation.References, operation);
        base.VisitIsType(operation);
    }

    public override void VisitFunctionPointerInvocation(IFunctionPointerInvocationOperation operation)
    {
        var methodReference = operation.Target
            .DescendantsAndSelf()
            .OfType<IMethodReferenceOperation>()
            .FirstOrDefault();
        var targetMethod = methodReference?.Method
            ?? FindMethodSymbol(operation.Target.Syntax);
        if (targetMethod is not null)
        {
            AddMethodEdge(targetMethod, GraphRelation.Calls, operation);
        }

        base.VisitFunctionPointerInvocation(operation);
    }

    public override void VisitPropertyReference(IPropertyReferenceOperation operation)
    {
        AddSymbolEdge(operation.Property, GraphRelation.References, operation);
        AddSymbolEdge(operation.ConstrainedToType, GraphRelation.References, operation);
        AddPropertyAccessorEdges(operation);
        base.VisitPropertyReference(operation);
    }

    public override void VisitInterpolatedStringHandlerCreation(IInterpolatedStringHandlerCreationOperation operation)
    {
        if (operation.HandlerCreation is IObjectCreationOperation creation)
        {
            AddMethodEdge(creation.Constructor, GraphRelation.Calls, operation);
        }

        base.VisitInterpolatedStringHandlerCreation(operation);
    }

    public override void VisitInterpolatedStringAppend(IInterpolatedStringAppendOperation operation)
    {
        if (operation.AppendCall is IInvocationOperation invocation)
        {
            AddMethodEdge(invocation.TargetMethod, GraphRelation.Calls, operation);
        }

        base.VisitInterpolatedStringAppend(operation);
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

    internal void AddResolvedSymbolUse(ISymbol? target, GraphRelation relation, int callerPosition, Location location)
    {
        if (target is null)
        {
            return;
        }

        var targetDeclaration = FindDeclaration(target);
        var caller = _callerResolver.Resolve(_semanticModel, callerPosition);
        if (targetDeclaration is null || caller is null)
        {
            return;
        }

        AddResolvedSymbolUse(caller, targetDeclaration, relation, location);
    }

    internal SymbolDeclaration? ResolveCaller(int position) =>
        _callerResolver.Resolve(_semanticModel, position);

    private void AddReference(SymbolDeclaration caller, SymbolDeclaration target, Location location)
    {
        AddResolvedSymbolUse(caller, target, GraphRelation.References, location);
    }

    private void AddResolvedSymbolUse(
        SymbolDeclaration caller,
        SymbolDeclaration target,
        GraphRelation relation,
        Location location)
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
            relation,
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

    private void AddSymbolEdge(ISymbol? target, GraphRelation relation, IOperation operation)
    {
        if (target is null)
        {
            return;
        }

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

    private void AddPropertyAccessorEdges(IPropertyReferenceOperation operation)
    {
        var target = UnwrapAssignmentTarget(operation);
        var use = target.Parent switch
        {
            IIncrementOrDecrementOperation increment
                when ReferenceEquals(increment.Target, target) => PropertyAccessorUse.ReadAndWrite,
            ICompoundAssignmentOperation compound
                when ReferenceEquals(compound.Target, target) => PropertyAccessorUse.ReadAndWrite,
            IAssignmentOperation assignment
                when ReferenceEquals(assignment.Target, target) => PropertyAccessorUse.Write,
            _ => PropertyAccessorUse.Read,
        };

        if (use is PropertyAccessorUse.Read or PropertyAccessorUse.ReadAndWrite)
        {
            AddMethodEdge(operation.Property.GetMethod, GraphRelation.Calls, operation);
        }

        if (use is PropertyAccessorUse.Write or PropertyAccessorUse.ReadAndWrite)
        {
            AddMethodEdge(operation.Property.SetMethod, GraphRelation.Calls, operation);
        }
    }

    private static IOperation UnwrapAssignmentTarget(IOperation operation)
    {
        var current = operation;
        while (current.Parent is IConversionOperation or IParenthesizedOperation)
        {
            current = current.Parent;
        }

        return current;
    }

    private enum PropertyAccessorUse
    {
        Read,
        Write,
        ReadAndWrite,
    }

    private void AddDeconstructionMethod(IDeconstructionAssignmentOperation operation)
    {
        if (operation.Value is null || operation.Value.Syntax is not ExpressionSyntax valueSyntax)
        {
            return;
        }

        var argumentCount = DeconstructionLeafCount(operation.Target);
        if (argumentCount == 0)
        {
            return;
        }

        var arguments = string.Join(", ", Enumerable.Range(0, argumentCount).Select(index => $"out var __graphifyDeconstruct{index}"));
        if (ResolveSpeculativeMethod(valueSyntax, "Deconstruct", arguments) is { } method)
        {
            AddSymbolEdge(method, GraphRelation.Calls, operation);
        }
    }

    private void AddForEachPatternReferences(IForEachLoopOperation operation)
    {
        if (operation.Collection is null)
        {
            return;
        }

        var collectionSyntax = operation.Collection.Syntax as ExpressionSyntax;
        if (collectionSyntax is null)
        {
            return;
        }

        var getEnumeratorName = operation.IsAsynchronous ? "GetAsyncEnumerator" : "GetEnumerator";
        var getEnumerator = ResolveSpeculativeMethod(collectionSyntax, getEnumeratorName);
        AddSymbolEdge(getEnumerator, GraphRelation.Calls, operation);
        if (getEnumerator is null)
        {
            return;
        }

        var enumeratorType = getEnumerator.ReturnType;
        var moveNextName = operation.IsAsynchronous ? "MoveNextAsync" : "MoveNext";
        var moveNext = FindParameterlessMethod(enumeratorType, moveNextName);
        AddSymbolEdge(moveNext, GraphRelation.Calls, operation);
        AddAwaitPatternReferencesForType(moveNext?.ReturnType, operation.Syntax);

        var current = FindReadableProperty(enumeratorType, "Current");
        AddSymbolEdge(current, GraphRelation.References, operation);

        var disposeName = operation.IsAsynchronous ? "DisposeAsync" : "Dispose";
        var dispose = FindDisposalMethod(enumeratorType, disposeName, operation.IsAsynchronous);
        AddSymbolEdge(dispose, GraphRelation.Calls, operation);
        if (operation.IsAsynchronous)
        {
            AddAwaitPatternReferencesForType(dispose?.ReturnType, operation.Syntax);
        }
    }

    private void AddAwaitPatternReferences(IOperation? awaitedOperation, SyntaxNode syntax)
    {
        if (awaitedOperation?.Type is null)
        {
            return;
        }

        var awaitedSyntax = awaitedOperation.Syntax as ExpressionSyntax;
        if (awaitedSyntax is null)
        {
            return;
        }

        var getAwaiter = ResolveSpeculativeMethod(awaitedSyntax, "GetAwaiter");
        AddResolvedSymbolUse(getAwaiter, GraphRelation.Calls, syntax.SpanStart, syntax.GetLocation());
        if (getAwaiter?.ReturnType is not { } awaiterType)
        {
            return;
        }

        AddResolvedSymbolUse(
            FindReadableProperty(awaiterType, "IsCompleted"),
            GraphRelation.References,
            syntax.SpanStart,
            syntax.GetLocation());
        AddResolvedSymbolUse(
            FindParameterlessMethod(awaiterType, "GetResult"),
            GraphRelation.Calls,
            syntax.SpanStart,
            syntax.GetLocation());

        var continuation = FindParameterlessContinuation(awaiterType);
        AddResolvedSymbolUse(continuation, GraphRelation.Calls, syntax.SpanStart, syntax.GetLocation());
    }

    private void AddDisposePatternReference(IOperation resource, bool isAsync, SyntaxNode syntax)
    {
        var resourceType = resource.Type
            ?? (resource as IVariableDeclaratorOperation)?.Symbol.Type;
        if (resourceType is null)
        {
            return;
        }

        var methodName = isAsync ? "DisposeAsync" : "Dispose";
        var method = FindDisposalMethod(resourceType, methodName, isAsync);
        AddResolvedSymbolUse(method, GraphRelation.Calls, syntax.SpanStart, syntax.GetLocation());
        if (isAsync)
        {
            AddAwaitPatternReferencesForType(method?.ReturnType, syntax);
        }
    }

    private void AddFixedPatternReferences(FixedStatementSyntax fixedStatement, IOperation operation)
    {
        foreach (var variable in fixedStatement.Declaration.Variables)
        {
            if (variable.Initializer?.Value is ExpressionSyntax value
                && ResolveSpeculativeMethod(value, "GetPinnableReference") is { } method)
            {
                AddMethodEdge(method, GraphRelation.Calls, operation);
            }
        }
    }

    private IMethodSymbol? FindMethodSymbol(SyntaxNode syntax)
    {
        return new[] { syntax }
            .Concat(syntax.DescendantNodes())
            .Select(node => _semanticModel.GetSymbolInfo(node).Symbol)
            .OfType<IMethodSymbol>()
            .FirstOrDefault();
    }

    private void AddAwaitPatternReferencesForType(ITypeSymbol? awaitableType, SyntaxNode syntax)
    {
        var getAwaiter = FindParameterlessMethod(awaitableType, "GetAwaiter");
        AddResolvedSymbolUse(getAwaiter, GraphRelation.Calls, syntax.SpanStart, syntax.GetLocation());
        if (getAwaiter?.ReturnType is not { } awaiterType)
        {
            return;
        }

        AddResolvedSymbolUse(
            FindReadableProperty(awaiterType, "IsCompleted"),
            GraphRelation.References,
            syntax.SpanStart,
            syntax.GetLocation());
        AddResolvedSymbolUse(
            FindParameterlessMethod(awaiterType, "GetResult"),
            GraphRelation.Calls,
            syntax.SpanStart,
            syntax.GetLocation());
        AddResolvedSymbolUse(
            FindParameterlessContinuation(awaiterType),
            GraphRelation.Calls,
            syntax.SpanStart,
            syntax.GetLocation());
    }

    private static IEnumerable<IOperation> ResourceOperations(IOperation operation)
    {
        if (operation is IVariableDeclarationGroupOperation group)
        {
            return group.Declarations
                .SelectMany(declaration => declaration.Declarators)
                .Cast<IOperation>();
        }

        return [operation];
    }

    private static int DeconstructionLeafCount(IOperation operation)
    {
        return operation switch
        {
            ITupleOperation tuple => tuple.Elements.Sum(DeconstructionLeafCount),
            IDeclarationExpressionOperation declaration => DeconstructionLeafCount(declaration.Expression),
            IDiscardOperation => 1,
            _ => 1,
        };
    }

    private IMethodSymbol? ResolveSpeculativeMethod(ExpressionSyntax receiver, string methodName, string arguments = "")
    {
        var invocation = SyntaxFactory.ParseExpression($"({receiver}) . {methodName}({arguments})");
        var symbol = _semanticModel.GetSpeculativeSymbolInfo(
            receiver.SpanStart,
            invocation,
            SpeculativeBindingOption.BindAsExpression).Symbol;
        return symbol as IMethodSymbol;
    }

    private static IMethodSymbol? FindParameterlessMethod(ITypeSymbol? type, string name)
    {
        return FindMembers(type, name)
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == 0)
            .FirstOrDefault();
    }

    private static IPropertySymbol? FindReadableProperty(ITypeSymbol? type, string name)
    {
        return FindMembers(type, name)
            .OfType<IPropertySymbol>()
            .Where(property => property.GetMethod is not null && property.Parameters.Length == 0)
            .FirstOrDefault();
    }

    private IMethodSymbol? FindDisposalMethod(ITypeSymbol type, string name, bool isAsync)
    {
        var direct = FindParameterlessMethod(type, name);
        if (direct is not null)
        {
            return direct;
        }

        var metadataName = isAsync
            ? "System.IAsyncDisposable"
            : "System.IDisposable";
        var disposableType = _semanticModel.Compilation.GetTypeByMetadataName(metadataName);
        if (disposableType is null)
        {
            return null;
        }

        foreach (var member in disposableType.GetMembers(name).OfType<IMethodSymbol>())
        {
            if (member.Parameters.Length == 0
                && type is INamedTypeSymbol namedType
                && namedType.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
            {
                return implementation;
            }
        }

        return null;
    }

    private static IMethodSymbol? FindParameterlessContinuation(ITypeSymbol type)
    {
        var unsafeContinuation = FindMembers(type, "UnsafeOnCompleted")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 1);
        return unsafeContinuation ?? FindMembers(type, "OnCompleted")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 1);
    }

    private static IEnumerable<ISymbol> FindMembers(ITypeSymbol? type, string name)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return [];
        }

        var members = new List<ISymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (var current = namedType; current is not null; current = current.BaseType)
        {
            foreach (var member in current.OriginalDefinition.GetMembers(name)
                         .OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
            {
                if (seen.Add(member))
                {
                    members.Add(member);
                }
            }
        }

        foreach (var interfaceType in namedType.AllInterfaces.OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
        {
            foreach (var member in interfaceType.GetMembers(name)
                         .OrderBy(item => item.ToDisplayString(), StringComparer.Ordinal))
            {
                var implementation = namedType.FindImplementationForInterfaceMember(member) ?? member;
                if (seen.Add(implementation))
                {
                    members.Add(implementation);
                }
            }
        }

        return members;
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
