using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Constellation;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda.Panes;
using Bazaar.Client.Vault;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class PaneModuleTests
{
    [Fact]
    public void Each_first_party_module_registers_its_own_kind()
    {
        // A null kind marks a module that registers a view only (the engine module: engine kinds are namespaced by the engine).
        var expected = new Dictionary<Type, string?>
        {
            [typeof(KanbanPaneModule)] = PaneKinds.Kanban,
            [typeof(DashboardPaneModule)] = PaneKinds.Dashboard,
            [typeof(ConstellationPaneModule)] = PaneKinds.Constellation,
            [typeof(VaultSearchPaneModule)] = PaneKinds.VaultSearch,
            [typeof(GcPaneModule)] = GcPaneViewModel.PaneKind,
            [typeof(RyuseiSchedulePaneModule)] = PaneKinds.RyuseiSchedule,
            [typeof(RoadmapPaneModule)] = PaneKinds.Roadmap,
            [typeof(WorkdayPaneModule)] = PaneKinds.Workday,
            [typeof(EnginePaneModule)] = null,
        };

        Assert.Equal(expected.Count, TestPanes.Modules.Count);
        foreach (var module in TestPanes.Modules)
        {
            var kind = expected[module.GetType()];

            var registry = new PaneRegistry(PaneViews.Instance);
            module.Register(registry, new PaneModuleContext());

            if (kind is null)
            {
                Assert.Empty(registry.Kinds);
                continue;
            }

            Assert.Equal(kind, module.Id);
            Assert.Equal([kind], registry.Kinds);
        }
    }

    [Fact]
    public void First_party_registry_keeps_the_same_kinds_in_the_same_order()
    {
        var registry = TestPanes.CreateRegistry();

        Assert.Equal(
            [
                PaneKinds.Kanban,
                PaneKinds.Dashboard,
                PaneKinds.Constellation,
                GcPaneViewModel.PaneKind,
                PaneKinds.VaultSearch,
                PaneKinds.RyuseiSchedule,
                PaneKinds.Roadmap,
                PaneKinds.Workday,
            ],
            registry.Kinds);
    }

    [Fact]
    public void Composed_list_registers_a_custom_module_alongside_first_party_panes()
    {
        var custom = new TestPaneModule("test.custom");

        var registry = PaneRegistry.Compose(PaneViews.Instance, [.. TestPanes.Modules, custom]);

        Assert.True(registry.Contains("test.custom"));
        Assert.Equal("test.custom", registry.Kinds[^1]);
        Assert.IsType<GenericPaneViewModel>(registry.Create(PaneRegistryTests.Descriptor("test.custom")));
    }

    [Fact]
    public void Composed_list_without_a_module_leaves_its_kind_out()
    {
        var modules = TestPanes.Modules.Where(module => module is not DashboardPaneModule).ToList();

        var registry = PaneRegistry.Compose(PaneViews.Instance, modules);

        Assert.False(registry.Contains(PaneKinds.Dashboard));
        Assert.Equal(
            [
                PaneKinds.Kanban,
                PaneKinds.Constellation,
                GcPaneViewModel.PaneKind,
                PaneKinds.VaultSearch,
                PaneKinds.RyuseiSchedule,
                PaneKinds.Roadmap,
                PaneKinds.Workday,
            ],
            registry.Kinds);
    }

    [Fact]
    public void Composed_list_with_a_duplicate_kind_throws()
    {
        var modules = new IPaneModule[] { new TestPaneModule("test.same"), new TestPaneModule("test.same") };

        Assert.Throws<InvalidOperationException>(() => PaneRegistry.Compose(PaneViews.Instance, modules));
    }

    private sealed class TestPaneModule(string kind) : IPaneModule
    {
        public string Id => kind;

        public void Register(PaneRegistry registry, PaneModuleContext context) =>
            registry.Register(kind, descriptor => new GenericPaneViewModel(descriptor));
    }
}
