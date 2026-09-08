using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;
using DomainSymbolKind = Graphify.CSharp.Domain.SymbolKind;

namespace Graphify.CSharp.Roslyn;

public sealed class RoslynSymbolIdentityFactory
{
    private static readonly SymbolDisplayFormat TypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public SymbolIdentity Create(ISymbol symbol, ProjectIdentity project, string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(project);

        return symbol switch
        {
            INamedTypeSymbol type => CreateType(type, project, repositoryRoot),
            IMethodSymbol method => CreateMethod(method, project, repositoryRoot),
            IPropertySymbol property => CreateProperty(property, project, repositoryRoot),
            IFieldSymbol field => CreateSimple(field, project, DomainSymbolKind.Field, repositoryRoot),
            IEventSymbol @event => CreateSimple(@event, project, DomainSymbolKind.Event, repositoryRoot),
            IParameterSymbol parameter => CreateParameter(parameter, project, repositoryRoot),
            ILocalSymbol local => CreateScoped(local, project, DomainSymbolKind.Local, repositoryRoot),
            IRangeVariableSymbol rangeVariable => CreateScoped(rangeVariable, project, DomainSymbolKind.RangeVariable, repositoryRoot),
            ITypeParameterSymbol typeParameter => CreateTypeParameter(typeParameter, project, repositoryRoot),
            ILabelSymbol label => CreateScoped(label, project, DomainSymbolKind.Label, repositoryRoot),
            IAliasSymbol alias => CreateScoped(alias, project, DomainSymbolKind.Alias, repositoryRoot),
            INamespaceSymbol @namespace => CreateNamespace(@namespace, project),
            _ => throw new ArgumentException($"Unsupported declaration symbol '{symbol.Kind}'.", nameof(symbol)),
        };
    }

    private static SymbolIdentity CreateType(
        INamedTypeSymbol type,
        ProjectIdentity project,
        string? repositoryRoot) => new(
        project,
        type.ContainingNamespace?.ToDisplayString(),
        ContainingTypes(type.ContainingType, repositoryRoot),
        DomainSymbolKind.Type,
        type.Name,
        type.Arity,
        declarationDiscriminator: type.IsFileLocal ? SourceDiscriminator(type, repositoryRoot) : null);

