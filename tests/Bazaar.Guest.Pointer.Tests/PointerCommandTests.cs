namespace Bazaar.Guest.Pointer.Tests;

public class PointerCommandParserTests
{
    private static PointerCommand Ok(params string[] args)
    {
        Assert.True(PointerCommandParser.TryParse(args, out PointerCommand? c, out string error), error);
        return c!;
    }

    private static void Rejected(params string[] args)
    {
        Assert.False(PointerCommandParser.TryParse(args, out PointerCommand? c, out string error));
        Assert.Null(c);
        Assert.NotEmpty(error);
        Assert.DoesNotContain('\n', error);
    }

    [Fact]
    public void Move_parses()
    {
        Assert.Equal(new PointerCommand(PointerVerb.Move, 10, 20, 1280, 720), Ok("move", "10", "20", "1280", "720"));
    }

    [Fact]
    public void Click_defaults_to_one_click_and_takes_a_count()
    {
        Assert.Equal(new PointerCommand(PointerVerb.Click, 1, 2, 800, 600, PointerButtonName.Right, 1), Ok("click", "1", "2", "800", "600", "right"));
        Assert.Equal(new PointerCommand(PointerVerb.Click, 1, 2, 800, 600, PointerButtonName.Middle, 3), Ok("click", "1", "2", "800", "600", "middle", "3"));
    }

    [Fact]
    public void Down_and_up_take_a_button()
    {
        Assert.Equal(PointerVerb.Down, Ok("down", "0", "0", "10", "10", "left").Verb);
        Assert.Equal(PointerVerb.Up, Ok("up", "9", "9", "10", "10", "left").Verb);
    }

    [Fact]
    public void Scroll_takes_signed_steps()
    {
        PointerCommand c = Ok("scroll", "5", "6", "100", "100", "-2", "3");
        Assert.Equal((PointerVerb.Scroll, -2, 3), (c.Verb, c.DeltaX, c.DeltaY));
    }

    [Fact]
    public void Button_codes_are_the_linux_evdev_codes()
    {
        Assert.Equal(0x110u, Ok("click", "0", "0", "9", "9", "left").ButtonCode);
        Assert.Equal(0x111u, Ok("click", "0", "0", "9", "9", "right").ButtonCode);
        Assert.Equal(0x112u, Ok("click", "0", "0", "9", "9", "middle").ButtonCode);
    }

    [Fact]
    public void Nothing_or_an_unknown_command_is_rejected()
    {
        Rejected();
        Rejected("drag", "1", "1", "10", "10");
        Rejected("MOVE", "1", "1", "10", "10");
        Rejected("--help");
    }

    [Theory]
    [InlineData("1", "1", "10")]
    [InlineData("1", "1", "10", "10", "extra")]
    public void Move_with_the_wrong_argument_count_is_rejected(params string[] tail) => Rejected(["move", .. tail]);

    [Theory]
    [InlineData("1.5")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("0x10")]
    [InlineData("1e2")]
    [InlineData("1,000")]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("٣")]
    [InlineData("99999999999")]
    public void Coordinates_must_be_plain_non_negative_integers(string bad)
    {
        Rejected("move", bad, "1", "100", "100");
        Rejected("move", "1", bad, "100", "100");
        Rejected("move", "1", "1", bad, "100");
        Rejected("move", "1", "1", "100", bad);
    }

    [Fact]
    public void Extent_and_point_ranges_are_enforced()
    {
        Rejected("move", "0", "0", "0", "10");
        Rejected("move", "0", "0", "10", "0");
        Rejected("move", "0", "0", "8193", "10");
        Ok("move", "0", "0", "8192", "8192");
        Rejected("move", "10", "0", "10", "10");
        Rejected("move", "0", "10", "10", "10");
        Ok("move", "9", "9", "10", "10");
        Rejected("move", "8192", "0", "9000", "10");
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("primary")]
    [InlineData("")]
    [InlineData("1")]
    public void Unknown_buttons_are_rejected(string button)
    {
        Rejected("click", "1", "1", "10", "10", button);
        Rejected("down", "1", "1", "10", "10", button);
        Rejected("up", "1", "1", "10", "10", button);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("two")]
    public void Click_counts_outside_one_to_three_are_rejected(string count) => Rejected("click", "1", "1", "10", "10", "left", count);

    [Fact]
    public void Down_and_up_do_not_take_a_count()
    {
        Rejected("down", "1", "1", "10", "10", "left", "1");
        Rejected("up", "1", "1", "10", "10", "left", "1");
    }

    [Theory]
    [InlineData("31", "0")]
    [InlineData("0", "-31")]
    [InlineData("0", "0")]
    [InlineData("+1", "0")]
    [InlineData("1.0", "0")]
    [InlineData("x", "1")]
    public void Scroll_steps_are_bounded_non_zero_integers(string dx, string dy) => Rejected("scroll", "1", "1", "10", "10", dx, dy);

    [Fact]
    public void Scroll_needs_both_steps()
    {
        Rejected("scroll", "1", "1", "10", "10", "1");
        Rejected("scroll", "1", "1", "10", "10", "1", "1", "1");
        Ok("scroll", "1", "1", "10", "10", "30", "-30");
    }
}

