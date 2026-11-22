namespace Bazaar.Server.Engines.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Engines_assembly_is_loadable()
    {
        Assert.Equal("Bazaar.Server.Engines", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
