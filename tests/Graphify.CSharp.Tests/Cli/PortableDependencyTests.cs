using System.Reflection.PortableExecutable;

namespace Graphify.CSharp.Tests.Cli;

public sealed class PortableDependencyTests
{
    [Theory]
    [InlineData("Microsoft.CodeAnalysis.dll")]
    [InlineData("Microsoft.CodeAnalysis.CSharp.dll")]
    public void Compiler_dependencies_do_not_contain_platform_specific_native_code(string fileName)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, fileName));
        using var image = new PEReader(stream);

        Assert.NotNull(image.PEHeaders.CorHeader);
        Assert.Equal(0, image.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size);
        Assert.Equal(Machine.I386, image.PEHeaders.CoffHeader.Machine);
    }
}
