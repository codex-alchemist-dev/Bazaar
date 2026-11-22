using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Dashboard.Tests;

public class DashboardPaneModuleTests
{
    private const string _model = """
        {"sections":[
          {"title":"While you were away","items":[{"label":"Unread GCs","value":3}]},
          {"title":"Awaiting you","items":[{"label":"Approvals","value":"2"}]}
        ]}
        """;

    [Fact]
    public void Registers_the_dashboard_kind_and_builds_its_view_model()
    {
        var registry = new PaneRegistry(new PlainViews());

        new DashboardPaneModule().Register(registry, new PaneModuleContext());

        Assert.Equal([PaneKinds.Dashboard], registry.Kinds);
        var pane = Assert.IsType<DashboardPaneViewModel>(registry.Create(Descriptor(_model)));
        Assert.Equal(2, pane.Sections.Count);
    }

    [Fact]
    public void Module_id_is_the_dashboard_kind()
    {
        Assert.Equal(PaneKinds.Dashboard, new DashboardPaneModule().Id);
    }

    [AvaloniaFact]
    public void View_shows_each_section_title_and_its_items()
    {
        var pane = new DashboardPaneViewModel(Descriptor(_model));

        var texts = DashboardPaneView.Create(pane).GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();

        // Whole-number figures are count badges: the label, then the count as badge text.
        Assert.Equal(["While you were away", "Unread GCs", "3", "Awaiting you", "Approvals", "2"], texts);
    }

    [AvaloniaFact]
    public void Registry_view_puts_the_pane_title_above_the_dashboard_body()
    {
        var registry = new PaneRegistry(new PlainViews());
        new DashboardPaneModule().Register(registry, new PaneModuleContext());

        var view = registry.CreateView(registry.Create(Descriptor(_model)));

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Equal("Start", texts[0]);
        Assert.Contains("Unread GCs", texts);
        Assert.Contains("3", texts);
    }

    private static PaneDescriptor Descriptor(string json) =>
        new(PaneKinds.Dashboard, "Start", string.Empty, JsonElement.Parse(json));

    /// <summary>A plain view factory: the title as a text block above the body. The Client.Core factory is not referenced here.</summary>
    private sealed class PlainViews : IPaneViewFactory
    {
        public Control Create(IPaneViewModel pane) => new TextBlock { Text = pane.Kind };

        public Control WithTitle(string title, Control body) =>
            new StackPanel { Children = { new TextBlock { Text = title }, body } };
    }
}
