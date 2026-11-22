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

    /// <summary>A client feature module (doc 40, step 7) references Client.Core only: no head and no other feature module.</summary>
    [Theory]
    [InlineData("Bazaar.Client.RyuseiFuda")]
    [InlineData("Bazaar.Client.Views")]
    [InlineData("Bazaar.Client.PullRequests.Abstractions")]
    public void Client_feature_module_references_only_client_core(string project)
    {
        var declared = ArchitectureRules.ClientHeadReferences(ArchitectureRules.ProjectReferenceNames(RepoPaths.ProjectFile(project)));

        // A feature module may use the pure shared libraries it names here (doc 41b section 2c). Each edge is listed, so a new one is a decision.
        var allowed = project switch
        {
            "Bazaar.Client.Views" => new[] { "Bazaar.Client.Theme", "Bazaar.Client.Navigation.Abstractions", "Bazaar.Client.Preferences.Abstractions" },
            // RyuseiFuda renders its panes from Rubedo view documents through the mapper (doc 41c).
            "Bazaar.Client.RyuseiFuda" => new[] { "Bazaar.Client.Views" },
            _ => [],
        };

        Assert.Empty(declared.Except(allowed));
    }

    /// <summary>
    /// Feature modules take their pane contracts from Bazaar.Client.Panes.Abstractions and never reference Client.Core, so a
    /// module can be built and tested without the shared UI. A project that does not exist yet is skipped.
    /// </summary>
    [Theory]
    [InlineData("Bazaar.Client.RyuseiFuda")]
    [InlineData("Bazaar.Client.Chat")]
    [InlineData("Bazaar.Client.Constellation")]
    [InlineData("Bazaar.Client.Dashboard")]
    [InlineData("Bazaar.Client.Vault")]
    [InlineData("Bazaar.Client.Engines")]
    [InlineData("Bazaar.Client.Theme")]
    [InlineData("Bazaar.Client.Commands")]
    [InlineData("Bazaar.Client.Input")]
    [InlineData("Bazaar.Client.Preload")]
    [InlineData("Bazaar.Client.Launcher")]
    [InlineData("Bazaar.Client.Modules")]
    [InlineData("Bazaar.Client.Gc")]
    [InlineData("Bazaar.Client.Souls")]
    [InlineData("Bazaar.Client.Health")]
    [InlineData("Bazaar.Client.Twin")]
    [InlineData("Bazaar.Client.Archive")]
    [InlineData("Bazaar.Client.CompanyMemory")]
    [InlineData("Bazaar.Client.Hive")]
    [InlineData("Bazaar.Client.Mobile")]
    [InlineData("Bazaar.Client.Push")]
    [InlineData("Bazaar.Client.Navigation")]
    [InlineData("Bazaar.Client.Preferences")]
    [InlineData("Bazaar.Client.Updates")]
    public void Client_feature_module_does_not_reference_client_core(string project)
    {
        Assert.SkipUnless(File.Exists(RepoPaths.ProjectFile(project)), $"{project} does not exist yet.");

        var declared = ArchitectureRules.ProjectReferenceNames(RepoPaths.ProjectFile(project));

        Assert.DoesNotContain("Bazaar.Client.Core", declared);
    }

    [Fact]
    public void Contracts_references_no_server_assembly()
    {
        var compiled = ArchitectureRules.ServerReferences(ArchitectureRules.ReferencedNames(typeof(Contracts.AssemblyMarker).Assembly));
        var declared = ArchitectureRules.ServerReferences(ArchitectureRules.ProjectReferenceNames(RepoPaths.ProjectFile("Bazaar.Contracts")));

        Assert.Empty(compiled.Concat(declared));
    }
}
