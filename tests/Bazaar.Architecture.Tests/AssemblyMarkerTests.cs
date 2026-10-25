namespace Bazaar.Architecture.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void Server_and_contracts_assemblies_load() =>
        Assert.NotEqual(typeof(Bazaar.Server.AssemblyMarker).Assembly, typeof(Bazaar.Contracts.AssemblyMarker).Assembly);
}
