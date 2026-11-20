using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Engines;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Engines.Tests;

public class EnginePaneModuleTests
{
    [Fact]
    public void Module_registers_no_pane_kind()
    {
        var registry = new PaneRegistry(new StubViewFactory());

        new EnginePaneModule().Register(registry, new PaneModuleContext());

        Assert.Empty(registry.Kinds);
        Assert.Equal("engine", new EnginePaneModule().Id);
    }

    [AvaloniaFact]
    public void Registry_builds_the_engine_view_under_the_pane_title()
    {
        var registry = PaneRegistry.Compose(new StubViewFactory(), [new EnginePaneModule()]);
        var pane = new EnginePaneViewModel(Descriptor("godot.scene-tree", "{\"name\":\"Main\"}"));

        var view = registry.CreateView(pane);

        Assert.IsType<EnginePaneView>(Unwrap(view));
        Assert.Contains("Main", Texts(view));
    }

    [Fact]
    public void Engine_view_model_is_a_pane_view_model()
    {
        var pane = new EnginePaneViewModel(Descriptor("godot.scene-tree", "{}"));

        var asPane = Assert.IsAssignableFrom<IPaneViewModel>(pane);
        Assert.Equal("godot.scene-tree", asPane.Kind);
        Assert.Equal("Title", asPane.Title);
    }

    private static Control Unwrap(Control view) => view is StubPanel stub ? stub.Body : view;

    private static List<string> Texts(Control view) =>
        view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

    private static PaneDescriptor Descriptor(string kind, string json) =>
        new(kind, "Title", EngineCapability.Scenes, JsonElement.Parse(json));

    /// <summary>Wraps the body so a test can see which view the registry chose.</summary>
    private sealed class StubViewFactory : IPaneViewFactory
    {
        public Control Create(IPaneViewModel pane) => new StubPanel(new TextBlock { Text = pane.Title }, new TextBlock { Text = pane.Kind });

        public Control WithTitle(string title, Control body) => new StubPanel(new TextBlock { Text = title }, body);
    }

    private sealed class StubPanel : StackPanel
    {
        public StubPanel(Control title, Control body)
        {
            Children.Add(title);
            Body = body;
            Children.Add(body);
        }

        public Control Body { get; }
    }
}
