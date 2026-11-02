using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Panes;
using Bazaar.Constellation.Rendering;
using Bazaar.Client.Core.Tests.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Constellation;

public class ConstellationPaneViewHeadlessTests
{
    [AvaloniaFact]
    public void Pane_renders_nodes_once_a_frame_is_applied_and_lists_them_for_keyboard_users()
    {
        var pane = new ConstellationPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Constellation, """
            {"nodes":[{"id":"a","label":"Alpha","kind":"note"},{"id":"b","label":"Beta","kind":"note"}],
             "edges":[{"from":"a","to":"b"}]}
            """));
        var view = new ConstellationPaneView(pane);
        var window = new Window { Content = PaneViews.Create(pane), Width = 800, Height = 600 };
        window.Show();

        Assert.True(view.ApplyFrame(new RenderFrame(1, 1, 2, new float[] { 0, 0, 80, 40 })));
        pane.Command(new ConstellationCommand(ConstellationCommandKind.Focus, "b"));

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(2, pane.AccessibleList.Count);
        Assert.Equal("b", pane.FocusedNodeId);
        window.Close();
    }

    [AvaloniaFact]
    public void Pane_without_a_frame_still_renders_its_chrome()
    {
        var pane = new ConstellationPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Constellation, """{"nodes":[]}"""));
        var window = new Window { Content = PaneViews.Create(pane), Width = 400, Height = 300 };
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }
}
