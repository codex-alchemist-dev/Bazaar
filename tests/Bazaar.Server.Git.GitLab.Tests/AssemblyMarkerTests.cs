namespace Bazaar.Server.Git.GitLab.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Marker_type_is_public() => Assert.NotNull(typeof(Bazaar.Server.Git.GitLab.AssemblyMarker));
}
