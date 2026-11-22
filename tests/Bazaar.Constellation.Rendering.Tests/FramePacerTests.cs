using Bazaar.Constellation.Rendering.Core;
using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class FramePacerTests
{
    private sealed class Clock
    {
        public TimeSpan Now { get; set; }

        public void Advance(double ms) => Now += TimeSpan.FromMilliseconds(ms);
    }

    private static (FramePacer Pacer, Clock Clock) Make(double hz)
    {
        var clock = new Clock();
        return (new FramePacer(hz, () => clock.Now), clock);
    }

    /// <summary>Runs a host loop for a duration, feeding input every 8 ms with the given delta, and returns the frame count.</summary>
    private static int Drive(FramePacer p, Clock c, double seconds, float delta, bool input)
    {
        int frames = 0;
        double end = c.Now.TotalMilliseconds + seconds * 1000;
        double nextInput = c.Now.TotalMilliseconds;
        while (c.Now.TotalMilliseconds < end)
        {
            if (input && c.Now.TotalMilliseconds >= nextInput)
            {
                p.OnInput(delta, c.Now);
                nextInput += 8;
            }

            FrameDue d = p.NextFrameDue(c.Now);
            if (d.Kind == FrameDueKind.RenderNow)
            {
                p.OnFrameStarted(c.Now);
                frames++;
                c.Advance(0.5);
            }
            else
            {
                c.Advance(d.Kind == FrameDueKind.Wait ? Math.Min(d.Delay.TotalMilliseconds, 1) : 1);
            }
        }

        return frames;
    }

    [Fact]
    public void Idle_RendersZeroFramesOverTenSeconds()
    {
        var (p, c) = Make(120);
        Assert.Equal(PacerMode.Idle, p.Mode);
        Assert.False(p.NeedsAnotherFrame);
        Assert.Equal(0, p.TargetFps);
        Assert.Equal(0, Drive(p, c, 10, 0, input: false));
    }

    [Fact]
    public void Input_WakesInstantly()
    {
        var (p, c) = Make(120);
        c.Advance(5000);
        Assert.Equal(FrameDueKind.Skip, p.NextFrameDue(c.Now).Kind);
        p.OnInput(0f, c.Now);
        Assert.Equal(FrameDueKind.RenderNow, p.NextFrameDue(c.Now).Kind);
        Assert.Equal(PacerMode.InteractingSmall, p.Mode);

        var (q, d) = Make(120);
        d.Advance(5000);
        q.Wake();
        Assert.Equal(FrameDueKind.RenderNow, q.NextFrameDue(d.Now).Kind);
    }

    [Theory]
    [InlineData(120.0, 120.0, 90.0)]
    [InlineData(240.0, 240.0, 180.0)]
    public void SmallAndBigMovement_UseTheirOwnRates(double hz, double big, double small)
    {
        var (p, c) = Make(hz);
        int bigFrames = Drive(p, c, 2, 500f, input: true);
        Assert.Equal(PacerMode.InteractingBig, p.Mode);
        Assert.Equal(big, p.TargetFps);
        Assert.InRange(bigFrames / 2.0, big * 0.9, big * 1.02);

        var (q, d) = Make(hz);
        int smallFrames = Drive(q, d, 2, 10f, input: true);
        Assert.Equal(PacerMode.InteractingSmall, q.Mode);
        Assert.Equal(small, q.TargetFps);
        Assert.InRange(smallFrames / 2.0, small * 0.9, small * 1.02);
        Assert.True(q.TargetFrameMs > p.TargetFrameMs);
    }

    [Fact]
    public void Settling_ReturnsToIdleThenSkips()
    {
        var (p, c) = Make(120);
        p.OnInput(10f, c.Now);
        p.OnFrameStarted(c.Now);
        c.Advance(150);
        Assert.Equal(PacerMode.Settling, p.Mode);
        Assert.True(p.NeedsAnotherFrame);
        c.Advance(500);
        Assert.Equal(PacerMode.Idle, p.Mode);
        Assert.Equal(FrameDueKind.Skip, p.NextFrameDue(c.Now).Kind);
    }

    [Fact]
    public void BigRate_HoldsThroughOneSlowEvent_ThenDropsToSmall()
    {
        var (p, c) = Make(120);
        p.OnInput(500f, c.Now);
        c.Advance(50);
        p.OnInput(5f, c.Now);
        Assert.Equal(PacerMode.InteractingBig, p.Mode);
        c.Advance(150);
        p.OnInput(5f, c.Now);
        Assert.Equal(PacerMode.InteractingSmall, p.Mode);
    }

    [Fact]
    public void Vrr_LateFrameDoesNotBurstCatchUp()
    {
        var (p, c) = Make(120);
        p.OnInput(500f, c.Now);
        p.OnFrameStarted(c.Now);

        // The frame took 50 ms (six intervals late); input keeps arriving.
        c.Advance(50);
        p.OnInput(500f, c.Now);
        Assert.Equal(FrameDueKind.RenderNow, p.NextFrameDue(c.Now).Kind);
        p.OnFrameStarted(c.Now);

        // Immediately after, the next frame is a full interval away, not due.
        FrameDue next = p.NextFrameDue(c.Now);
        Assert.Equal(FrameDueKind.Wait, next.Kind);
        Assert.Equal(1000.0 / 120, next.Delay.TotalMilliseconds, 3);
    }

    [Fact]
    public void Delay_NeverExceedsNextAllowedFrameTime()
    {
        var (p, c) = Make(120);
        p.OnInput(500f, c.Now);
        p.OnFrameStarted(c.Now);
        c.Advance(3);
        FrameDue d = p.NextFrameDue(c.Now);
        Assert.Equal(FrameDueKind.Wait, d.Kind);
        Assert.Equal((1000.0 / 120) - 3, d.Delay.TotalMilliseconds, 3);
        c.Now += d.Delay;
        Assert.Equal(FrameDueKind.RenderNow, p.NextFrameDue(c.Now).Kind);
    }

    [Fact]
    public void Animation_KeepsFramesComingWithoutInput()
    {
        var (p, c) = Make(120);
        p.SetAnimating(true);
        Assert.True(p.NeedsAnotherFrame);
        Assert.True(Drive(p, c, 1, 0, input: false) > 60);
        p.SetAnimating(false);
        Assert.False(p.NeedsAnotherFrame);
        Assert.Equal(FrameDueKind.Skip, p.NextFrameDue(c.Now).Kind);
    }

    [Fact]
    public void ZoomRatio_ConvertsSymmetrically()
    {
        var (p, _) = Make(120);
        Assert.Equal(p.ZoomRatioToPixels(2f), p.ZoomRatioToPixels(0.5f), 2);
        Assert.Equal(0f, p.ZoomRatioToPixels(1f));
    }

    [Fact]
    public void InvalidSettings_Throw()
    {
        var c = new Clock();
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(0, () => c.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(120, () => c.Now, new FramePacerOptions { SmallFraction = 0f }));
    }
}
