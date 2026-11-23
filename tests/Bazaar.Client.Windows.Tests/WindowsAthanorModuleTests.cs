using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Modules;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Windows;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Windows.Tests;

public sealed class WindowsAthanorModuleTests
{
    [Fact]
    public void Windows_head_registers_the_athanor_pane_kind()
    {
        var registry = FirstPartyPanes.CreateRegistry([new AthanorPaneModule()], features: Program.CreateFeatures(tiles: null));

        Assert.True(registry.Contains(AthanorPaneKinds.Editor));
    }

    [Fact]
    public void Athanor_module_loads_through_the_client_module_host()
    {
        var module = new AthanorPaneModule();

        var host = ClientModuleHost.Load([module], ClientHostFacts.Current(), _ => { });

        Assert.Contains(host.Loaded, m => m.Id == AthanorClientModule.ModuleId);
        Assert.Single(host.Only<IPaneModule>([module]));
    }
}
