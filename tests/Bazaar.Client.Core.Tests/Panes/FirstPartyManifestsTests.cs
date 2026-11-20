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

    [Fact]
    public void Modules_contribute_the_navigation_commands_with_their_default_mnemonics()
    {
        var result = ClientModuleHost.Load(FirstParty(), _host);

        Assert.Equal(
            ["nav.constellation", "nav.gc.first", "nav.gc.top", "nav.ryusei.board", "nav.ryusei.roadmap"],
            result.Commands.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.All(result.Commands, c => Assert.Contains(c.Id, result.CommandOwners.Keys));
    }

    [Fact]
    public void Module_contributions_match_the_module_owned_defaults_of_the_mnemonic_engine()
    {
        var contributed = Bazaar.Ex.MnemonicAssignment.FromContributions(ClientModuleHost.Load(FirstParty(), _host).Commands);
        var shellIds = new[] { Bazaar.Ex.NavigationDefaults.UnreadCommandId, Bazaar.Ex.NavigationDefaults.SettingsCommandId };
        var moduleOwned = Bazaar.Ex.NavigationDefaults.Assignments.Where(a => !shellIds.Contains(a.CommandId));

        Assert.Equal(
            moduleOwned.OrderBy(a => a.Mnemonic, StringComparer.Ordinal),
            contributed.OrderBy(a => a.Mnemonic, StringComparer.Ordinal));
    }

    [Fact]
    public void A_command_outside_the_manifest_or_with_a_bad_mnemonic_is_refused()
    {
        var manifest = new GcPaneModule().Manifest;
        var builder = new ClientModuleBuilder(manifest);

        Assert.Throws<InvalidOperationException>(() => builder.AddCommand(new ClientCommandContribution("nav.other", "Other", "other")));
        Assert.Throws<InvalidOperationException>(() => builder.AddCommand(new ClientCommandContribution("nav.gc.first", "First", "Bad Mnemonic")));
        builder.AddCommand(new ClientCommandContribution("nav.gc.first", "First", "ngc1"));
        Assert.Throws<InvalidOperationException>(() => builder.AddCommand(new ClientCommandContribution("nav.gc.first", "First", "ngc1")));
    }
}