    private static SymbolIdentity CreateMethod(
        IMethodSymbol method,
        ProjectIdentity project,
        string? repositoryRoot)
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
            ContainingTypes(method.ContainingType, repositoryRoot),
            kind,
            name,
            method.Arity,
            method.Parameters.Select(parameter => new ParameterIdentity(TypeName(parameter.Type), Modifier(parameter))),
            returnTypeName: method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                ? null
                : TypeName(method.ReturnType),
            containingMemberPath: ContainingMemberPath(method.ContainingSymbol));
    }

    private static SymbolIdentity CreateProperty(
        IPropertySymbol property,
        ProjectIdentity project,
        string? repositoryRoot) => new(
        project,
        NamespaceOf(property),
        ContainingTypes(property.ContainingType, repositoryRoot),
        DomainSymbolKind.Property,
        property.Name,
        parameters: property.Parameters.Select(parameter => new ParameterIdentity(TypeName(parameter.Type), Modifier(parameter))),
        returnTypeName: TypeName(property.Type));

    private static SymbolIdentity CreateSimple(
        ISymbol symbol,
        ProjectIdentity project,
        DomainSymbolKind kind,
        string? repositoryRoot) => new(
        project,
        NamespaceOf(symbol),
        ContainingTypes(ContainingTypeOf(symbol), repositoryRoot),
        kind,
        symbol.Name);

    private static SymbolIdentity CreateParameter(
        IParameterSymbol parameter,
        ProjectIdentity project,
        string? repositoryRoot) => new(
        project,
        NamespaceOf(parameter),
        ContainingTypes(ContainingTypeOf(parameter.ContainingSymbol), repositoryRoot),
        DomainSymbolKind.Parameter,
        parameter.Name,
        containingMemberPath: ContainingMemberPath(parameter.ContainingSymbol),
        declarationDiscriminator: ScopedDiscriminator(parameter, repositoryRoot, $"ordinal={parameter.Ordinal}"));

    private static SymbolIdentity CreateTypeParameter(
        ITypeParameterSymbol typeParameter,
        ProjectIdentity project,
        string? repositoryRoot) => new(
        project,
        NamespaceOf(typeParameter),
        ContainingTypes(typeParameter.DeclaringType ?? typeParameter.DeclaringMethod?.ContainingType, repositoryRoot),
        DomainSymbolKind.TypeParameter,
        typeParameter.Name,
        containingMemberPath: typeParameter.DeclaringMethod is null
            ? null
            : ContainingMemberPath(typeParameter.DeclaringMethod),
        declarationDiscriminator: ScopedDiscriminator(typeParameter, repositoryRoot, $"ordinal={typeParameter.Ordinal}"));

    private static SymbolIdentity CreateScoped(
        ISymbol symbol,
        ProjectIdentity project,
        DomainSymbolKind kind,
        string? repositoryRoot) => new(
            project,
            NamespaceOf(symbol),
            ContainingTypes(ContainingTypeOf(symbol), repositoryRoot),
            kind,
            symbol.Name,
            containingMemberPath: ContainingMemberPath(symbol.ContainingSymbol),
            declarationDiscriminator: SourceDiscriminator(symbol, repositoryRoot));

    private static SymbolIdentity CreateNamespace(INamespaceSymbol @namespace, ProjectIdentity project)
    {
        var fullName = @namespace.ToDisplayString();
        var lastSeparator = fullName.LastIndexOf('.');
        var parent = lastSeparator < 0 ? string.Empty : fullName[..lastSeparator];
        var name = lastSeparator < 0 ? fullName : fullName[(lastSeparator + 1)..];
        return new SymbolIdentity(project, parent, null, DomainSymbolKind.Namespace, name);
    }

    private static IEnumerable<ContainingTypeIdentity> ContainingTypes(
        INamedTypeSymbol? containingType,
        string? repositoryRoot)
    {
        var types = new Stack<ContainingTypeIdentity>();
        for (var current = containingType; current is not null; current = current.ContainingType)
        {
            types.Push(new ContainingTypeIdentity(
                current.Name,
                current.Arity,
                current.IsFileLocal ? SourceDiscriminator(current, repositoryRoot) : null));
        }

        return types;
    }

    private static INamedTypeSymbol? ContainingTypeOf(ISymbol? symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamedTypeSymbol type)
            {
                return type;
            }
        }

        return null;
    }

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeDisplayFormat);

    private static IEnumerable<string> ContainingMemberPath(ISymbol? containingSymbol)
    {
        var path = new Stack<string>();
        for (var current = containingSymbol; current is not null; current = current.ContainingSymbol)
        {
            switch (current)
            {
                case IMethodSymbol method:
                    path.Push(MemberSignature(method));
                    break;
                case IPropertySymbol property:
                    path.Push(PropertySignature(property));
                    break;
                case IEventSymbol @event:
                    path.Push($"event:{@event.Name}:{TypeName(@event.Type)}");
                    break;
                case IFieldSymbol field:
                    path.Push($"field:{field.Name}:{TypeName(field.Type)}");
                    break;
                case INamedTypeSymbol:
                case INamespaceSymbol:
                    return path;
            }
        }

        return path;
    }

    private static string PropertySignature(IPropertySymbol property)
    {
        var parameters = string.Join(
            ",",
            property.Parameters.Select(parameter => new ParameterIdentity(TypeName(parameter.Type), Modifier(parameter)).CanonicalName));
        return $"property:{property.Name}({parameters}):{TypeName(property.Type)}";
    }

    private static string MemberSignature(IMethodSymbol method)
    {
        var parameters = string.Join(
            ",",
            method.Parameters.Select(parameter => new ParameterIdentity(TypeName(parameter.Type), Modifier(parameter)).CanonicalName));
        var returnType = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            ? string.Empty
            : TypeName(method.ReturnType);
        return $"{method.MethodKind}:{method.Name}`{method.Arity}({parameters}):{returnType}";
    }

    private static string? NamespaceOf(ISymbol symbol) =>
        NamespaceSymbolOf(symbol) is not { IsGlobalNamespace: false } @namespace
            ? null
            : @namespace.ToDisplayString();

    private static INamespaceSymbol? NamespaceSymbolOf(ISymbol symbol)
    {
        for (var current = symbol is INamespaceSymbol ? symbol : symbol.ContainingSymbol;
             current is not null;
             current = current.ContainingSymbol)
        {
            if (current is INamespaceSymbol @namespace)
            {
                return @namespace;
            }
        }

        return null;
    }

    private static string? SourceDiscriminator(ISymbol symbol, string? repositoryRoot)
    {
        var location = symbol.Locations.FirstOrDefault(item => item.IsInSource && item.SourceTree is not null);
        if (location is null)
        {
            return null;
        }

        var path = location.SourceTree!.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"span={location.SourceSpan.Start}:{location.SourceSpan.Length}";
        }

        var normalizedPath = repositoryRoot is null
            ? path.Replace('\\', '/')
            : ProjectIdentity.FromPath(path, repositoryRoot).RelativePath;
        return $"source={normalizedPath}@{location.SourceSpan.Start}:{location.SourceSpan.Length}";
    }

    private static string ScopedDiscriminator(ISymbol symbol, string? repositoryRoot, string ordinal)
    {
        var source = SourceDiscriminator(symbol, repositoryRoot);
        return source is null ? ordinal : $"{source};{ordinal}";
    }

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
