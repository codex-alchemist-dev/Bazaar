using Bazaar.Client.Motion;

namespace Bazaar.Client.Gestures.Tests;

public class BackTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(16);
    private static readonly GestureSettings _s = GestureSettings.Default;

    private static void Run(PredictiveBackController c)
    {
        for (var i = 0; i < 200 && c.Advance(_frame); i++)
        {
        }
    }

    [Fact]
    public void Mapping_at_rest_is_identity_and_at_full_progress_hits_the_tokens()
    {
        Assert.Equal(BackTransform.Identity with { BehindScale = _s.BackBehindMinScale }, PredictiveBackMapper.Map(0, BackGestureEdge.Left, 400, _s));
        var full = PredictiveBackMapper.Map(1, BackGestureEdge.Left, 400, _s);
        Assert.Equal(_s.BackMinScale, full.Scale, 9);
        Assert.Equal(_s.BackMaxShift * 400, full.TranslateX, 9);
        Assert.Equal(1, full.BehindScale, 9);
    }

    [Fact]
    public void Mapping_shifts_away_from_the_edge_and_clamps_progress()
    {
        Assert.True(PredictiveBackMapper.Map(0.5, BackGestureEdge.Left, 400, _s).TranslateX > 0);
        Assert.True(PredictiveBackMapper.Map(0.5, BackGestureEdge.Right, 400, _s).TranslateX < 0);
        Assert.Equal(PredictiveBackMapper.Map(1, BackGestureEdge.Left, 400, _s), PredictiveBackMapper.Map(7, BackGestureEdge.Left, 400, _s));
    }

    [Fact]
    public void Progress_drives_the_transform_and_commit_pops_after_the_spring()
    {
        var relay = new BackGestureRelay();
        using var c = new PredictiveBackController(relay, 400);
        var pops = 0;
        c.PopRequested += (_, _) => pops++;
        relay.Start(0, BackGestureEdge.Left);
        relay.Progress(0.5, BackGestureEdge.Left);
        Assert.True(c.IsActive);
        Assert.Equal(PredictiveBackMapper.Map(0.5, BackGestureEdge.Left, 400, _s), c.Transform);

        relay.Commit(0.5, BackGestureEdge.Left);
        Assert.Equal(0, pops);
        Run(c);
        Assert.Equal(1, pops);
        Assert.False(c.IsActive);
        Assert.Equal(BackTransform.Identity, c.Transform);
    }

    [Fact]
    public void Cancel_springs_back_without_popping()
    {
        var relay = new BackGestureRelay();
        using var c = new PredictiveBackController(relay, 400);
        var pops = 0;
        c.PopRequested += (_, _) => pops++;
        relay.Start(0, BackGestureEdge.Right);
        relay.Progress(0.7, BackGestureEdge.Right);
        relay.Cancel(0.7, BackGestureEdge.Right);
        Run(c);
        Assert.Equal(0, pops);
        Assert.False(c.IsActive);
        Assert.Equal(0, c.Progress);
    }

    [Fact]
    public void Fallback_source_pops_instantly()
    {
        var source = new FallbackBackGestureSource();
        using var c = new PredictiveBackController(source, 400);
        var pops = 0;
        c.PopRequested += (_, _) => pops++;
        Assert.False(source.IsPredictive);
        source.Back();
        Assert.Equal(1, pops);
        Assert.False(c.IsActive);
    }

    [Fact]
    public void Reduced_motion_pops_instantly_and_draws_nothing()
    {
        var relay = new BackGestureRelay();
        using var c = new PredictiveBackController(relay, 400, level: MotionLevel.Reduced);
        var pops = 0;
        c.PopRequested += (_, _) => pops++;
        relay.Start(0, BackGestureEdge.Left);
        relay.Progress(0.6, BackGestureEdge.Left);
        Assert.Equal(BackTransform.Identity, c.Transform);
        relay.Commit(0.6, BackGestureEdge.Left);
        Assert.Equal(1, pops);
    }

    [Fact]
    public void Disposed_controller_ignores_the_source()
    {
        var relay = new BackGestureRelay();
        var c = new PredictiveBackController(relay, 400);
        c.Dispose();
        relay.Start(0, BackGestureEdge.Left);
        Assert.False(c.IsActive);
    }
}
