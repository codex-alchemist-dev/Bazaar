using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class PaneViewRegistrationTests
{
    [AvaloniaFact]
    public void A_module_view_renders_under_the_pane_title()
    {
        var registry = PaneRegistry.Compose([new TestViewModule()]);
        var pane = registry.Create(PaneRegistryTests.Descriptor("test.view") with { Title = "Test pane" });

        var view = registry.CreateView(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Equal(["Test pane", "module body"], texts);
    }

    [AvaloniaFact]
    public void A_pane_with_no_registered_view_falls_back_to_the_generic_view()
    {
        var registry = new PaneRegistry();
        var pane = registry.Create(PaneRegistryTests.Descriptor("godot.scene-tree", "{\"name\":\"Main\"}"));

        var view = registry.CreateView(pane);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.IsType<GenericPaneViewModel>(pane);
        Assert.Contains(texts, text => text is not null && text.Contains("Main", StringComparison.Ordinal));
    }

    [Fact]
    public void Registering_a_second_view_for_the_same_pane_type_throws()
    {
        var registry = new PaneRegistry();
        registry.RegisterView<TestPane>(_ => new TextBlock());

        Assert.Throws<InvalidOperationException>(() => registry.RegisterView<TestPane>(_ => new TextBlock()));
    }

    [Fact]
    public void Context_carries_a_service_a_head_added()
    {
        var service = new TestService();

        var context = new PaneModuleContext().With(service);

        Assert.Same(service, context.Get<TestService>());
        Assert.Null(new PaneModuleContext().Get<TestService>());
    }

    [Fact]
    public void Adding_a_service_keeps_the_original_context_unchanged()
    {
        var original = new PaneModuleContext(Report: _ => { });

        var extended = original.With(new TestService());

        Assert.Null(original.Get<TestService>());
        Assert.Same(original.Report, extended.Report);
    }

    private sealed class TestService;

    private sealed class TestPane(PaneDescriptor descriptor) : IPaneViewModel
    {
        public string Kind => descriptor.Kind;

        public string Title => descriptor.Title;
    }

    private sealed class TestViewModule : IPaneModule
    {
        public string Id => "test.view";

        public void Register(PaneRegistry registry, PaneModuleContext context)
        {
            registry.Register(Id, descriptor => new TestPane(descriptor));
            registry.RegisterView<TestPane>(_ => new TextBlock { Text = "module body" });
        }
    }
}
