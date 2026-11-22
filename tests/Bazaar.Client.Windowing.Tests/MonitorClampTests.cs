namespace Bazaar.Client.Windowing.Tests;

/// <summary>Clamp rules for restored windows. Pure: fake monitors only.</summary>
public sealed class MonitorClampTests
{
    [Fact]
    public void Off_screen_window_lands_on_the_primary_even_when_it_is_listed_second()
    {
        var monitors = new[] { TestData.Right, TestData.Primary };

        var result = MonitorClamp.Clamp(new WindowBounds(-9000, -9000, 800, 600), null, monitors);

        Assert.Equal(new WindowBounds(0, 0, 800, 600), result);
    }

    [Fact]
    public void Window_past_the_right_and_bottom_edges_is_pulled_fully_inside()
    {
        var result = MonitorClamp.Clamp(new WindowBounds(1800, 900, 800, 600), null, [TestData.Primary]);

        Assert.Equal(new WindowBounds(1120, 440, 800, 600), result);
    }

    [Fact]
    public void Window_partly_off_the_top_left_moves_inside_without_resizing()
    {
        var result = MonitorClamp.Clamp(new WindowBounds(-100, -50, 400, 300), null, [TestData.Primary]);

        Assert.Equal(new WindowBounds(0, 0, 400, 300), result);
    }

    [Fact]
    public void Window_bigger_than_the_work_area_is_limited_to_it()
    {
        var result = MonitorClamp.Clamp(new WindowBounds(0, 0, 5000, 3000), null, [TestData.Primary]);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1040), result);
    }

    [Fact]
    public void Window_smaller_than_the_minimum_is_raised_to_320_by_200()
    {
        var result = MonitorClamp.Clamp(new WindowBounds(100, 100, 50, 50), null, [TestData.Primary]);

        Assert.Equal(new WindowBounds(100, 100, 320, 200), result);
    }

    [Fact]
    public void Work_area_wins_over_the_minimum_on_a_very_small_display()
    {
        var tiny = new MonitorInfo("tiny", new WindowBounds(0, 0, 300, 150), 1.0, true);

        var result = MonitorClamp.Clamp(new WindowBounds(0, 0, 800, 600), null, [tiny]);

        Assert.Equal(new WindowBounds(0, 0, 300, 150), result);
    }

    [Fact]
    public void Known_monitor_id_wins_over_overlap()
    {
        // Mostly on the primary, but the saved monitor is the right one.
        var result = MonitorClamp.Clamp(new WindowBounds(100, 100, 400, 300), "right", [TestData.Primary, TestData.Right]);

        Assert.Equal(new WindowBounds(1920, 100, 400, 300), result);
    }

    [Fact]
    public void Removed_monitor_falls_back_to_the_monitor_with_most_overlap()
    {
        // The saved monitor "gone" no longer exists. The window sits fully on the right monitor.
        var wanted = new WindowBounds(2100, 100, 800, 600);

        var result = MonitorClamp.Clamp(wanted, "gone", [TestData.Primary, TestData.Right]);

        Assert.Equal(wanted, result);
    }

    [Fact]
    public void Overlap_picks_the_monitor_holding_most_of_the_window()
    {
        // 220 px of the window are on the primary, 580 px on the right.
        var result = MonitorClamp.Clamp(new WindowBounds(1700, 100, 800, 600), null, [TestData.Primary, TestData.Right]);

        Assert.Equal(new WindowBounds(1920, 100, 800, 600), result);
    }

    [Fact]
    public void Monitor_with_a_negative_origin_is_respected()
    {
        var left = new MonitorInfo("left", new WindowBounds(-1920, 0, 1920, 1040), 1.0, false);

        var result = MonitorClamp.Clamp(new WindowBounds(-5000, 0, 400, 300), "left", [TestData.Primary, left]);

        Assert.Equal(new WindowBounds(-1920, 0, 400, 300), result);
    }

    [Fact]
    public void Scale_does_not_change_the_result()
    {
        var at1 = new MonitorInfo("primary", new WindowBounds(0, 0, 1920, 1040), 1.0, true);
        var at2 = new MonitorInfo("primary", new WindowBounds(0, 0, 1920, 1040), 2.0, true);
        var wanted = new WindowBounds(5000, 5000, 800, 600);

        Assert.Equal(MonitorClamp.Clamp(wanted, null, [at1]), MonitorClamp.Clamp(wanted, null, [at2]));
    }

    [Fact]
    public void No_monitors_returns_the_wanted_bounds_unchanged()
    {
        var wanted = new WindowBounds(5000, 5000, 10, 10);

        Assert.Equal(wanted, MonitorClamp.Clamp(wanted, "primary", []));
    }

    [Fact]
    public void Extreme_values_do_not_overflow_and_still_land_inside_the_work_area()
    {
        var wanted = new WindowBounds(int.MaxValue, int.MinValue, int.MaxValue, int.MinValue);

        var result = MonitorClamp.Clamp(wanted, null, [TestData.Primary]);

        Assert.Equal(new WindowBounds(0, 0, 1920, 200), result);
    }

    [Theory]
    [InlineData(-200, -200, 100, 100)]
    [InlineData(1900, 1000, 900, 900)]
    [InlineData(int.MinValue, int.MaxValue, 1, 1)]
    [InlineData(960, 520, 0, 0)]
    public void Result_is_inside_the_work_area_and_clamping_is_idempotent(int x, int y, int width, int height)
    {
        var wanted = new WindowBounds(x, y, width, height);

        var once = MonitorClamp.Clamp(wanted, null, [TestData.Primary, TestData.Right]);
        var twice = MonitorClamp.Clamp(once, null, [TestData.Primary, TestData.Right]);

        Assert.Equal(once, twice);
        Assert.True(once.X >= 0 && once.Y >= 0);
        Assert.True((long)once.X + once.Width <= 1920 && (long)once.Y + once.Height <= 1040);
    }
}
