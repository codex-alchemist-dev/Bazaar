using Bazaar.Client.Mobile;

namespace Bazaar.Client.Mobile.Tests;

public class PhoneGestureRecognizerTests
{
    private const double _width = 400;

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_long_horizontal_drag_commits_a_swipe_in_the_direction_of_travel()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);

        var started = r.Move(new(180, 302), Ms(50));
        var progress = r.Move(new(100, 305), Ms(300));
        var release = r.Up(new(100, 305), Ms(310));

        Assert.Equal(GestureEventKind.SwipeProgress, started.Kind);
        Assert.Equal(-100, progress.Dx);
        Assert.Equal(GestureEventKind.SwipeCommitted, release.Kind);
        Assert.Equal(SwipeDirection.Next, release.Direction);
    }

    [Fact]
    public void Dragging_right_goes_to_the_previous_page()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(100, 300), Ms(0), _width);
        r.Move(new(190, 300), Ms(400));

        var release = r.Up(new(190, 300), Ms(410));

        Assert.Equal(SwipeDirection.Previous, release.Direction);
    }

    [Fact]
    public void A_short_slow_swipe_springs_back()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);
        r.Move(new(180, 300), Ms(500));

        Assert.Equal(GestureEventKind.SwipeCancelled, r.Up(new(180, 300), Ms(1000)).Kind);
    }

    [Fact]
    public void A_short_fast_flick_still_commits()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);
        r.Move(new(170, 300), Ms(20));
        r.Move(new(140, 300), Ms(40));

        var release = r.Up(new(140, 300), Ms(45));

        Assert.Equal(GestureEventKind.SwipeCommitted, release.Kind);
        Assert.Equal(SwipeDirection.Next, release.Direction);
    }

    [Fact]
    public void A_flick_back_against_the_drag_does_not_commit()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);
        r.Move(new(150, 300), Ms(400));
        r.Move(new(140, 300), Ms(900));
        r.Move(new(170, 300), Ms(910));

        Assert.Equal(GestureEventKind.SwipeCancelled, r.Up(new(170, 300), Ms(915)).Kind);
    }

    [Fact]
    public void A_vertical_drag_is_a_scroll_and_never_a_swipe_or_a_hold()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);

        Assert.Equal(GestureEventKind.None, r.Move(new(205, 380), Ms(60)).Kind);
        Assert.Equal(GestureEventKind.None, r.Move(new(260, 460), Ms(120)).Kind);
        Assert.Equal(GestureEventKind.None, r.HoldElapsed().Kind);
        Assert.Equal(GestureEventKind.None, r.Up(new(260, 460), Ms(130)).Kind);
    }

    [Fact]
    public void A_press_that_stays_put_opens_the_radial_at_the_press_point()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);
        r.Move(new(203, 302), Ms(100));
        Assert.True(r.IsHoldPending);

        var hold = r.HoldElapsed();

        Assert.Equal(GestureEventKind.HoldTriggered, hold.Kind);
        Assert.Equal(new GesturePoint(200, 300), hold.Point);
        Assert.True(r.IsHolding);
    }

    [Fact]
    public void While_the_radial_is_open_moves_and_release_are_reported_as_hold_events()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);
        r.HoldElapsed();

        var move = r.Move(new(260, 240), Ms(500));
        var up = r.Up(new(260, 240), Ms(600));

        Assert.Equal(GestureEventKind.HoldMoved, move.Kind);
        Assert.Equal(new GesturePoint(260, 240), move.Point);
        Assert.Equal(GestureEventKind.HoldReleased, up.Kind);
        Assert.False(r.IsHolding);
    }

    [Fact]
    public void Moving_past_the_slop_before_the_timer_fires_cancels_the_hold()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);
        r.Move(new(230, 300), Ms(100));

        Assert.Equal(GestureEventKind.None, r.HoldElapsed().Kind);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(23)]
    [InlineData(390)]
    public void A_touch_in_an_edge_band_is_ignored_so_the_system_back_gesture_wins(double x)
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(x, 300), Ms(0), _width);

        Assert.False(r.IsHoldPending);
        Assert.Equal(GestureEventKind.None, r.Move(new(x + 200, 300), Ms(300)).Kind);
        Assert.Equal(GestureEventKind.None, r.HoldElapsed().Kind);
        Assert.Equal(GestureEventKind.None, r.Up(new(x + 200, 300), Ms(310)).Kind);
    }

    [Fact]
    public void The_edge_inset_cannot_be_set_below_its_floor()
    {
        var options = new GestureOptions { EdgeInsetDp = 2 };
        var r = new PhoneGestureRecognizer(options);

        r.Down(new(10, 300), Ms(0), _width);

        Assert.Equal(GestureOptions.EdgeInsetFloorDp, options.EdgeInsetDp);
        Assert.False(r.IsHoldPending);
    }

    [Fact]
    public void A_touch_over_selectable_text_starts_no_swipe_and_no_hold()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width, overSelectableText: true);

        Assert.Equal(GestureEventKind.None, r.HoldElapsed().Kind);
        Assert.Equal(GestureEventKind.None, r.Move(new(50, 300), Ms(200)).Kind);
    }

    [Fact]
    public void An_interrupted_swipe_commits_nothing_and_an_interrupted_hold_reports_it()
    {
        var swipe = new PhoneGestureRecognizer();
        swipe.Down(new(200, 300), Ms(0), _width);
        swipe.Move(new(60, 300), Ms(300));
        Assert.Equal(GestureEventKind.SwipeCancelled, swipe.Cancel().Kind);

        var hold = new PhoneGestureRecognizer();
        hold.Down(new(200, 300), Ms(0), _width);
        hold.HoldElapsed();
        Assert.Equal(GestureEventKind.HoldCancelled, hold.Cancel().Kind);
        Assert.Equal(GestureEventKind.None, hold.Cancel().Kind);
    }

    [Fact]
    public void A_new_touch_can_start_after_the_last_one_ends()
    {
        var r = new PhoneGestureRecognizer();
        r.Down(new(200, 300), Ms(0), _width);
        r.Up(new(200, 300), Ms(50));

        r.Down(new(200, 300), Ms(100), _width);

        Assert.True(r.IsHoldPending);
    }
}
