using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Bazaar.Client.Calcinato.Emulation;
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.Calcinato.Avalonia.Tests;

public class TerminalControlTests
{
    private static readonly byte[] _ctrlC = [3];

    // A window, a manager with one local terminal, and the control on it. Resizes apply at once so tests need not wait.
    // The pane's clock is fake: its accessible-text rate limit only fires when a test advances time, never on a thread pool timer.
    // Tests run one at a time (see TestAppBuilder), so one shared clock is reset for each build.
    private static FakeTimeProvider _clock = new();

    private static (Window Window, TerminalControl Control, TerminalSessionManager Manager, TerminalPaneViewModel Pane, FakeHost Host) Build(double width = 800, double height = 480)
    {
        _clock = new FakeTimeProvider();
        var host = new FakeHost();
        var manager = new TerminalSessionManager(host, defaultCols: 80, defaultRows: 24);
        var pane = new TerminalPaneViewModel("Terminal", manager, _clock);
        manager.OpenLocal();
        var control = new TerminalControl(pane) { ResizeDebounce = TimeSpan.Zero };
        var window = new Window { Content = control, Width = width, Height = height };
        window.Show();
        Pump.Layout();
        control.Focus();
        return (window, control, manager, pane, host);
    }

    private static FakeSource Source(FakeHost host) => host.Sources[^1];

    private static void EnterInsert(TerminalSessionManager manager)
    {
        Pump.Run(manager.HandleKeyAsync(TerminalKey.Typed('i')));
        Assert.Equal(ModalMode.Insert, manager.Active!.Engine.Mode);
    }

    [AvaloniaFact]
    public void Control_renders_a_snapshot_with_colours_and_wide_text_without_error()
    {
        var (window, control, _, pane, host) = Build();
        Source(host).Emit("\u001b[31mred\u001b[0m 中文\r\nsecond line\u001b[7m rev\u001b[0m");
        Pump.Until(() => pane.Snapshot.Rows.Any(r => r.Text.Contains("second line")), "output rendered into the snapshot");

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal(window.ClientSize, control.Bounds.Size);
        Assert.Equal(TerminalControl.GridFor(control.Bounds.Size, TerminalControl.MeasureCell()).Rows, pane.Snapshot.Rows.Count);
    }

    [AvaloniaFact]
    public void Control_size_gives_the_expected_grid_and_resize_reaches_the_source()
    {
        var (window, control, _, _, host) = Build(width: 800, height: 480);
        CellSize cell = TerminalControl.MeasureCell();
        (int cols, int rows) = TerminalControl.GridFor(control.Bounds.Size, cell);

        Pump.Until(() => Source(host).Resizes.Count > 0, "initial resize");
        Assert.Equal((cols, rows), Source(host).Resizes[^1]);
        Assert.True(cols >= 1 && rows >= 1);

        window.Height = 300;
        Pump.Layout();
        (int cols2, int rows2) = TerminalControl.GridFor(control.Bounds.Size, cell);
        Pump.Until(() => Source(host).Resizes[^1] == (cols2, rows2), "resize after the window shrank");
        Assert.True(rows2 < rows);
    }

    [AvaloniaFact]
    public void Cell_size_comes_from_the_theme_font_and_grid_leaves_a_status_row()
    {
        var (_, control, _, _, _) = Build(width: 400, height: 200);
        CellSize cell = TerminalControl.MeasureCell();

        Assert.True(cell.Width > 0);
        Assert.True(cell.Height > 0);
        (int cols, int rows) = TerminalControl.GridFor(new Size(400, 200), cell);
        Assert.Equal((int)Math.Floor(400 / cell.Width), cols);
        Assert.Equal((int)Math.Floor(200 / cell.Height) - 1, rows);
    }

    [AvaloniaFact]
    public void Automation_peer_has_the_name_role_and_live_text_value()
    {
        var (_, control, _, _, host) = Build();
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(control));

