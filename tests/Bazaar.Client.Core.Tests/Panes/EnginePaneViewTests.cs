using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Core.Tests.Panes;

public class EnginePaneViewTests
{
    private const string _treeKind = "godot.scene-tree";

    [Fact]
    public void Model_fields_are_listed_in_order_with_their_values()
    {
        var pane = new EnginePaneViewModel(Descriptor(_treeKind, "{\"name\":\"Main\",\"visible\":true,\"count\":3}"));

        Assert.Equal(
            [new EnginePaneField("name", "Main"), new EnginePaneField("visible", "true"), new EnginePaneField("count", "3")],
            pane.Fields);
    }

    [Fact]
    public void Non_object_model_shows_a_single_value_field()
    {
        var pane = new EnginePaneViewModel(Descriptor(_treeKind, "[1,2]"));

        Assert.Equal([new EnginePaneField("value", "[1,2]")], pane.Fields);
    }

    [Fact]
    public void Long_values_are_cut_to_the_maximum_length()
    {
        var text = new string('x', EnginePaneViewModel.MaxValueLength + 20);
        var pane = new EnginePaneViewModel(Descriptor(_treeKind, $"{{\"log\":\"{text}\"}}"));

        var value = Assert.Single(pane.Fields).Value;
        Assert.Equal(EnginePaneViewModel.MaxValueLength + 3, value.Length);
        Assert.EndsWith("...", value);
    }

    [AvaloniaFact]
    public void Generic_view_shows_title_field_names_and_values()
    {
        var view = new EnginePaneView(new EnginePaneViewModel(Descriptor(_treeKind, "{\"name\":\"Main\",\"visible\":true}")));
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();

        var texts = Texts(view);
        Assert.Contains("Title", texts);
        Assert.Contains("name", texts);
        Assert.Contains("Main", texts);
        Assert.Contains("visible", texts);
        Assert.Contains("true", texts);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }

    [AvaloniaFact]
    public void Empty_model_shows_no_fields_message()
    {
        var view = new EnginePaneView(new EnginePaneViewModel(Descriptor(_treeKind, "{}")));

        Assert.Contains("No fields", Texts(view));
    }

    [AvaloniaFact]
    public void Unknown_renderer_key_falls_back_to_generic_view()
    {
        var renderers = new Dictionary<string, EnginePaneRenderer>
        {
            ["roblox.explorer"] = _ => new TextBlock { Text = "PLUGIN" },
        };
        var view = new EnginePaneView(new EnginePaneViewModel(Descriptor(_treeKind, "{\"name\":\"Main\"}")), renderers);
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();

        var texts = Texts(view);
        Assert.Contains("name", texts);
        Assert.DoesNotContain("PLUGIN", texts);
        window.Close();
    }

    [AvaloniaFact]
    public void Known_renderer_key_replaces_the_generic_body()
    {
        var renderers = new Dictionary<string, EnginePaneRenderer>
        {
            [_treeKind] = _ => new TextBlock { Text = "PLUGIN" },
        };
        var view = new EnginePaneView(new EnginePaneViewModel(Descriptor(_treeKind, "{\"name\":\"Main\"}")), renderers);
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();

        var texts = Texts(view);
        Assert.Contains("PLUGIN", texts);
        Assert.DoesNotContain("name", texts);
        window.Close();
    }

    [AvaloniaFact]
    public void Stale_revision_is_ignored_and_the_view_keeps_the_newer_snapshot()
    {
        var pane = new EnginePaneViewModel(Descriptor(_treeKind, "{\"name\":\"New\"}", revision: 5));
        var view = new EnginePaneView(pane);
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();

        var applied = pane.Apply(Descriptor(_treeKind, "{\"name\":\"Old\"}", revision: 4));
        var sameRevision = pane.Apply(Descriptor(_treeKind, "{\"name\":\"Same\"}", revision: 5));

        Assert.False(applied);
        Assert.False(sameRevision);
        Assert.Equal(5, pane.Revision);
        var texts = Texts(view);
        Assert.Contains("New", texts);
        Assert.DoesNotContain("Old", texts);
        Assert.DoesNotContain("Same", texts);
        window.Close();
    }

    [AvaloniaFact]
    public void Newer_revision_is_applied_and_the_view_rebuilds()
    {
        var pane = new EnginePaneViewModel(Descriptor(_treeKind, "{\"name\":\"Old\"}", revision: 1));
        var view = new EnginePaneView(pane);
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();

        var applied = pane.Apply(new PaneDescriptor(_treeKind, "Renamed", EngineCapability.Scenes, Json("{\"name\":\"New\"}"), 2));

        Assert.True(applied);
        Assert.Equal(2, pane.Revision);
        Assert.Equal("Renamed", pane.Title);
        var texts = Texts(view);
        Assert.Contains("Renamed", texts);
        Assert.Contains("New", texts);
        Assert.DoesNotContain("Old", texts);
        window.Close();
    }

    [Fact]
    public void Apply_with_a_different_kind_throws()
    {
        var pane = new EnginePaneViewModel(Descriptor(_treeKind, "{}"));

        Assert.Throws<ArgumentException>(() => pane.Apply(Descriptor("roblox.explorer", "{}", revision: 1)));
    }

    private static List<string> Texts(Control view) =>
        view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

    private static PaneDescriptor Descriptor(string kind, string json, long revision = 0) =>
        new(kind, "Title", EngineCapability.Scenes, Json(json), revision);

    private static JsonElement Json(string json) => JsonElement.Parse(json);
}
