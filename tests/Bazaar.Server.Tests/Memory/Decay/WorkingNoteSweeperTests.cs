using Bazaar.Server.Memory.Decay;

using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Memory.Decay;

public class WorkingNoteSweeperTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static WorkingNoteSweeper CreateSweeper(DateTimeOffset now) =>
        new(new FakeTimeProvider(now));

    [Fact]
    public void Sweeps_only_closed_and_expired_working_notes()
    {
        var sweeper = CreateSweeper(_now);
        var notes = new[]
        {
            new WorkingNote("agents/a/memory/working/closed-old.md", TaskClosed: true, _now.AddDays(-8)),
            new WorkingNote("agents/a/memory/working/closed-fresh.md", TaskClosed: true, _now.AddDays(-2)),
            new WorkingNote("agents/a/memory/working/open-old.md", TaskClosed: false, _now.AddDays(-30)),
        };

        var plan = sweeper.Plan(notes);

        Assert.Equal(1, plan.Count);
        Assert.Equal(["agents/a/memory/working/closed-old.md"], plan.NotePaths);
    }

    [Fact]
    public void Open_tasks_are_never_swept_however_old()
    {
        var sweeper = CreateSweeper(_now);
        var notes = new[]
        {
            new WorkingNote("agents/a/memory/working/open-ancient.md", TaskClosed: false, _now.AddDays(-3650)),
            new WorkingNote("agents/a/memory/working/open-ancient-2.md", TaskClosed: false, DateTimeOffset.UnixEpoch),
        };

        var plan = sweeper.Plan(notes);

        Assert.Equal(0, plan.Count);
        Assert.Empty(plan.NotePaths);
    }

    [Fact]
    public void Note_exactly_at_ttl_is_not_swept()
    {
        var sweeper = CreateSweeper(_now);
        var notes = new[] { new WorkingNote("agents/a/memory/working/edge.md", TaskClosed: true, _now.AddDays(-7)) };

        Assert.Equal(0, sweeper.Plan(notes).Count);
    }

    [Fact]
    public void Custom_ttl_is_used()
    {
        var sweeper = new WorkingNoteSweeper(new FakeTimeProvider(_now), TimeSpan.FromDays(2));
        var notes = new[] { new WorkingNote("agents/a/memory/working/t.md", TaskClosed: true, _now.AddDays(-3)) };

        Assert.Equal(1, sweeper.Plan(notes).Count);
    }

    [Fact]
    public void Default_ttl_is_seven_days()
    {
        var sweeper = CreateSweeper(_now);

        Assert.Equal(TimeSpan.FromDays(7), sweeper.Ttl);
    }

    [Fact]
    public void Plan_is_deterministic_for_a_fixed_now()
    {
        var notes = new[]
        {
            new WorkingNote("agents/b/memory/working/z.md", TaskClosed: true, _now.AddDays(-20)),
            new WorkingNote("agents/a/memory/working/m.md", TaskClosed: true, _now.AddDays(-9)),
            new WorkingNote("agents/a/memory/working/b.md", TaskClosed: true, _now.AddDays(-30)),
            new WorkingNote("agents/a/memory/working/open.md", TaskClosed: false, _now.AddDays(-30)),
        };

        var first = CreateSweeper(_now).Plan(notes);
        var second = CreateSweeper(_now).Plan(notes);

        Assert.Equal(first.NotePaths, second.NotePaths);
        Assert.Equal(
            ["agents/a/memory/working/b.md", "agents/a/memory/working/m.md", "agents/b/memory/working/z.md"],
            first.NotePaths);
    }

    [Fact]
    public void Plan_changes_only_when_the_clock_moves()
    {
        var clock = new FakeTimeProvider(_now);
        var sweeper = new WorkingNoteSweeper(clock);
        var notes = new[] { new WorkingNote("agents/a/memory/working/t.md", TaskClosed: true, _now) };

        Assert.Equal(0, sweeper.Plan(notes).Count);

        clock.Advance(TimeSpan.FromDays(8));

        Assert.Equal(1, sweeper.Plan(notes).Count);
    }

    [Fact]
    public void Empty_input_gives_an_empty_plan()
    {
        var plan = CreateSweeper(_now).Plan([]);

        Assert.Equal(0, plan.Count);
    }

    [Fact]
    public void Non_positive_ttl_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkingNoteSweeper(new FakeTimeProvider(_now), TimeSpan.Zero));
    }
}
