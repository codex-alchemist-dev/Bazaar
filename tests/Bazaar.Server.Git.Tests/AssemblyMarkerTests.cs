namespace Bazaar.Server.Git.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Marker_type_is_public() => Assert.NotNull(typeof(Bazaar.Server.Git.AssemblyMarker));
}
