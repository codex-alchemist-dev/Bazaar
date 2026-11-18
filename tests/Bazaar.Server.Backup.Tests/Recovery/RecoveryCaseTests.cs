using Bazaar.Server.Backup.Recovery;

namespace Bazaar.Server.Backup.Tests.Recovery;

public sealed class RecoveryCaseTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] _devices = ["device-a", "device-b"];
    private static readonly string[] _backups = ["backup-1", "backup-2", "backup-3"];

    private sealed class FakeVerifier(params string[] validMembers) : IHardwareKeyAssertionVerifier
    {
        public bool Verify(string caseId, string memberId, ReadOnlyMemory<byte> assertion) => validMembers.Contains(memberId);
    }

    private static readonly FakeVerifier _verifier = new("backup-1", "backup-2", "backup-3");

    private static RecoveryCase Waiting(QuorumPolicy? policy = null)
    {
        var init = RecoveryCase.Initiate("case-1", policy ?? QuorumPolicy.Default, "backup-1", _devices, _backups, _t0);
        return init.Case.StartTimeLock(_t0).Case;
    }

    private static RecoveryCase WithAssertions(RecoveryCase c, params string[] members)
    {
        foreach (var m in members)
        {
            c = c.AcceptAssertion(m, new byte[] { 1 }, _verifier, _t0.AddHours(1)).Case;
        }

        return c;
    }

    [Fact]
    public void Default_policy_is_valid_and_documented()
    {
        var p = QuorumPolicy.Default;
        Assert.Equal(3, p.BackupOwnerCount);
        Assert.Equal(2, p.Threshold);
        Assert.Equal(TimeSpan.FromHours(72), p.RecoveryTimeLock);
        _ = QuorumPolicy.Create(p.BackupOwnerCount, p.Threshold, p.RecoveryTimeLock);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(5, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 4)]
    [InlineData(1, 2)]
    [InlineData(0, 1)]
    public void Policy_refuses_bad_threshold_or_count(int n, int k)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuorumPolicy.Create(n, k, TimeSpan.FromHours(48)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 3)]
    public void Policy_accepts_valid_threshold(int n, int k)
    {
        Assert.Equal(k, QuorumPolicy.Create(n, k, TimeSpan.FromHours(48)).Threshold);
    }

    [Fact]
    public void Policy_enforces_time_lock_floor_and_ceiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuorumPolicy.Create(3, 2, TimeSpan.FromHours(24) - TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuorumPolicy.Create(3, 2, TimeSpan.FromDays(30) + TimeSpan.FromTicks(1)));
        Assert.Equal(TimeSpan.FromHours(24), QuorumPolicy.Create(3, 2, QuorumPolicy.MinimumRecoveryTimeLock).RecoveryTimeLock);
        Assert.Equal(TimeSpan.FromDays(30), QuorumPolicy.Create(3, 2, QuorumPolicy.MaximumRecoveryTimeLock).RecoveryTimeLock);
    }

    [Fact]
    public void Initiate_emits_event_and_alerts_every_owner_device_and_backup_owner()
    {
        var r = RecoveryCase.Initiate("case-1", QuorumPolicy.Default, "backup-1", _devices, _backups, _t0);

        Assert.True(r.Succeeded);
        Assert.Equal(RecoveryState.Initiated, r.Case.State);
        Assert.Equal("recovery.initiated", Assert.Single(r.Events).Kind);
        Assert.Equal(2, r.Alerts.Count(a => a.Target == RecoveryAlertTarget.OwnerDevice));
        Assert.Equal(3, r.Alerts.Count(a => a.Target == RecoveryAlertTarget.BackupOwner));
        Assert.All(r.Alerts, a => Assert.Equal(_t0 + QuorumPolicy.Default.RecoveryTimeLock, a.EarliestCompletion));
    }

    [Fact]
    public void Initiate_requires_initiating_member()
    {
        Assert.Throws<ArgumentException>(() => RecoveryCase.Initiate("case-1", QuorumPolicy.Default, " ", _devices, _backups, _t0));
    }

    [Fact]
    public void StartTimeLock_moves_to_waiting_and_emits_event()
    {
        var init = RecoveryCase.Initiate("case-1", QuorumPolicy.Default, "backup-1", _devices, _backups, _t0);
        var r = init.Case.StartTimeLock(_t0);

        Assert.Equal(RecoveryState.Waiting, r.Case.State);
        Assert.Equal(_t0.AddHours(72), r.Case.LockEndsAt);
        Assert.Equal("recovery.lock.started", Assert.Single(r.Events).Kind);
        Assert.False(r.Case.StartTimeLock(_t0).Succeeded);
    }

    [Fact]
    public void Cannot_complete_before_lock_elapses()
    {
        var c = WithAssertions(Waiting(), "backup-1", "backup-2");

        var r = c.Complete(_t0.AddHours(72) - TimeSpan.FromTicks(1));

        Assert.False(r.Succeeded);
        Assert.Equal(RecoveryState.Waiting, r.Case.State);
        Assert.Equal("recovery.completion.refused", Assert.Single(r.Events).Kind);
        Assert.Contains("time-lock-running", r.Events[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Cannot_complete_with_fewer_than_threshold_assertions()
    {
        var c = WithAssertions(Waiting(), "backup-1");

        var r = c.Complete(_t0.AddHours(73));

        Assert.False(r.Succeeded);
        Assert.Contains("threshold-not-met", r.Events[0].Payload, StringComparison.Ordinal);
        Assert.False(c.Advance(_t0.AddHours(73)).Succeeded);
    }

    [Fact]
    public void Duplicate_member_assertions_count_once()
    {
        var c = WithAssertions(Waiting(), "backup-1", "backup-1", "backup-1");

        Assert.Single(c.AcceptedMembers);
        Assert.False(c.Complete(_t0.AddHours(100)).Succeeded);

        var dup = c.AcceptAssertion("backup-1", new byte[] { 1 }, _verifier, _t0.AddHours(2));
        Assert.Contains("\"duplicate\":true", dup.Events[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_assertion_is_rejected_and_not_counted()
    {
        var r = Waiting().AcceptAssertion("stranger", new byte[] { 1 }, _verifier, _t0.AddHours(1));

        Assert.False(r.Succeeded);
        Assert.Empty(r.Case.AcceptedMembers);
        Assert.Equal("recovery.assertion.rejected", Assert.Single(r.Events).Kind);
    }

    [Fact]
    public void Completes_after_lock_with_threshold_distinct_assertions()
    {
        var c = WithAssertions(Waiting(), "backup-1", "backup-2");
        var now = _t0.AddHours(72);

        var ready = c.Advance(now);
        Assert.Equal(RecoveryState.ReadyToComplete, ready.Case.State);
        Assert.Equal("recovery.ready", Assert.Single(ready.Events).Kind);

        var done = ready.Case.Complete(now);
        Assert.True(done.Succeeded);
        Assert.Equal(RecoveryState.Completed, done.Case.State);
        Assert.Equal("recovery.completed", Assert.Single(done.Events).Kind);
    }

    [Fact]
    public void Accepted_assertion_emits_event()
    {
        var r = Waiting().AcceptAssertion("backup-2", new byte[] { 1 }, _verifier, _t0.AddHours(1));

        Assert.True(r.Succeeded);
        Assert.Equal("recovery.assertion.accepted", Assert.Single(r.Events).Kind);
        Assert.Contains("\"memberId\":\"backup-2\"", r.Events[0].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_during_lock_moves_to_cancelled_and_blocks_completion()
    {
        var c = WithAssertions(Waiting(), "backup-1", "backup-2");

        var r = c.Cancel("device-a", _t0.AddHours(10));

        Assert.True(r.Succeeded);
        Assert.Equal(RecoveryState.Cancelled, r.Case.State);
        Assert.Equal("recovery.cancelled", Assert.Single(r.Events).Kind);
        Assert.False(r.Case.Complete(_t0.AddHours(100)).Succeeded);
        Assert.False(r.Case.AcceptAssertion("backup-3", new byte[] { 1 }, _verifier, _t0.AddHours(11)).Succeeded);
        Assert.False(r.Case.Cancel("device-b", _t0.AddHours(12)).Succeeded);
    }

    [Fact]
    public void Completed_case_cannot_be_cancelled()
    {
        var c = WithAssertions(Waiting(), "backup-1", "backup-2");
        var done = c.Complete(_t0.AddHours(72)).Case;

        var r = done.Cancel("device-a", _t0.AddHours(73));

        Assert.False(r.Succeeded);
        Assert.Equal(RecoveryState.Completed, r.Case.State);
        Assert.Equal("recovery.cancel.refused", Assert.Single(r.Events).Kind);
    }

    [Fact]
    public void Every_transition_emits_events_with_case_id()
    {
        var init = RecoveryCase.Initiate("case-1", QuorumPolicy.Default, "backup-1", _devices, _backups, _t0);
        var lockStart = init.Case.StartTimeLock(_t0);
        var acc = lockStart.Case.AcceptAssertion("backup-1", new byte[] { 1 }, _verifier, _t0);
        var acc2 = acc.Case.AcceptAssertion("backup-2", new byte[] { 1 }, _verifier, _t0);
        var done = acc2.Case.Complete(_t0.AddHours(72));

        foreach (var t in new[] { init, lockStart, acc, acc2, done })
        {
            Assert.NotEmpty(t.Events);
            Assert.All(t.Events, e => Assert.Contains("\"caseId\":\"case-1\"", e.Payload, StringComparison.Ordinal));
        }
    }
}
