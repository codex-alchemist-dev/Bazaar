using Bazaar.Client.Mobile;

namespace Bazaar.Client.Mobile.Tests;

public class RowSwipeTests
{
    private static RowSwipe Both() => new(new RowSwipeActions(RowAction.Approve, RowAction.Archive), commitDp: 100);

    [Fact]
    public void Pulling_right_past_the_commit_point_arms_the_right_action_once_and_release_commits_it()
    {
        var row = Both();
        row.Begin();

        var early = row.Move(60, out var crossedEarly);
        var armed = row.Move(110, out var crossed);
        var further = row.Move(140, out var crossedAgain);

        Assert.False(early.Armed);
        Assert.False(crossedEarly);
        Assert.True(armed.Armed);
        Assert.True(crossed);
        Assert.Equal(RowAction.Approve, armed.Action);
        Assert.False(crossedAgain);
        Assert.Equal(140, further.OffsetDp);
        Assert.Equal(RowAction.Approve, row.Release());
        Assert.False(row.IsActive);
    }

    [Fact]
    public void Pulling_left_commits_the_left_action()
    {
        var row = Both();
        row.Begin();
        row.Move(-120, out _);

        Assert.Equal(RowAction.Archive, row.Release());
    }

    [Fact]
    public void Releasing_before_the_commit_point_springs_back_with_no_action()
    {
        var row = Both();
        row.Begin();
        row.Move(80, out _);

        Assert.Null(row.Release());
    }

    [Fact]
    public void Pulling_back_under_the_commit_point_disarms_and_rearming_ticks_again()
    {
        var row = Both();
        row.Begin();
        row.Move(120, out _);
        var back = row.Move(50, out _);
        var again = row.Move(130, out var crossed);

        Assert.False(back.Armed);
        Assert.True(again.Armed);
        Assert.True(crossed);
    }

    [Fact]
    public void A_direction_with_no_action_does_not_stretch_the_row_or_arm()
    {
        var row = new RowSwipe(new RowSwipeActions(RowAction.Reply, null), commitDp: 100);
        row.Begin();

        var state = row.Move(-200, out var crossed);

        Assert.Equal(0, state.OffsetDp);
        Assert.Null(state.Action);
        Assert.False(crossed);
        Assert.Null(row.Release());
    }

    [Fact]
    public void An_interrupted_pull_commits_nothing()
    {
        var row = Both();
        row.Begin();
        row.Move(150, out _);
        row.Cancel();

        Assert.False(row.IsActive);
        Assert.Null(row.Release());
    }

    [Fact]
    public void Moves_before_begin_are_ignored_and_a_bad_commit_distance_is_refused()
    {
        var row = Both();

        Assert.False(row.Move(200, out var crossed).Armed);
        Assert.False(crossed);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowSwipe(new RowSwipeActions(null, null), 0));
    }
}
