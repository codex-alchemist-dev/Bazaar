namespace Bazaar.Kodo.Tests;

public sealed class ModuleIdTests
{
    [Fact]
    public void ModuleIdIsKodo()
    {
        Assert.Equal("kodo", KodoAssembly.ModuleId);
    }
}
