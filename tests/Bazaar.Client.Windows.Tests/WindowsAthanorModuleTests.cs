using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Windows;

namespace Bazaar.Client.Windows.Tests;

public sealed class WindowsAthanorModuleTests
{
    [Fact]
    public void Windows_head_registers_the_athanor_pane_kind()
    {
        var registry = Program.CreatePaneRegistry(tiles: null, report: null);

        Assert.True(registry.Contains(AthanorPaneKinds.Editor));
    }

    [Fact]
    public void Windows_head_lists_the_athanor_client_module()
    {
        Assert.Contains(Program.ClientModules(), m => m.Manifest.Id == AthanorClientModule.ModuleId);
    }
}
