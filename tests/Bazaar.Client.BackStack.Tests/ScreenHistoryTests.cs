using Bazaar.Client.BackStack.Abstractions;

namespace Bazaar.Client.BackStack.Tests;

public class ScreenHistoryTests
{
    private static ScreenHistory Chat() => new(new ScreenEntry("chat"));

    [Fact]
    public void A_new_stack_has_only_the_root_and_cannot_pop()
    {
        var stack = Chat();

        Assert.Equal(1, stack.Depth);
        Assert.False(stack.CanPop);
        Assert.Equal(stack.Root, stack.Current);
        Assert.Null(stack.Behind);
        Assert.False(stack.Pop());
    }

    [Fact]
    public void Push_and_pop_walk_chat_thread_profile_and_back()
    {
        var stack = Chat();
        stack.Push(new ScreenEntry("thread", "t1"));
        stack.Push(new ScreenEntry("profile", "p1"));

        Assert.Equal(3, stack.Depth);
        Assert.Equal(new ScreenEntry("profile", "p1"), stack.Current);
        Assert.Equal(new ScreenEntry("thread", "t1"), stack.Behind);

        Assert.True(stack.Pop());
        Assert.Equal(new ScreenEntry("thread", "t1"), stack.Current);
        Assert.Equal(stack.Root, stack.Behind);
        Assert.True(stack.Pop());
        Assert.False(stack.CanPop);
        Assert.Equal(new[] { new ScreenEntry("chat") }, stack.Entries);
    }

    [Fact]
    public void Pushing_the_screen_already_on_top_does_nothing()
    {
        var stack = Chat();
        stack.Push(new ScreenEntry("thread", "t1"));
        var raised = 0;
        stack.Changed += (_, _) => raised++;

        Assert.False(stack.Push(new ScreenEntry("thread", "t1")));
        Assert.Equal(2, stack.Depth);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void The_same_kind_with_another_key_is_a_different_screen()
    {
        var stack = Chat();
        stack.Push(new ScreenEntry("profile", "a"));

        Assert.True(stack.Push(new ScreenEntry("profile", "b")));
        Assert.Equal(3, stack.Depth);
    }

    [Fact]
    public void Push_rejects_a_blank_kind()
    {
        Assert.Throws<ArgumentException>(() => Chat().Push(new ScreenEntry(" ")));
    }

    [Fact]
    public void PopTo_keeps_the_named_screen_and_drops_everything_above()
    {
        var stack = Chat();
        stack.Push(new ScreenEntry("thread", "t1"));
        stack.Push(new ScreenEntry("profile", "p1"));
        stack.Push(new ScreenEntry("settings"));

        Assert.True(stack.PopTo("thread"));
        Assert.Equal(new ScreenEntry("thread", "t1"), stack.Current);
        Assert.Equal(2, stack.Depth);
    }

    [Fact]
    public void PopTo_does_nothing_for_a_missing_kind_or_the_top()
    {
        var stack = Chat();
        stack.Push(new ScreenEntry("thread", "t1"));

        Assert.False(stack.PopTo("profile"));
        Assert.False(stack.PopTo("thread"));
        Assert.Equal(2, stack.Depth);
    }

    [Fact]
    public void ReplaceTop_swaps_without_growing_and_is_a_no_op_at_the_root()
    {
        var stack = Chat();
        Assert.False(stack.ReplaceTop(new ScreenEntry("thread", "t1")));

        stack.Push(new ScreenEntry("thread", "t1"));
        Assert.True(stack.ReplaceTop(new ScreenEntry("thread", "t2")));
        Assert.Equal(2, stack.Depth);
        Assert.Equal(new ScreenEntry("thread", "t2"), stack.Current);
    }

    [Fact]
    public void PopToRoot_clears_everything_above_the_root()
    {
        var stack = Chat();
        stack.Push(new ScreenEntry("thread", "t1"));
        stack.Push(new ScreenEntry("profile", "p1"));

        Assert.True(stack.PopToRoot());
        Assert.Equal(1, stack.Depth);
        Assert.False(stack.PopToRoot());
    }

    [Fact]
    public void Changed_reports_each_change_with_the_affected_entry()
    {
        var stack = Chat();
        var seen = new List<(ScreenHistoryChangeKind, ScreenEntry)>();
        stack.Changed += (_, e) => seen.Add((e.Change, e.Affected));

        stack.Push(new ScreenEntry("thread", "t1"));
        stack.ReplaceTop(new ScreenEntry("thread", "t2"));
        stack.Pop();
        stack.Push(new ScreenEntry("profile", "p"));
        stack.PopToRoot();

        Assert.Equal(
            [
                (ScreenHistoryChangeKind.Pushed, new ScreenEntry("thread", "t1")),
                (ScreenHistoryChangeKind.Replaced, new ScreenEntry("thread", "t1")),
                (ScreenHistoryChangeKind.Popped, new ScreenEntry("thread", "t2")),
                (ScreenHistoryChangeKind.Pushed, new ScreenEntry("profile", "p")),
                (ScreenHistoryChangeKind.Reset, new ScreenEntry("profile", "p")),
            ],
            seen);
    }

    [Fact]
    public void Entries_is_a_snapshot()
    {
        var stack = Chat();
        var before = stack.Entries;
        stack.Push(new ScreenEntry("thread", "t1"));

        Assert.Single(before);
        Assert.Equal(2, stack.Entries.Count);
    }
}
