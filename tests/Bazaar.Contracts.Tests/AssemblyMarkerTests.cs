namespace Bazaar.Contracts.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Marker_is_instantiable() => Assert.NotNull(new AssemblyMarker());
}
