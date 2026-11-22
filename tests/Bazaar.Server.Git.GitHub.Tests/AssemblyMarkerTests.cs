namespace Bazaar.Server.Git.GitHub.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Marker_type_is_public() => Assert.NotNull(typeof(Bazaar.Server.Git.GitHub.AssemblyMarker));
}
