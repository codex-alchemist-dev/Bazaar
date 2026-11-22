namespace Bazaar.Client.Mobile;

/// <summary>What a row swipe can do (doc 93: approve, reply, archive).</summary>
public enum RowAction
{
    Approve,
    Reply,
    Archive,
}

/// <summary>The action each direction commits. A direction with null does nothing and does not stretch the row.</summary>
public sealed record RowSwipeActions(RowAction? Right, RowAction? Left);

/// <summary>Where a row swipe stands after a move.</summary>
/// <param name="OffsetDp">How far the row has been pulled; the sign follows the finger.</param>
/// <param name="Action">The action the pull would commit, or null when that direction has none.</param>
/// <param name="Armed">True once the pull is past the commit point: releasing now commits the action.</param>
public readonly record struct RowSwipeState(double OffsetDp, RowAction? Action, bool Armed);

/// <summary>
/// The row swipe state machine (doc 93, Phone gestures). It has no UI: the head feeds it the finger's horizontal travel and
/// shows the action blob behind the row from the state. A haptic tick is due when <see cref="RowSwipeState.Armed"/> turns true
/// (the commit point); pulling back under it disarms, so the row can still be cancelled by releasing early.
/// </summary>
public sealed class RowSwipe
{
    private readonly RowSwipeActions _actions;
    private readonly double _commitDp;
    private double _offset;
    private bool _active;

    public RowSwipe(RowSwipeActions actions, double commitDp = 96)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        if (commitDp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commitDp), "The commit distance must be positive.");
        }

        _commitDp = commitDp;
    }

    /// <summary>True while a pull is in progress.</summary>
    public bool IsActive => _active;

    /// <summary>The finger went down on the row.</summary>
    public void Begin()
    {
        _active = true;
        _offset = 0;
    }

    /// <summary>The finger moved to <paramref name="totalDxDp"/> from where it went down.</summary>
    /// <param name="crossedCommitPoint">True exactly when this move armed the action, so the head can play one haptic tick.</param>
    public RowSwipeState Move(double totalDxDp, out bool crossedCommitPoint)
    {
        crossedCommitPoint = false;
        if (!_active)
        {
            return new RowSwipeState(0, null, false);
        }

        var action = ActionFor(totalDxDp);
        var wasArmed = Armed(_offset);
        _offset = action is null ? 0 : totalDxDp;
        var armed = Armed(_offset);
        crossedCommitPoint = armed && !wasArmed;
        return new RowSwipeState(_offset, action, armed);
    }

    /// <summary>The finger lifted. Returns the action to run when the pull was armed, otherwise null (the row springs back).</summary>
    public RowAction? Release()
    {
        if (!_active)
        {
            return null;
        }

        var action = Armed(_offset) ? ActionFor(_offset) : null;
        Reset();
        return action;
    }

    /// <summary>The gesture was interrupted. Nothing commits.</summary>
    public void Cancel() => Reset();

    private RowAction? ActionFor(double dx) => dx > 0 ? _actions.Right : dx < 0 ? _actions.Left : null;

    private bool Armed(double offset) => ActionFor(offset) is not null && Math.Abs(offset) >= _commitDp;

    private void Reset()
    {
        _active = false;
        _offset = 0;
    }
}
