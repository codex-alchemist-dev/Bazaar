using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Settings;

/// <summary>The write path: floor checks run first, refused writes persist nothing, and each accepted write is audited once.</summary>
public sealed class FloorCheckingSettingStoreTests
{
    private const string _taskScope = "security.approval.task_scope_minutes";
    private const string _tier3 = "security.tier3.actions";
    private const string _ownBranch = "security.push.own_branch_untainted_tier";
    private const string _tier2 = "security.approval.tier2_when_untainted";

    private static readonly SettingActor _owner = new("owner:dom", SettingRole.Owner);

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    /// <summary>A store over the security keys with no approval gate, so accepted writes can be seen.</summary>
    private static (FloorCheckingSettingStore Store, InMemorySettingStore Inner, InMemorySettingAuditSink Sink) Open(IEnumerable<SettingDefinition>? extra = null)
    {
        var sink = new InMemorySettingAuditSink();
        var definitions = SecuritySettings.Build(ApprovalTier.None);
        if (extra is not null)
            definitions = [.. definitions, .. extra];
        var inner = new InMemorySettingStore(new SettingRegistry(definitions), sink);
        return (new FloorCheckingSettingStore(inner, sink), inner, sink);
    }

    [Theory]
    [InlineData(_tier3, """["captcha","sms","id","recovery_codes","legal_terms"]""")]
    [InlineData(_tier3, """["captcha"]""")]
    [InlineData(_taskScope, "481")]
    [InlineData(_taskScope, "0")]
    [InlineData(_ownBranch, "-1")]
    [InlineData(_tier2, "\"never\"")]
    public async Task A_loosening_write_is_refused_stores_nothing_and_is_audited_once(string key, string proposed)
    {
        var (store, inner, sink) = Open();

        var result = await store.SetJsonAsync(key, Json(proposed), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.NotNull(result.Reason);
        Assert.Null(inner.StoredJson(key));
        var entry = Assert.Single(sink.Entries);
        Assert.Equal(SettingWriteOutcome.Rejected, entry.Outcome);
        Assert.Equal(key, entry.Key);
        Assert.Equal(_owner.Id, entry.ActorId);
        Assert.Equal(proposed, entry.NewValueJson);
        Assert.NotNull(entry.Reason);
    }

    [Fact]
    public async Task A_refused_write_leaves_the_stored_value_unchanged()
    {
        var (store, inner, sink) = Open();
        Assert.Equal(SettingWriteOutcome.Accepted, (await store.SetJsonAsync(_taskScope, Json("30"), _owner, "", TestContext.Current.CancellationToken)).Outcome);

        var refused = await store.SetJsonAsync(_taskScope, Json("9999"), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, refused.Outcome);
        Assert.Equal("30", inner.StoredJson(_taskScope));
        Assert.Equal(2, sink.Entries.Count);
    }

    [Fact]
    public async Task A_tightening_write_is_accepted_and_audited_once_with_old_and_new_values()
    {
        var (store, inner, sink) = Open();

        var result = await store.SetJsonAsync(_taskScope, Json("30"), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Accepted, result.Outcome);
        Assert.Equal("30", inner.StoredJson(_taskScope));
        var entry = Assert.Single(sink.Entries);
        Assert.Equal(SettingWriteOutcome.Accepted, entry.Outcome);
        Assert.Equal("60", entry.OldValueJson);
        Assert.Equal("30", entry.NewValueJson);
        Assert.Equal(_owner.Id, entry.ActorId);
        Assert.Null(entry.Reason);
    }

    [Fact]
    public async Task A_repeat_of_the_stored_value_is_accepted_without_a_second_audit_entry()
    {
        var (store, _, sink) = Open();
        Assert.Equal(SettingWriteOutcome.Accepted, (await store.SetJsonAsync(_taskScope, Json("30"), _owner, "", TestContext.Current.CancellationToken)).Outcome);

        var repeat = await store.SetJsonAsync(_taskScope, Json("30"), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Accepted, repeat.Outcome);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public async Task A_refused_credential_mount_key_is_refused_and_not_audited()
    {
        var (store, inner, sink) = Open();

        var result = await store.SetJsonAsync("security.mount.credentials", Json("true"), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Null(inner.StoredJson("security.mount.credentials"));
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task An_unknown_security_key_is_refused_and_not_audited()
    {
        var (store, inner, sink) = Open();

        var result = await store.SetJsonAsync("security.approval.bypass", Json("true"), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Null(inner.StoredJson("security.approval.bypass"));
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task A_typed_write_runs_the_floor_check_too()
    {
        var tier3 = new SettingKey<IReadOnlyList<Tier3Action>>(_tier3, SecuritySettings.DefaultTier3Actions, SettingScope.Install, SettingRole.Owner, "Test copy.");
        var sink = new InMemorySettingAuditSink();
        var inner = new InMemorySettingStore(new SettingRegistry([tier3]), sink);
        var store = new FloorCheckingSettingStore(inner, sink);

        var result = await store.SetAsync(tier3, [Tier3Action.Captcha], _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Null(inner.StoredJson(_tier3));
        Assert.Equal(SettingWriteOutcome.Rejected, Assert.Single(sink.Entries).Outcome);
    }

    [Fact]
    public async Task A_non_security_key_passes_straight_through()
    {
        var skimmer = new SettingKey<int>("skimmer.scored_wakes_per_hour", 20, SettingScope.Agent, SettingRole.Owner, "Test copy.");
        var (store, inner, sink) = Open([skimmer]);

        var result = await store.SetJsonAsync("skimmer.scored_wakes_per_hour", Json("5"), _owner, "agent:charles", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Accepted, result.Outcome);
        Assert.Equal("5", inner.StoredJson("skimmer.scored_wakes_per_hour", "agent:charles"));
        Assert.Single(sink.Entries);
    }

    [Fact]
    public async Task Shipped_security_keys_hold_an_accepted_tightening_for_approval_and_store_nothing()
    {
        var sink = new InMemorySettingAuditSink();
        var inner = new InMemorySettingStore(new SettingRegistry(SecuritySettings.All), sink);
        var store = new FloorCheckingSettingStore(inner, sink);

        var result = await store.SetJsonAsync(_taskScope, Json("30"), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.PendingApproval, result.Outcome);
        Assert.Equal(ApprovalTier.Tier2, result.ApprovalTier);
        Assert.Null(inner.StoredJson(_taskScope));
        Assert.Equal(SettingWriteOutcome.PendingApproval, Assert.Single(sink.Entries).Outcome);
    }

    [Fact]
    public async Task A_refused_floor_write_on_a_shipped_key_is_refused_before_the_approval_hold()
    {
        var sink = new InMemorySettingAuditSink();
        var inner = new InMemorySettingStore(new SettingRegistry(SecuritySettings.All), sink);
        var store = new FloorCheckingSettingStore(inner, sink);

        var result = await store.SetJsonAsync(_ownBranch, Json("7"), _owner, "", TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Equal(ApprovalTier.None, result.ApprovalTier);
        Assert.Null(inner.StoredJson(_ownBranch));
    }
}
