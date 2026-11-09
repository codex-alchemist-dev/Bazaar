using Bazaar.Server.Kanban.Ryusei;

namespace Bazaar.Server.Tests.Kanban.Ryusei;

/// <summary>Trigger chips on macro board cards (doc 38, Layer 1: micro links on a card). Pure grouping and ordering.</summary>
public sealed class RyuseiTaskChipsTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _taskA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _taskB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static RyuseiTriggerRecord Trigger(
        int n,
        Guid? taskId,
        RyuseiTriggerState state = RyuseiTriggerState.Active,
        string purpose = "Check the release",
        DateTimeOffset? nextFireAt = null,
        RyuseiTriggerKind kind = RyuseiTriggerKind.Timer) => new(
            TriggerId: Id(n),
            ProjectId: Guid.Parse("00000000-0000-0000-0000-0000000000f1"),
            CreatedBy: Guid.Parse("00000000-0000-0000-0000-0000000000c1"),
            Kind: kind,
            TargetAgentIds: [Guid.Parse("00000000-0000-0000-0000-0000000000a1")],
            Purpose: purpose,
            SpecJson: "{}",
            TaskId: taskId,
            AttachGcId: null,
            PingMode: RyuseiPingMode.None,
            State: state,
            NextFireAt: nextFireAt,
            FireCount: 0,
            ConsecutiveRefusals: 0,
            MaxFires: null,
            ExpiresAt: null,
            ChainDepth: 0,
            Version: 1,
            CreatedAt: _now,
            UpdatedAt: _now);

    [Fact]
    public void Triggers_without_a_task_are_ignored()
    {
        var triggers = new[] { Trigger(1, taskId: null), Trigger(2, taskId: null, state: RyuseiTriggerState.Paused) };

        Assert.Empty(RyuseiTaskChips.ForBoard(triggers));
        Assert.Empty(RyuseiTaskChips.ForTask(triggers, _taskA));
    }

    [Theory]
    [InlineData(RyuseiTriggerState.Active, true)]
    [InlineData(RyuseiTriggerState.Paused, true)]
    [InlineData(RyuseiTriggerState.Fired, false)]
    [InlineData(RyuseiTriggerState.Expired, false)]
    [InlineData(RyuseiTriggerState.Cancelled, false)]
    public void Only_live_states_produce_chips(RyuseiTriggerState state, bool expectedShown)
    {
        var triggers = new[] { Trigger(1, _taskA, state) };

        var board = RyuseiTaskChips.ForBoard(triggers);
        var task = RyuseiTaskChips.ForTask(triggers, _taskA);

        Assert.Equal(expectedShown, board.ContainsKey(_taskA));
        Assert.Equal(expectedShown, task.Count == 1);
    }

    [Fact]
    public void Long_purpose_is_truncated_to_80_characters_with_an_ellipsis()
    {
        var purpose = new string('x', 120);

        var chip = Assert.Single(RyuseiTaskChips.ForTask([Trigger(1, _taskA, purpose: purpose)], _taskA));

        Assert.Equal(80, chip.Purpose.Length);
        Assert.EndsWith("…", chip.Purpose, StringComparison.Ordinal);
        Assert.Equal(new string('x', 79) + "…", chip.Purpose);
    }

    [Fact]
    public void Purpose_of_exactly_80_characters_is_kept_whole()
    {
        var purpose = new string('y', 80);

        var chip = Assert.Single(RyuseiTaskChips.ForTask([Trigger(1, _taskA, purpose: purpose)], _taskA));

        Assert.Equal(purpose, chip.Purpose);
    }

    [Fact]
    public void ForTask_orders_by_next_fire_then_trigger_id_with_nulls_last()
    {
        var early = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        var triggers = new[]
        {
            Trigger(5, _taskA, nextFireAt: null),
            Trigger(4, _taskA, nextFireAt: late),
            Trigger(3, _taskA, nextFireAt: early),
            Trigger(2, _taskA, nextFireAt: early),
            Trigger(1, _taskA, nextFireAt: null),
            Trigger(9, _taskB, nextFireAt: early),
        };

        var chips = RyuseiTaskChips.ForTask(triggers, _taskA);

        Assert.Equal(
            [Id(2), Id(3), Id(4), Id(1), Id(5)],
            chips.Select(c => c.TriggerId).ToArray());
    }

    [Fact]
    public void ForBoard_groups_chips_by_task_and_skips_tasks_with_no_live_triggers()
    {
        var triggers = new[]
        {
            Trigger(1, _taskA, kind: RyuseiTriggerKind.Interval),
            Trigger(2, _taskB, state: RyuseiTriggerState.Paused, kind: RyuseiTriggerKind.Watcher),
            Trigger(3, _taskA, kind: RyuseiTriggerKind.Event),
            Trigger(4, Guid.Parse("00000000-0000-0000-0000-0000000000cc"), state: RyuseiTriggerState.Cancelled),
        };

        var board = RyuseiTaskChips.ForBoard(triggers);

        Assert.Equal(2, board.Count);
        Assert.Equal(2, board[_taskA].Count);
        Assert.Single(board[_taskB]);
        Assert.Equal(RyuseiTriggerKind.Watcher, board[_taskB][0].Kind);
        Assert.Equal(RyuseiTriggerState.Paused, board[_taskB][0].State);
    }

    [Fact]
    public void Empty_input_gives_no_chips()
    {
        Assert.Empty(RyuseiTaskChips.ForBoard([]));
        Assert.Empty(RyuseiTaskChips.ForTask([], _taskA));
    }
}
