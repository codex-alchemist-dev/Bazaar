namespace Bazaar.Client.Gestures.Tests;

public class SpringValueTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Retarget_keeps_position_and_velocity()
    {
        var s = new SpringValue();
        s.Set(0);
        s.AnimateTo(100, 1.0, Ms(300), 500);
        s.Advance(Ms(50));
        var (p, v) = (s.Position, s.Velocity);
        s.AnimateTo(0, 1.0, Ms(300));
        Assert.Equal(p, s.Position);
        Assert.Equal(v, s.Velocity);
    }

    [Fact]
    public void Release_velocity_pushes_past_the_start_before_turning()
    {
        var s = new SpringValue();
        s.Set(10);
        s.AnimateTo(0, 0.8, Ms(320), 1000);
        s.Advance(Ms(16));
        Assert.True(s.Position > 10);
    }

    [Fact]
    public void Settles_exactly_on_target_and_stops()
    {
        var s = new SpringValue();
        s.Set(80);
        s.AnimateTo(0, 0.8, Ms(320));
        for (var i = 0; i < 200 && s.Advance(Ms(16)); i++)
        {
        }

        Assert.False(s.IsAnimating);
        Assert.Equal(0, s.Position);
        Assert.Equal(0, s.Velocity);
    }

    [Fact]
    public void Rebase_shifts_the_frame_without_disturbing_the_motion()
    {
        var s = new SpringValue();
        s.Set(-300);
        s.AnimateTo(-400, 1.0, Ms(300), -200);
        s.Advance(Ms(40));
        var v = s.Velocity;
        var p = s.Position;
        s.Rebase(400);
        Assert.Equal(p + 400, s.Position, 9);
        Assert.Equal(v, s.Velocity);
        Assert.Equal(0, s.Target, 9);
    }

    [Fact]
    public void Settings_are_clamped()
    {
        var c = (GestureSettings.Default with { BackMinScale = 0.1, SettleTime = Ms(1), SettleDamping = 9 }).Clamped();
        Assert.Equal(0.5, c.BackMinScale);
        Assert.Equal(Ms(30), c.SettleTime);
        Assert.Equal(2.0, c.SettleDamping);
    }
}
