using System.Reflection;

namespace Bazaar.Architecture.Tests;

/// <summary>L0: layering between Contracts, Client.Core, the heads and Server.</summary>
public class LayeringTests
{
    [Fact]
    public void Client_core_references_no_client_head()
    {
        var compiled = ArchitectureRules.ClientHeadReferences(ArchitectureRules.ReferencedNames(typeof(Client.Core.App).Assembly));
        var declared = ArchitectureRules.ClientHeadReferences(ArchitectureRules.ProjectReferenceNames(RepoPaths.ProjectFile("Bazaar.Client.Core")));

        Assert.Empty(compiled.Concat(declared));
    }

    [Fact]
    public void Contracts_references_no_server_assembly()
    {
        var compiled = ArchitectureRules.ServerReferences(ArchitectureRules.ReferencedNames(typeof(Contracts.AssemblyMarker).Assembly));
        var declared = ArchitectureRules.ServerReferences(ArchitectureRules.ProjectReferenceNames(RepoPaths.ProjectFile("Bazaar.Contracts")));

        Assert.Empty(compiled.Concat(declared));
    }
}
