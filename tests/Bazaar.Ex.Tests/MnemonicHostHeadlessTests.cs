using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Bazaar.Client.Core.Input;
using Bazaar.Ex;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicHostHeadlessTests
{
    private sealed record Harness(Window Window, TextBox Input, MnemonicLiveRegion Region, MnemonicHost Host, List<string> Commands, List<string?> Overlays);

    private static Harness Build()
    {
        var input = new TextBox();
        var region = new MnemonicLiveRegion();
        var window = new Window
        {
            Content = new StackPanel { Orientation = Orientation.Vertical, Children = { input, region.Control } },
        };
        window.Show();

        var resolution = MnemonicResolver.Resolve(NavigationDefaults.Assignments);
        var session = MnemonicSession.TryCreate(resolution, [KeyStroke.OfChar('p')])!;
        var host = new MnemonicHost(window, session, region);
        var commands = new List<string>();
        var overlays = new List<string?>();
        host.CommandInvoked += (_, id) => commands.Add(id);
        host.OverlayChanged += (_, overlay) => overlays.Add(overlay);
        return new Harness(window, input, region, host, commands, overlays);
    }

    private static void Press(Window window, Key key, string? symbol = null) =>
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, symbol);

    [AvaloniaFact]
    public void Typing_a_navigation_mnemonic_and_Enter_runs_it_and_speaks_the_target()
    {
        var harness = Build();

        Press(harness.Window, Key.N, "n");
        Press(harness.Window, Key.G, "g");
        Press(harness.Window, Key.C, "c");
        Press(harness.Window, Key.D1, "1");
        Press(harness.Window, Key.Enter);

        Assert.Equal([NavigationDefaults.GcFirstCommandId], harness.Commands);
        Assert.Equal("GC list, first chat.", harness.Region.Control.Text);
    }

    [AvaloniaFact]
    public void Pending_mnemonic_shows_the_overlay_text_through_the_live_region()
    {
        var harness = Build();

        Press(harness.Window, Key.N, "n");
        Press(harness.Window, Key.G, "g");
        Press(harness.Window, Key.C, "c");
        Press(harness.Window, Key.D1, "1");

        Assert.Equal("Move to: GC list, first chat", harness.Overlays[^1]);
        Assert.Empty(harness.Commands);
    }

    [AvaloniaFact]
    public void I_moves_focus_into_the_first_text_input_and_Escape_releases_it()
    {
        var harness = Build();

        Press(harness.Window, Key.I, "i");
        Assert.True(harness.Input.IsFocused);

        Press(harness.Window, Key.Escape);
        Assert.Null(harness.Window.FocusManager?.GetFocusedElement());
    }

    [AvaloniaFact]
    public void Letters_typed_into_a_focused_text_input_are_not_taken_as_mnemonics()
    {
        var harness = Build();
        harness.Input.Focus();

        Press(harness.Window, Key.N, "n");
        Press(harness.Window, Key.G, "g");
        Press(harness.Window, Key.C, "c");
        Press(harness.Window, Key.D1, "1");
        Press(harness.Window, Key.Enter);

        Assert.Empty(harness.Commands);
    }

    [AvaloniaFact]
    public void Disposing_the_host_stops_it_seeing_keys()
    {
        var harness = Build();
        harness.Host.Dispose();

        Press(harness.Window, Key.N, "n");
        Press(harness.Window, Key.G, "g");
        Press(harness.Window, Key.C, "c");
        Press(harness.Window, Key.D1, "1");
        Press(harness.Window, Key.Enter);

        Assert.Empty(harness.Commands);
    }

    [AvaloniaFact]
    public void Live_region_sets_its_politeness_and_text()
    {
        var region = new MnemonicLiveRegion();

        region.Announce("Unread.", AnnouncePriority.Assertive);

        Assert.Equal("Unread.", region.Control.Text);
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Assertive, Avalonia.Automation.AutomationProperties.GetLiveSetting(region.Control));
    }
}
