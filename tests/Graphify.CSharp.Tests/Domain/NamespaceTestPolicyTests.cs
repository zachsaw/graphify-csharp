using Graphify.CSharp.Domain;

namespace Graphify.CSharp.Tests.Domain;

public sealed class NamespaceTestPolicyTests
{
    [Theory]
    [InlineData("Company.Product.Tests", CallerClassification.Test)]
    [InlineData("Company.Tests.Integration", CallerClassification.Test)]
    [InlineData("Tests", CallerClassification.Test)]
    [InlineData("Company.Product.Testing", CallerClassification.Production)]
    [InlineData("Company.Product", CallerClassification.Production)]
    [InlineData(null, CallerClassification.Production)]
    public void Classifies_by_exact_namespace_segment(string? namespaceName, CallerClassification expected)
    {
        var policy = new NamespaceTestPolicy();

        Assert.Equal(expected, policy.Classify(namespaceName));
    }

    [Fact]
    public void Policy_can_use_a_repository_specific_test_segment()
    {
        var policy = new NamespaceTestPolicy("Fixtures");

        Assert.Equal(CallerClassification.Test, policy.Classify("Product.Test.Fixtures"));
        Assert.Equal(CallerClassification.Production, policy.Classify("Product.Tests"));
    }

    [Fact]
    public void Policy_rejects_a_multi_segment_pattern()
    {
        Assert.Throws<ArgumentException>(() => new NamespaceTestPolicy("Product.Tests"));
    }
}
