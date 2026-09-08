using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Tests.Domain;

public sealed class SymbolIdentityTests
{
    private static readonly ProjectIdentity Project = new("src/App/App.csproj", "net10.0");

    [Fact]
    public void Canonical_key_distinguishes_overloads_and_ref_like_modifiers()
    {
        var byValue = CreateMethod(new ParameterIdentity("int"));
        var byReference = CreateMethod(new ParameterIdentity("int", ParameterModifier.Ref));
        var secondOverload = CreateMethod(new ParameterIdentity("string"));

        Assert.NotEqual(byValue.CanonicalKey, byReference.CanonicalKey);
        Assert.NotEqual(byValue.CanonicalKey, secondOverload.CanonicalKey);
        Assert.NotEqual(NodeId.ForSymbol(byValue), NodeId.ForSymbol(byReference));
    }

    [Fact]
    public void Canonical_key_includes_project_target_framework_and_nested_generic_types()
    {
        var net10 = new SymbolIdentity(
            Project,
            "Company.Product",
            [new ContainingTypeIdentity("Outer", 1), new ContainingTypeIdentity("Inner", 2)],
            SymbolKind.Method,
            "Run",
            genericArity: 1);
        var net9 = new SymbolIdentity(
            new ProjectIdentity("src/App/App.csproj", "net9.0"),
            "Company.Product",
            [new ContainingTypeIdentity("Outer", 1), new ContainingTypeIdentity("Inner", 2)],
            SymbolKind.Method,
            "Run",
            genericArity: 1);

        Assert.Contains("type=Outer`1.Inner`2", net10.CanonicalKey, StringComparison.Ordinal);
        Assert.NotEqual(net10.CanonicalKey, net9.CanonicalKey);
        Assert.NotEqual(NodeId.ForSymbol(net10), NodeId.ForSymbol(net9));
    }

    [Fact]
    public void Equivalent_symbol_inputs_have_the_same_key_and_node_id()
    {
        var first = CreateMethod(new ParameterIdentity("global::System.Collections.Generic.List< string >"));
        var second = CreateMethod(new ParameterIdentity("System.Collections.Generic.List<string>"));

        Assert.Equal(first.CanonicalKey, second.CanonicalKey);
        Assert.Equal(NodeId.ForSymbol(first), NodeId.ForSymbol(second));
    }

    [Fact]
    public void Project_identity_is_repository_relative_and_uses_forward_slashes()
    {
        var identity = ProjectIdentity.FromPath("/repo/src\\App\\App.csproj", "/repo", "net10.0");

        Assert.Equal("src/App/App.csproj", identity.RelativePath);
        Assert.Equal("project=src/App/App.csproj|tfm=net10.0", identity.Key);
    }

    private static SymbolIdentity CreateMethod(params ParameterIdentity[] parameters) => new(
        Project,
        "Company.Product",
        [new ContainingTypeIdentity("Service")],
        SymbolKind.Method,
        "Run",
        parameters: parameters);
}
