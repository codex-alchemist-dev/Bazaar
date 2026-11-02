using System.Text.Json;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class GenericPaneViewModelTests
{
    private static readonly string[] _expectedTreeLines =
    [
        "{",
        "  name: Main",
        "  children:",
        "    [0]:",
        "      name: Player",
        "      visible: true",
    ];

    [Fact]
    public void Flattens_nested_objects_and_arrays_into_indented_lines()
    {
        var pane = new GenericPaneViewModel(PaneRegistryTests.Descriptor(
            "godot.scene-tree",
            "{\"name\":\"Main\",\"children\":[{\"name\":\"Player\",\"visible\":true}]}"));

        Assert.Equal(_expectedTreeLines, pane.Lines);
    }

    [Fact]
    public void Caps_output_at_max_lines()
    {
        var items = string.Join(",", Enumerable.Range(0, GenericPaneViewModel.MaxLines + 50).Select(i => i));
        var pane = new GenericPaneViewModel(PaneRegistryTests.Descriptor("x.y", $"[{items}]"));

        Assert.Equal(GenericPaneViewModel.MaxLines + 1, pane.Lines.Count);
        Assert.Equal("...", pane.Lines[^1]);
    }

    [Fact]
    public void Keeps_kind_and_title()
    {
        var descriptor = new Bazaar.Contracts.Panes.PaneDescriptor("roblox.explorer", "Explorer", "studio", JsonElement.Parse("1"));

        var pane = new GenericPaneViewModel(descriptor);

        Assert.Equal("roblox.explorer", pane.Kind);
        Assert.Equal("Explorer", pane.Title);
        Assert.Equal(["1"], pane.Lines);
    }
}
