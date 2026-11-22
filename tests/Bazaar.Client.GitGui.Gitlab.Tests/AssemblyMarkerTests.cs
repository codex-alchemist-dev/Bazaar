namespace Bazaar.Client.GitGui.Gitlab.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Marker_type_is_public() => Assert.NotNull(typeof(Bazaar.Client.GitGui.Gitlab.AssemblyMarker));
}
