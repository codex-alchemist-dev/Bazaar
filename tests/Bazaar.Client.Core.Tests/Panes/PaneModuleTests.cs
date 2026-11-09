using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class PaneModuleTests
{
    [Fact]
    public void Each_first_party_module_registers_its_own_kind()
    {
        var expected = new Dictionary<Type, string>
        {
            [typeof(KanbanPaneModule)] = PaneKinds.Kanban,
            [typeof(DashboardPaneModule)] = PaneKinds.Dashboard,
            [typeof(ConstellationPaneModule)] = PaneKinds.Constellation,
            [typeof(VaultSearchPaneModule)] = PaneKinds.VaultSearch,
            [typeof(GcPaneModule)] = GcPaneViewModel.PaneKind,
        };

        Assert.Equal(expected.Count, FirstPartyPanes.Modules.Count);
        foreach (var module in FirstPartyPanes.Modules)
        {
            var kind = expected[module.GetType()];
            Assert.Equal(kind, module.Id);

            var registry = new PaneRegistry();
            module.Register(registry, new PaneModuleContext());

            Assert.Equal([kind], registry.Kinds);
        }
    }

    [Fact]
    public void First_party_registry_keeps_the_same_kinds_in_the_same_order()
    {
        var registry = FirstPartyPanes.CreateRegistry();

        Assert.Equal(
            [
                PaneKinds.Kanban,
                PaneKinds.Dashboard,
                PaneKinds.Constellation,
                PaneKinds.VaultSearch,
                GcPaneViewModel.PaneKind,
            ],
            registry.Kinds);
    }

    [Fact]
    public void Composed_list_registers_a_custom_module_alongside_first_party_panes()
    {
        var custom = new TestPaneModule("test.custom");

        var registry = PaneRegistry.Compose([.. FirstPartyPanes.Modules, custom]);

        Assert.True(registry.Contains("test.custom"));
        Assert.Equal("test.custom", registry.Kinds[^1]);
        Assert.IsType<GenericPaneViewModel>(registry.Create(PaneRegistryTests.Descriptor("test.custom")));
    }

    [Fact]
    public void Composed_list_without_a_module_leaves_its_kind_out()
    {
        var modules = FirstPartyPanes.Modules.Where(module => module is not DashboardPaneModule).ToList();

        var registry = PaneRegistry.Compose(modules);

        Assert.False(registry.Contains(PaneKinds.Dashboard));
        Assert.Equal(
            [PaneKinds.Kanban, PaneKinds.Constellation, PaneKinds.VaultSearch, GcPaneViewModel.PaneKind],
            registry.Kinds);
    }

    [Fact]
    public void Composed_list_with_a_duplicate_kind_throws()
    {
        var modules = new IPaneModule[] { new TestPaneModule("test.same"), new TestPaneModule("test.same") };

        Assert.Throws<InvalidOperationException>(() => PaneRegistry.Compose(modules));
    }

    private sealed class TestPaneModule(string kind) : IPaneModule
    {
        public string Id => kind;

        public void Register(PaneRegistry registry, PaneModuleContext context) =>
            registry.Register(kind, descriptor => new GenericPaneViewModel(descriptor));
    }
}
