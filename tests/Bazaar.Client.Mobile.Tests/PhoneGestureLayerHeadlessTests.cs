using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Mobile;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Mobile.Tests;

public class PhoneGestureLayerHeadlessTests
{
    private static readonly GestureOptions _fastHold = new() { HoldTime = TimeSpan.FromMilliseconds(5) };

    private sealed class RecordingHaptics : IHaptics
    {
        public List<HapticKind> Ticks { get; } = [];

        public void Tick(HapticKind kind) => Ticks.Add(kind);
    }

    private static (Window Window, PhoneShell Shell) Show(GestureOptions? options = null, IHaptics? haptics = null)
    {
        var registry = new PaneRegistry(TestPaneViews.Instance);
        foreach (var kind in new[] { "a", "b", "c" })
        {
            registry.Register(kind, descriptor => new GenericPaneViewModel(descriptor));
        }

        var shell = new PhoneShell(registry, gestures: options, haptics: haptics);
        var window = new Window { Width = 400, Height = 800, Content = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static Point InBody(PhoneShell shell, double x, double y)
    {
        // Positions are relative to the layer; the title strip sits above it, so shift by the layer's own offset.
        var origin = shell.Gestures.TranslatePoint(new Point(0, 0), TopLevel.GetTopLevel(shell)!) ?? default;
        return new Point(origin.X + x, origin.Y + y);
    }

    [AvaloniaFact]
    public void Swiping_left_far_enough_shows_the_next_page_and_swiping_right_goes_back()
    {
        var (window, shell) = Show();

        window.MouseDown(InBody(shell, 250, 200), MouseButton.Left);
        window.MouseMove(InBody(shell, 200, 202));
        window.MouseMove(InBody(shell, 120, 204));
        window.MouseUp(InBody(shell, 120, 204), MouseButton.Left);
        Assert.Equal(1, shell.SelectedPage);

        window.MouseDown(InBody(shell, 120, 200), MouseButton.Left);
        window.MouseMove(InBody(shell, 170, 200));
        window.MouseMove(InBody(shell, 260, 200));
        window.MouseUp(InBody(shell, 260, 200), MouseButton.Left);
        Assert.Equal(0, shell.SelectedPage);
    }

    [AvaloniaFact]
    public void A_swipe_that_is_too_short_leaves_the_page_and_clears_the_drag_offset()
    {
        // The flick rule reads speed from the event timestamps, which differ by a few milliseconds in a headless run, so a
        // 15 dp move would read as a fast flick. Turn the flick rule off so this test checks only the distance rule.
        var (window, shell) = Show(new GestureOptions { SwipeCommitVelocityDpPerSecond = double.PositiveInfinity });

        window.MouseDown(InBody(shell, 250, 200), MouseButton.Left);
        window.MouseMove(InBody(shell, 235, 200));
        window.MouseUp(InBody(shell, 235, 200), MouseButton.Left);

        Assert.Equal(0, shell.SelectedPage);
    }

    [AvaloniaFact]
    public void A_swipe_that_starts_in_the_edge_band_is_left_to_the_system()
    {
        var (window, shell) = Show();

        window.MouseDown(InBody(shell, 10, 200), MouseButton.Left);
        window.MouseMove(InBody(shell, 100, 200));
        window.MouseMove(InBody(shell, 250, 200));
        window.MouseUp(InBody(shell, 250, 200), MouseButton.Left);

        Assert.Equal(0, shell.SelectedPage);
    }

    [AvaloniaFact]
    public async Task Holding_still_opens_the_radial_and_releasing_on_a_sector_reports_it()
    {
        var (window, shell) = Show(_fastHold);
        var dictation = new List<bool>();
        var releases = new List<RadialRelease>();
        shell.DictationChanged += (_, on) => dictation.Add(on);
        shell.RadialReleased += (_, r) => releases.Add(r);

        window.MouseDown(InBody(shell, 200, 300), MouseButton.Left);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.Gestures.IsRadialOpen);
        Assert.Equal([true], dictation);

        // The first sector is straight up from the press point.
        window.MouseMove(InBody(shell, 200, 220));
        Assert.Equal(AndroidSettings.DefaultRadialSectors[0], shell.Gestures.Radial.HighlightedSector);
        Assert.Equal([true, false], dictation);

        window.MouseUp(InBody(shell, 200, 220), MouseButton.Left);

        Assert.False(shell.Gestures.IsRadialOpen);
        var release = Assert.Single(releases);
        Assert.Equal(RadialOutcome.OpenSector, release.Outcome);
        Assert.Equal(AndroidSettings.DefaultRadialSectors[0], release.SectorId);
    }

    [AvaloniaFact]
    public async Task Releasing_in_the_centre_places_the_transcript()
    {
        var (window, shell) = Show(_fastHold);
        var releases = new List<RadialRelease>();
        shell.RadialReleased += (_, r) => releases.Add(r);

        window.MouseDown(InBody(shell, 200, 300), MouseButton.Left);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(InBody(shell, 202, 301), MouseButton.Left);

        Assert.Equal(RadialOutcome.PlaceTranscript, Assert.Single(releases).Outcome);
    }

    [AvaloniaFact]
    public async Task Releasing_outside_every_sector_cancels()
    {
        var (window, shell) = Show(_fastHold);
        var releases = new List<RadialRelease>();
        shell.RadialReleased += (_, r) => releases.Add(r);

        window.MouseDown(InBody(shell, 200, 300), MouseButton.Left);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(InBody(shell, 200, 60));
        window.MouseUp(InBody(shell, 200, 60), MouseButton.Left);

        Assert.Equal(RadialOutcome.Cancelled, Assert.Single(releases).Outcome);
    }

    [AvaloniaFact]
    public void The_next_and_previous_routes_for_accessibility_move_the_pager_and_stop_at_the_ends()
    {
        var (_, shell) = Show();

        Assert.False(shell.GoToPrevious());
        Assert.True(shell.GoToNext());
        Assert.True(shell.GoToNext());
        Assert.False(shell.GoToNext());
        Assert.Equal(2, shell.SelectedPage);
    }

    [AvaloniaFact]
    public async Task Each_new_radial_sector_and_a_committed_swipe_play_one_tick()
    {
        var haptics = new RecordingHaptics();
        var (window, shell) = Show(_fastHold, haptics);

        window.MouseDown(InBody(shell, 200, 300), MouseButton.Left);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(InBody(shell, 200, 220));
        window.MouseMove(InBody(shell, 201, 221));
        window.MouseMove(InBody(shell, 280, 300));
        window.MouseUp(InBody(shell, 280, 300), MouseButton.Left);

        Assert.Equal([HapticKind.RadialSector, HapticKind.RadialSector], haptics.Ticks);
    }

    [AvaloniaFact]
    public void A_committed_swipe_plays_a_commit_tick()
    {
        var haptics = new RecordingHaptics();
        var (window, shell) = Show(haptics: haptics);

        window.MouseDown(InBody(shell, 250, 200), MouseButton.Left);
        window.MouseMove(InBody(shell, 200, 202));
        window.MouseMove(InBody(shell, 120, 204));
        window.MouseUp(InBody(shell, 120, 204), MouseButton.Left);

        Assert.Equal([HapticKind.SwipeCommit], haptics.Ticks);
    }

    [Fact]
    public void The_haptics_gate_follows_the_setting()
    {
        var inner = new RecordingHaptics();
        var on = true;
        var gate = new HapticsGate(inner, () => on);

        gate.Tick(HapticKind.RowCommit);
        on = false;
        gate.Tick(HapticKind.RowCommit);

        Assert.Equal([HapticKind.RowCommit], inner.Ticks);
    }
}
