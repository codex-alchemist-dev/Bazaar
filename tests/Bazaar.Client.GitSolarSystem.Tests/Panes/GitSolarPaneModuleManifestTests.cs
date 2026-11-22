using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

public sealed class GitSolarPaneModuleManifestTests
{
    [Fact]
    public void The_manifest_is_valid_and_declares_the_solar_system_pane_kind()
    {
        var module = new GitSolarPaneModule();

        var result = ModuleManifestValidator.Validate(module.Manifest);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => i.Message)));
        Assert.Equal(PaneKinds.GitSolarSystem, module.Manifest.Id);
        Assert.Equal([PaneKinds.GitSolarSystem], module.Manifest.PaneKinds);
    }

    [Fact]
    public void Registering_through_the_builder_records_the_declared_pane_kind()
    {
        var module = new GitSolarPaneModule();
        var builder = new ClientModuleBuilder(module.Manifest);

        module.Register(builder);

        Assert.Contains(PaneKinds.GitSolarSystem, builder.RegisteredPaneKinds);
    }
}