public class PointerPlannerTests
{
    private static string Hex(PlannedWrite w) => Convert.ToHexString(w.Bytes);

    [Fact]
    public void Move_is_an_absolute_motion_and_a_frame()
    {
        IReadOnlyList<PlannedWrite> plan = PointerPlanner.Plan(new PointerCommand(PointerVerb.Move, 100, 200, 1280, 720), 5, 1000);
        PlannedWrite w = Assert.Single(plan);
        Assert.Equal(
            "05000000" + "01001C00" + "E8030000" + "64000000" + "C8000000" + "00050000" + "D0020000" + "05000000" + "04000800",
            Hex(w));
        Assert.Equal(0, w.DelayAfterMs);
    }

    [Fact]
    public void A_click_is_move_frame_press_frame_pause_release_frame()
    {
        IReadOnlyList<PlannedWrite> plan = PointerPlanner.Plan(new PointerCommand(PointerVerb.Click, 1, 2, 10, 10, PointerButtonName.Right), 5, 100);
        Assert.Equal(3, plan.Count);
        Assert.Equal(
            "05000000" + "01001C00" + "64000000" + "01000000" + "02000000" + "0A000000" + "0A000000" + "05000000" + "04000800",
            Hex(plan[0]));
        Assert.Equal(PointerPlanner.SettleMs, plan[0].DelayAfterMs);
        // Move at t=100, 20 ms to settle, press at t=120, 20 ms hold, release at t=140.
        Assert.Equal("05000000" + "02001400" + "78000000" + "11010000" + "01000000" + "05000000" + "04000800", Hex(plan[1]));
        Assert.Equal(PointerPlanner.HoldMs, plan[1].DelayAfterMs);
        Assert.Equal("05000000" + "02001400" + "8C000000" + "11010000" + "00000000" + "05000000" + "04000800", Hex(plan[2]));
        Assert.Equal(0, plan[2].DelayAfterMs);
    }

    [Fact]
    public void A_triple_click_presses_and_releases_three_times_with_pauses_between()
    {
        IReadOnlyList<PlannedWrite> plan = PointerPlanner.Plan(new PointerCommand(PointerVerb.Click, 1, 2, 10, 10, Count: 3), 5, 0);
        Assert.Equal(1 + 2 * 3, plan.Count);
        Assert.Equal([20, 20, 20, 20, 20, 20, 0], plan.Select(p => p.DelayAfterMs));
    }

    [Fact]
    public void Down_and_up_are_a_move_and_one_button_event()
    {
        IReadOnlyList<PlannedWrite> downPlan = PointerPlanner.Plan(new PointerCommand(PointerVerb.Down, 1, 2, 10, 10), 5, 0);
        Assert.Equal(2, downPlan.Count);
        Assert.Equal(PointerPlanner.SettleMs, downPlan[0].DelayAfterMs);
        PlannedWrite down = downPlan[1];
        Assert.Equal("05000000" + "02001400" + "14000000" + "10010000" + "01000000" + "05000000" + "04000800", Hex(down));
        PlannedWrite up = PointerPlanner.Plan(new PointerCommand(PointerVerb.Up, 1, 2, 10, 10), 5, 0)[1];
        Assert.Equal("05000000" + "02001400" + "14000000" + "10010000" + "00000000" + "05000000" + "04000800", Hex(up));
    }

    [Fact]
    public void Scroll_sends_one_wheel_click_per_step_with_the_sign_of_the_delta()
    {
        PlannedWrite w = PointerPlanner.Plan(new PointerCommand(PointerVerb.Scroll, 1, 2, 10, 10, DeltaX: 0, DeltaY: 2), 5, 0)[1];
        string step =
            "05000000" + "05000C00" + "00000000"
            + "05000000" + "03001400" + "14000000" + "00000000" + "000F0000"
            + "05000000" + "07001800" + "14000000" + "00000000" + "000F0000" + "01000000"
            + "05000000" + "04000800";
        Assert.EndsWith(step + step, Hex(w));

        PlannedWrite up = PointerPlanner.Plan(new PointerCommand(PointerVerb.Scroll, 1, 2, 10, 10, DeltaX: -1, DeltaY: 0), 5, 0)[1];
        Assert.Contains("14000000" + "01000000" + "00F1FFFF" + "FFFFFFFF", Hex(up));
    }

    [Fact]
    public void Socket_path_comes_from_the_two_variables_with_a_default_name()
    {
        Assert.Equal("/run/user/1/wayland-0", WaylandSession.SocketPath("/run/user/1", null));
        Assert.Equal("/run/user/1/wayland-1", WaylandSession.SocketPath("/run/user/1", "wayland-1"));
        Assert.Throws<PointerToolException>(() => WaylandSession.SocketPath(null, "wayland-0"));
        Assert.Throws<PointerToolException>(() => WaylandSession.SocketPath("relative", "wayland-0"));
        Assert.Throws<PointerToolException>(() => WaylandSession.SocketPath("/run/user/1", "../x"));
        Assert.Throws<PointerToolException>(() => WaylandSession.SocketPath("/run/user/1", ".."));
    }
}
