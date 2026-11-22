namespace Bazaar.Client.GitGui.Github.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Marker_type_is_public() => Assert.NotNull(typeof(Bazaar.Client.GitGui.Github.AssemblyMarker));
}
