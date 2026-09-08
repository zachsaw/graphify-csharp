using System.Collections.Immutable;

namespace Graphify.CSharp.Domain;

public enum SymbolKind
{
    Namespace,
    Type,
    Method,
    Constructor,
    Property,
    Field,
    Event,
}

public enum ParameterModifier
{
    None,
    Ref,
    Out,
    In,
    This,
    Params,
}

public sealed record ContainingTypeIdentity
{
    public ContainingTypeIdentity(string name, int genericArity = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(genericArity);

        Name = name.Trim();
        GenericArity = genericArity;
    }

    public string Name { get; }

    public int GenericArity { get; }

    public string CanonicalName => GenericArity == 0 ? Name : $"{Name}`{GenericArity}";
}

public sealed record ParameterIdentity
{
    public ParameterIdentity(string typeName, ParameterModifier modifier = ParameterModifier.None)
    {
        TypeName = CanonicalText.NormalizeType(typeName);
        Modifier = modifier;
    }

    public string TypeName { get; }

    public ParameterModifier Modifier { get; }

    public string CanonicalName => $"{ModifierToken(Modifier)}{TypeName}";

    private static string ModifierToken(ParameterModifier modifier) => modifier switch
    {
        ParameterModifier.None => string.Empty,
        ParameterModifier.Ref => "ref:",
        ParameterModifier.Out => "out:",
        ParameterModifier.In => "in:",
        ParameterModifier.This => "this:",
        ParameterModifier.Params => "params:",
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, "Unknown parameter modifier."),
    };
}

public sealed class SymbolIdentity : IEquatable<SymbolIdentity>
{
    public SymbolIdentity(
        ProjectIdentity project,
        string? namespaceName,
        IEnumerable<ContainingTypeIdentity>? containingTypes,
        SymbolKind kind,
        string name,
        int genericArity = 0,
        IEnumerable<ParameterIdentity>? parameters = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Namespace = CanonicalText.NormalizeNamespace(namespaceName);
        ContainingTypes = (containingTypes ?? Array.Empty<ContainingTypeIdentity>()).ToImmutableArray();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(genericArity);

        Kind = kind;
        Name = name.Trim();
        GenericArity = genericArity;
        Parameters = (parameters ?? Array.Empty<ParameterIdentity>()).ToImmutableArray();
        CanonicalKey = BuildCanonicalKey();
    }

    public ProjectIdentity Project { get; }

    public string Namespace { get; }

    public ImmutableArray<ContainingTypeIdentity> ContainingTypes { get; }

    public SymbolKind Kind { get; }

    public string Name { get; }

    public int GenericArity { get; }

    public ImmutableArray<ParameterIdentity> Parameters { get; }

    public string CanonicalKey { get; }

    public string DisplayName
    {
        get
        {
            var typeName = string.Join('.', ContainingTypes.Select(type => type.CanonicalName));
            var prefix = string.IsNullOrEmpty(Namespace) ? typeName : Namespace + (string.IsNullOrEmpty(typeName) ? string.Empty : "." + typeName);
            var memberName = Kind == SymbolKind.Constructor && ContainingTypes.Length > 0
                ? ContainingTypes[^1].CanonicalName
                : Name;
            var qualifiedName = string.IsNullOrEmpty(prefix) ? memberName : prefix + "." + memberName;
            var genericSuffix = GenericArity == 0 ? string.Empty : $"<{GenericArity}>";
            var parameterSuffix = Parameters.Length == 0
                ? string.Empty
                : "(" + string.Join(", ", Parameters.Select(parameter => parameter.CanonicalName)) + ")";

            return qualifiedName + genericSuffix + parameterSuffix;
        }
    }

    public bool Equals(SymbolIdentity? other) => other is not null && string.Equals(CanonicalKey, other.CanonicalKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SymbolIdentity other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalKey);

    public override string ToString() => CanonicalKey;

    private string BuildCanonicalKey()
    {
        var containingTypes = string.Join('.', ContainingTypes.Select(type => type.CanonicalName));
        var parameters = string.Join(';', Parameters.Select(parameter => parameter.CanonicalName));

        return string.Join(
            '|',
            "csharp/v1",
            Project.Key,
            $"symbol={Kind.ToString().ToLowerInvariant()}",
            $"namespace={CanonicalText.Escape(Namespace)}",
            $"type={CanonicalText.Escape(containingTypes)}",
            $"name={CanonicalText.Escape(Name)}",
            $"arity={GenericArity}",
            $"params={CanonicalText.Escape(parameters)}");
    }
}