        var peer = Assert.IsType<TerminalAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(control));
        Assert.Equal("Terminal", peer.GetName());
        Assert.Equal(AutomationControlType.Edit, peer.GetAutomationControlType());
        Assert.True(peer.IsReadOnly);

        Source(host).Emit("hello world");
        Pump.Until(() => control.Pane.Snapshot.Rows.Any(r => r.Text.Contains("hello world", StringComparison.Ordinal)), "output processed");
        _clock.Advance(TimeSpan.FromSeconds(1)); // Releases the rate-limited accessible update, which the control announces.
        Pump.Until(() => peer.Value.Contains("hello world", StringComparison.Ordinal), "accessible text includes output");
        Assert.Equal(string.Join('\n', control.Pane.Accessible.VisibleRows), peer.Value);

        var provider = peer.GetProvider<IValueProvider>();
        Assert.NotNull(provider);
        Assert.Equal(peer.Value, provider.Value);
    }

    [AvaloniaFact]
    public void Ctrl_c_reaches_the_shell_as_byte_three_in_insert_mode()
    {
        var (window, _, manager, _, host) = Build();
        EnterInsert(manager);

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        Pump.Until(() => Source(host).Writes.Any(w => w.SequenceEqual(_ctrlC)), "byte 3 written to the source");
    }

    [AvaloniaFact]
    public void Arrow_keys_are_encoded_for_the_shell_in_insert_mode()
    {
        var (window, _, manager, _, host) = Build();
        EnterInsert(manager);

        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);

        Pump.Until(() => Source(host).Writes.Any(w => Encoding.ASCII.GetString(w) == "\u001b[A" || Encoding.ASCII.GetString(w) == "\u001bOA"), "arrow up written");
    }

    [AvaloniaFact]
    public void Committed_text_input_is_sent_as_utf8()
    {
        var (_, control, manager, _, host) = Build();
        EnterInsert(manager);

        control.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Source = control, Text = "中文" });

        Pump.Until(() => Source(host).Writes.Any(w => Encoding.UTF8.GetString(w) == "中文"), "committed text written");
    }

    [AvaloniaFact]
    public void Text_input_in_normal_mode_drives_the_engine_and_is_not_sent_to_the_shell()
    {
        var (_, control, manager, _, host) = Build();

        control.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Source = control, Text = "j" });

        Pump.Until(() => manager.Active!.Engine.CursorRow >= 0, "engine handled the key");
        Assert.Empty(Source(host).Writes);
        Assert.Equal(ModalMode.Normal, manager.Active!.Engine.Mode);
    }

    [AvaloniaFact]
    public void Paste_is_sent_in_insert_mode_and_bracketed_when_the_program_asks()
    {
        var (_, control, manager, pane, host) = Build();
        EnterInsert(manager);

        control.Paste("hello");
        Pump.Until(() => Source(host).Writes.Any(w => Encoding.UTF8.GetString(w) == "hello"), "plain paste written");

        Source(host).Emit("\u001b[?2004h");
        Pump.Until(() => manager.Active!.Emulator.BracketedPaste, "bracketed paste on");

        control.Paste("pasted");
        Pump.Until(() => Source(host).Writes.Any(w => Encoding.UTF8.GetString(w) == "\u001b[200~pasted\u001b[201~"), "bracketed paste written");
        Assert.NotNull(pane.Snapshot);
    }

    [AvaloniaFact]
    public void Paste_is_refused_outside_insert_mode()
    {
        var (_, control, _, _, host) = Build();

        control.Paste("nope");

        Assert.Empty(Source(host).Writes);
    }

    [AvaloniaFact]
    public void Visual_mode_reports_its_selection_anchor_and_paints_it_in_the_selection_colour()
    {
        var (_, _, manager, pane, host) = Build();
        Source(host).Emit("abcdef");
        Pump.Until(() => pane.Snapshot.Rows.Any(r => r.Text.StartsWith("abcdef", StringComparison.Ordinal)), "text rendered");

        Pump.Run(manager.HandleKeyAsync(TerminalKey.Typed('v')));
        Pump.Run(manager.HandleKeyAsync(TerminalKey.Typed('l')));
        Pump.Run(manager.HandleKeyAsync(TerminalKey.Typed('l')));
        Pump.Until(() => pane.Snapshot.ModeName == "VISUAL" && pane.Snapshot.CursorColumn == 2, "visual mode with two steps");

        Assert.Equal(0, pane.Snapshot.SelectionAnchorColumn);
        Assert.Equal(0, pane.Snapshot.SelectionAnchorRow);

        var selected = TerminalControl.ResolveCellColors(CellStyle.Plain, TerminalPalette.Fallback, selected: true);
        Assert.Equal(TerminalPalette.Fallback.Selection, selected.Background);
        var plain = TerminalControl.ResolveCellColors(CellStyle.Plain, TerminalPalette.Fallback, selected: false);
        Assert.Equal(TerminalPalette.Fallback.Background, plain.Background);
    }

    [AvaloniaFact]
    public void Reverse_video_swaps_the_cell_colours()
    {
        var style = new CellStyle(TermColor.FromRgb(1, 2, 3), TermColor.FromRgb(4, 5, 6), CellAttributes.Reverse);

        var colours = TerminalControl.ResolveCellColors(style, TerminalPalette.Fallback, selected: false);

        Assert.Equal(Color.FromRgb(4, 5, 6), colours.Foreground);
        Assert.Equal(Color.FromRgb(1, 2, 3), colours.Background);
    }

    [Fact]
    public void Wide_characters_take_two_columns_and_narrow_ones_one()
    {
        var row = new TerminalRow([new TerminalCellRun("中a", CellStyle.Plain)]);

        var clusters = TerminalControl.Clusters(row).ToList();

        Assert.Equal([0, 2], clusters.Select(c => c.Column).ToArray());
        Assert.Equal([2, 1], clusters.Select(c => c.Width).ToArray());
    }

    [AvaloniaFact]
    public void Wide_characters_from_the_emulator_occupy_two_cells_in_the_snapshot()
    {
        var (_, _, _, pane, host) = Build();
        Source(host).Emit("中a");
        Pump.Until(() => pane.Snapshot.Rows.Any(r => r.Text.StartsWith("中a", StringComparison.Ordinal)), "wide text rendered");

        TerminalRow row = pane.Snapshot.Rows.First(r => r.Text.StartsWith("中a", StringComparison.Ordinal));
        var clusters = TerminalControl.Clusters(row).ToList();
        Assert.Equal("中", clusters[0].Text);
        Assert.Equal(2, clusters[0].Width);
        Assert.Equal(2, clusters[1].Column);
    }

    [AvaloniaFact]
    public void Pane_views_register_the_control_for_the_terminal_pane_kind()
    {
        var host = new FakeHost();
        var manager = new TerminalSessionManager(host);
        var context = new PaneModuleContext().With(manager);
        var registry = PaneRegistry.Compose(new PlainPaneViewFactory(), [new TerminalPaneModule()], context);
        TerminalPaneViews.Register(registry);
        var descriptor = new PaneDescriptor(PaneKinds.Terminal, "Terminal", string.Empty, JsonElement.Parse("{}"));
        var pane = Assert.IsType<TerminalPaneViewModel>(registry.Create(descriptor));

        var window = new Window { Content = registry.CreateView(pane), Width = 300, Height = 200 };
        window.Show();

        Assert.Single(window.GetVisualDescendants().OfType<TerminalControl>());
    }

    [AvaloniaFact]
    public void Control_without_a_session_renders_the_empty_state_and_accepts_no_input()
    {
        var host = new FakeHost();
        var manager = new TerminalSessionManager(host);
        var pane = new TerminalPaneViewModel("Terminal", manager, _clock);
        var control = new TerminalControl(pane) { ResizeDebounce = TimeSpan.Zero };
        var window = new Window { Content = control, Width = 300, Height = 200 };
        window.Show();
        control.Focus();

        using var frame = window.CaptureRenderedFrame();
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        Assert.NotNull(frame);
        Assert.Empty(host.Sources);
    }
}
