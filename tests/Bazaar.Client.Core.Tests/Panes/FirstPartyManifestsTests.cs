using Bazaar.Client.Constellation;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.Modules;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Vault;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Core.Tests.Panes;

public class FirstPartyManifestsTests
{
    private static readonly HostFacts _host = new("linux", "x64", new HashSet<string>(StringComparer.Ordinal));

    private static IClientModule[] FirstParty()
    {
        IPaneModule[] panes =
        [
            RyuseiFudaPanes.Board,
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new VaultSearchPaneModule(),
            .. RyuseiFudaPanes.Modules,
            new EnginePaneModule(),
        ];
        return [.. panes.Cast<IClientModule>()];
    }

    [Fact]
    public void Every_first_party_module_loads_through_the_module_host()
    {
        var modules = FirstParty();

        var result = ClientModuleHost.Load(modules, _host);

        Assert.Empty(result.Skipped);
        Assert.Equal(modules.Length, result.Loaded.Count);
    }

    [Fact]
    public void Each_module_registers_exactly_the_pane_kinds_its_manifest_declares()
    {
        foreach (var module in FirstParty())
        {
            var builder = new ClientModuleBuilder(module.Manifest);

            module.Register(builder);

            Assert.Equal(module.Manifest.PaneKinds.Order(StringComparer.Ordinal), builder.RegisteredPaneKinds.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void A_pane_module_registers_its_own_id_as_its_pane_kind()
    {
        foreach (var module in FirstParty().Where(m => m.Manifest.PaneKinds.Count > 0))
        {
            Assert.Equal(((IPaneModule)module).Id, Assert.Single(module.Manifest.PaneKinds));
            Assert.Equal(module.Manifest.Id, module.Manifest.PaneKinds[0]);
        }
    }

    [Fact]
    public void Manifests_are_in_process_compiled_and_run_on_every_client_host()
    {
        foreach (var module in FirstParty())
        {
            Assert.Equal(ModuleIsolation.InProcessCompiled, module.Manifest.Isolation);
            Assert.Equal(["android", "linux", "windows"], module.Manifest.Host.OperatingSystems);
        }
    }
}
