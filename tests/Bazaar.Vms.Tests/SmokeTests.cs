namespace Bazaar.Vms.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Component_assembly_loads()
    {
        Assert.NotNull(typeof(SmokeTests).Assembly);
    }
}
