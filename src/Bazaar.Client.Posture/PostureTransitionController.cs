namespace Bazaar.Client.Posture;

/// <summary>What the view applies: the layout class to arrange with, the hinge gap, and the transition channels.</summary>
public readonly record struct PostureFrame(PostureLayoutClass Class, LayoutVariantKind Variant, HingeInset Hinge, UnfoldFrame Unfold);

/// <summary>
/// Ties posture, layout selection and the unfold transition together, pure. Feed it posture readings; read frames as time advances.
/// The layout class stays at the previous non-folded class while folding, so the panes shrink back instead of vanishing at once;
/// the first reading snaps with no animation.
/// </summary>
public sealed class PostureTransitionController
{
    private readonly PostureThresholds _thresholds;
    private readonly UnfoldTransition _transition;

    private bool _initialized;
    private LayoutSelection _selection = new(PostureLayoutClass.Folded, LayoutVariantKind.Folded);
    private PostureLayoutClass _visualClass = PostureLayoutClass.Folded;
    private LayoutVariantKind _visualVariant = LayoutVariantKind.Folded;
    private HingeInset _hinge = HingeInset.None;
    private double? _lastAngle;

    public PostureTransitionController(Motion.MotionLevel level, PostureThresholds? thresholds = null, PostureMotionTokens? tokens = null, Motion.MotionTokens? motion = null)
    {
        _thresholds = (thresholds ?? PostureThresholds.Default).Clamped();
        _transition = new UnfoldTransition(level, tokens, motion);
    }

    /// <summary>The latest frame.</summary>
    public PostureFrame Current => new(_visualClass, _visualVariant, _hinge, _transition.Current);

    /// <summary>The layout the controller is heading for (differs from <see cref="Current"/> only while folding).</summary>
    public LayoutSelection Target => _selection;

    /// <summary>True while a transition runs; the driver asks for frames only then.</summary>
    public bool IsRunning => !_transition.IsSettled;

    /// <summary>Takes a new posture reading and window width.</summary>
    public void Update(PostureState state, double windowWidth, LayoutVariants variants)
    {
        _selection = LayoutSelector.Select(state, windowWidth, variants, _thresholds);
        _hinge = state.Hinge;
        var target = _selection.Class == PostureLayoutClass.Folded ? 0.0 : 1.0;

        if (!_initialized)
        {
            _initialized = true;
            _transition.Snap(target);
            _lastAngle = state.FoldAngleDegrees;
            ResolveVisual();
            return;
        }

        _transition.Retarget(target);
        if (state.FoldAngleDegrees is { } angle && angle != _lastAngle)
        {
            _transition.TrackAngle(angle);
        }

        _lastAngle = state.FoldAngleDegrees;
        ResolveVisual();
    }

    /// <summary>Changes the motion level (the person's setting or the OS flag changed); a running transition rests at its goal.</summary>
    public void SetLevel(Motion.MotionLevel level)
    {
        _transition.SetLevel(level);
        ResolveVisual();
    }

    /// <summary>Moves time forward and returns the new frame.</summary>
    public PostureFrame Advance(TimeSpan delta)
    {
        _transition.Advance(delta);
        ResolveVisual();
        return Current;
    }

    private void ResolveVisual()
    {
        if (_selection.Class != PostureLayoutClass.Folded)
        {
            _visualClass = _selection.Class;
            _visualVariant = _selection.Variant;
        }
        else if (_transition.IsSettled && _transition.Progress <= 0)
        {
            _visualClass = PostureLayoutClass.Folded;
            _visualVariant = LayoutVariantKind.Folded;
        }
    }
}
