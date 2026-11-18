using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class KanbanFieldRuleTests
{
    private static (KanbanFieldRule Rule, InMemoryKanbanFieldState State, RecordingReviewSink Review) Build()
    {
        var state = new InMemoryKanbanFieldState();
        var review = new RecordingReviewSink();
        return (new KanbanFieldRule(state, new FakeTransitionChecker(), review), state, review);
    }

    private static OfflineWrite Field(string task, string field, string value, string? baseVersion, Guid? writeId = null) =>
        RuleTestKit.Write(OfflineDataClass.KanbanField, task, RuleTestKit.Json(new KanbanFieldPayload(field, value)), baseVersion, writeId);

    [Fact]
    public async Task Write_on_the_base_version_is_applied_and_bumps_the_version()
    {
        var (rule, state, _) = Build();

        var outcome = await rule.MergeAsync(Field("task-1", "title", "Write docs", "0"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("1", outcome.ServerVersion);
        var snapshot = await state.GetFieldAsync("task-1", "title", CancellationToken.None);
        Assert.Equal("Write docs", snapshot.Value);
        Assert.Equal(1, snapshot.Version);
    }

    [Fact]
    public async Task Stale_base_version_is_a_conflict_with_a_review_item()
    {
        var (rule, state, review) = Build();
        await rule.MergeAsync(Field("task-1", "title", "First", "0"), CancellationToken.None);

        var stale = await rule.MergeAsync(Field("task-1", "title", "Second", "0"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Conflict, stale.Kind);
        Assert.Equal("1", stale.ServerVersion);
        Assert.Equal("First", (await state.GetFieldAsync("task-1", "title", CancellationToken.None)).Value);
        var item = Assert.Single(review.Items);
        Assert.Equal("kanban-field-conflict", item.Kind);
    }

    [Fact]
    public async Task Missing_or_malformed_base_version_is_rejected()
    {
        var (rule, _, _) = Build();

        var missing = await rule.MergeAsync(Field("task-1", "title", "x", null), CancellationToken.None);
        var malformed = await rule.MergeAsync(Field("task-1", "title", "x", "one"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, missing.Kind);
        Assert.Equal(OfflineOutcomeKind.Rejected, malformed.Kind);
    }

    [Fact]
    public async Task Disallowed_status_transition_is_rejected_and_leaves_the_field_alone()
    {
        var (rule, state, _) = Build();
        await rule.MergeAsync(Field("task-1", "status", "Backlog", "0"), CancellationToken.None);

        var skip = await rule.MergeAsync(Field("task-1", "status", "Done", "1"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, skip.Kind);
        Assert.Contains("not allowed", skip.Reason);
        var snapshot = await state.GetFieldAsync("task-1", "status", CancellationToken.None);
        Assert.Equal("Backlog", snapshot.Value);
        Assert.Equal(1, snapshot.Version);
    }

    [Fact]
    public async Task Allowed_status_transition_is_applied()
    {
        var (rule, state, _) = Build();
        await rule.MergeAsync(Field("task-1", "status", "Backlog", "0"), CancellationToken.None);

        var ready = await rule.MergeAsync(Field("task-1", "status", "Ready", "1"), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, ready.Kind);
        Assert.Equal("Ready", (await state.GetFieldAsync("task-1", "status", CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Replayed_conflict_is_a_duplicate_and_opens_no_second_review_item()
    {
        var (rule, _, review) = Build();
        await rule.MergeAsync(Field("task-1", "title", "First", "0"), CancellationToken.None);
        var conflicting = Field("task-1", "title", "Second", "0");
        await rule.MergeAsync(conflicting, CancellationToken.None);

        var replay = await rule.MergeAsync(conflicting, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Duplicate, replay.Kind);
        Assert.Single(review.Items);
    }
}
