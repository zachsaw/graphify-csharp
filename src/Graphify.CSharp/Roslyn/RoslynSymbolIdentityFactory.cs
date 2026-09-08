using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;
using DomainSymbolKind = Graphify.CSharp.Domain.SymbolKind;

namespace Graphify.CSharp.Roslyn;

public sealed class RoslynSymbolIdentityFactory
{
    private static readonly SymbolDisplayFormat TypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public SymbolIdentity Create(ISymbol symbol, ProjectIdentity project)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(project);

        return symbol switch
        {
            INamedTypeSymbol type => CreateType(type, project),
            IMethodSymbol method => CreateMethod(method, project),
            IPropertySymbol property => CreateProperty(property, project),
            IFieldSymbol field => CreateSimple(field, project, DomainSymbolKind.Field),
            IEventSymbol @event => CreateSimple(@event, project, DomainSymbolKind.Event),
            INamespaceSymbol @namespace => CreateNamespace(@namespace, project),
            _ => throw new ArgumentException($"Unsupported declaration symbol '{symbol.Kind}'.", nameof(symbol)),
        };
    }

    private static SymbolIdentity CreateType(INamedTypeSymbol type, ProjectIdentity project) => new(
        project,
        type.ContainingNamespace?.ToDisplayString(),
        ContainingTypes(type.ContainingType),
        DomainSymbolKind.Type,
        type.Name,
        type.Arity);

    private static SymbolIdentity CreateMethod(IMethodSymbol method, ProjectIdentity project)
    {
        var kind = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            ? DomainSymbolKind.Constructor
            : DomainSymbolKind.Method;
        var name = method.MethodKind == MethodKind.StaticConstructor
            ? ".cctor"
            : kind == DomainSymbolKind.Constructor ? ".ctor" : method.Name;

        return new SymbolIdentity(
            project,
            NamespaceOf(method),
            ContainingTypes(method.ContainingType),
            kind,
            name,
            method.Arity,
            method.Parameters.Select(parameter => new ParameterIdentity(TypeName(parameter.Type), Modifier(parameter))),
            returnTypeName: method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                ? null
                : TypeName(method.ReturnType));
    }

    private static SymbolIdentity CreateProperty(IPropertySymbol property, ProjectIdentity project) => new(
        project,
        NamespaceOf(property),
        ContainingTypes(property.ContainingType),
        DomainSymbolKind.Property,
        property.Name,
        parameters: property.Parameters.Select(parameter => new ParameterIdentity(TypeName(parameter.Type), Modifier(parameter))));

    private static SymbolIdentity CreateSimple(ISymbol symbol, ProjectIdentity project, DomainSymbolKind kind) => new(
        project,
        NamespaceOf(symbol),
        ContainingTypes(symbol.ContainingType),
        kind,
        symbol.Name);

    private static SymbolIdentity CreateNamespace(INamespaceSymbol @namespace, ProjectIdentity project)
    {
        var fullName = @namespace.ToDisplayString();
        var lastSeparator = fullName.LastIndexOf('.');
        var parent = lastSeparator < 0 ? string.Empty : fullName[..lastSeparator];
        var name = lastSeparator < 0 ? fullName : fullName[(lastSeparator + 1)..];
        return new SymbolIdentity(project, parent, null, DomainSymbolKind.Namespace, name);
    }

    private static IEnumerable<ContainingTypeIdentity> ContainingTypes(INamedTypeSymbol? containingType)
    {
        var types = new Stack<ContainingTypeIdentity>();
        for (var current = containingType; current is not null; current = current.ContainingType)
        {
            types.Push(new ContainingTypeIdentity(current.Name, current.Arity));
        }

        return types;
    }

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeDisplayFormat);

    private static string? NamespaceOf(ISymbol symbol) =>
        symbol.ContainingNamespace is null || symbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : symbol.ContainingNamespace.ToDisplayString();

    private static ParameterModifier Modifier(IParameterSymbol parameter)
    {
        if (parameter.IsThis)
        {
            return ParameterModifier.This;
        }

        if (parameter.IsParams)
        {
            return ParameterModifier.Params;
        }

        return parameter.RefKind switch
        {
            RefKind.Ref => ParameterModifier.Ref,
            RefKind.Out => ParameterModifier.Out,
            RefKind.In => ParameterModifier.In,
            _ => ParameterModifier.None,
        };
    }
}
