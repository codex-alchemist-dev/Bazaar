using System.Text;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests;

public sealed class MountManagerTests
{
    private const string _guestProfile = MountHarness.LinuxProfile;

    [Fact]
    public async Task Soul_attach_plants_canaries_copies_the_profile_in_and_audits()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "cookie-bytes");
        h.WriteHostProfile("Local State", "state");

        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        Assert.Equal(MountMode.Ro, mount.Mode);
        Assert.Equal(2, mount.CanaryCount);
        Assert.Equal(2, mount.ProfileFilesCopied);
        Assert.Equal(Encoding.UTF8.GetBytes("cookie-bytes"), h.Channel.Files[$"{_guestProfile}/Default/Cookies"]);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(h.Soul(h.Agent), ".canary")).Length);
        Assert.Contains($"watch:{mount.MountId}", h.Provider.Calls);

        var audit = Assert.Single(h.Audit.OfKind(VmMountAuditActions.Attach));
        Assert.Equal(VmMountAuditActions.Actor, audit.Actor);
        Assert.Contains(mount.MountId.ToString(), audit.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canary_values_are_random_per_attach_and_never_appear_in_the_audit_log()
    {
        using var h = new MountHarness();

        await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var first = await File.ReadAllTextAsync(Path.Combine(h.Soul(h.Agent), ".canary", "credentials.json"), MountHarness.Ct);
        await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct);
        await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var second = await File.ReadAllTextAsync(Path.Combine(h.Soul(h.Agent), ".canary", "credentials.json"), MountHarness.Ct);

        Assert.NotEqual(first, second);
        var token = second.Split('"')[3];
        Assert.StartsWith("bzc_", token, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Audit.Entries, e => e.Payload.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_attach_on_the_same_guest_path_is_refused_and_exposes_nothing_more()
    {
        using var h = new MountHarness();
        await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.AlreadyAttached, ex.Reason);
        Assert.Single(h.Provider.Calls);
        Assert.Single(h.Store.All);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
    }

    [Fact]
    public async Task The_same_guest_path_can_be_attached_again_after_a_detach()
    {
        using var h = new MountHarness();
        var first = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await h.Manager.DetachAsync(first.MountId, DetachReason.Clean, MountHarness.Ct);

        var second = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        Assert.NotEqual(first.MountId, second.MountId);
    }

    [Fact]
    public async Task Clean_detach_writes_the_guest_profile_back_and_removes_the_canaries()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "old");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{_guestProfile}/Default/Cookies", Encoding.UTF8.GetBytes("fresh-session"));
        h.Channel.GuestWrite($"{_guestProfile}/Default/New", Encoding.UTF8.GetBytes("added"));

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.True(result.Detached);
        Assert.Equal(WriteBackOutcome.Written, result.WriteBack);
        Assert.Equal("fresh-session", h.ReadHostProfile("Default/Cookies"));
        Assert.Equal("added", h.ReadHostProfile("Default/New"));
        Assert.False(Directory.Exists(Path.Combine(h.Soul(h.Agent), ".canary")));
        Assert.False(Directory.Exists(Path.Combine(h.AgentRoot(h.Agent), "staging", "x")));
        Assert.Contains($"detach:{mount.MountId}", h.Provider.Calls);
        Assert.NotNull((await h.Store.GetAsync(mount.MountId, MountHarness.Ct))!.DetachedAt);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.WriteBack));
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Detach));
    }

    [Theory]
    [InlineData(DetachReason.HardTaint)]
    [InlineData(DetachReason.KillSwitch)]
    public async Task A_hard_taint_or_kill_switch_detach_never_writes_back_and_never_calls_the_guest(DetachReason reason)
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{_guestProfile}/Default/Cookies", Encoding.UTF8.GetBytes("planted-by-tainted-session"));
        var listsBefore = h.Channel.ListCalls;
        var getsBefore = h.Channel.GetCalls;
        var putsBefore = h.Channel.PutCalls;

        var results = await h.Manager.DetachAllForAgentAsync(h.Agent, reason, MountHarness.Ct);

        var result = Assert.Single(results);
        Assert.True(result.Detached);
        Assert.True(result.GuestScrubbed);
        Assert.Equal(WriteBackOutcome.NotAttempted, result.WriteBack);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
        Assert.Equal(listsBefore, h.Channel.ListCalls);
        Assert.Equal(getsBefore, h.Channel.GetCalls);
        Assert.Equal(putsBefore, h.Channel.PutCalls);
        Assert.Contains($"detach:{mount.MountId}", h.Provider.Calls);
        Assert.Empty(h.Audit.OfKind(VmMountAuditActions.WriteBack));
        Assert.Equal(reason.ToString(), System.Text.Json.JsonElement.Parse(h.Audit.OfKind(VmMountAuditActions.Detach).Single().Payload)
            .GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_profile_write_back_is_refused_once_the_mount_is_detached_by_taint()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.HardTaint, MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct));

        Assert.Equal(MountRefusalReason.NotAttached, ex.Reason);
    }

    [Fact]
    public async Task Detach_all_covers_every_vm_of_the_agent_and_only_that_agent_and_is_repeatable()
    {
        using var h = new MountHarness();
        var otherAgent = Guid.CreateVersion7();
        h.CreateAgent(otherAgent);
        var vm2 = h.NewVm();
        await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await h.Manager.AttachAsync(h.WorkspaceSpec(vm2), MountHarness.Ct);
        var bystander = await h.Manager.AttachAsync(h.WorkspaceSpec(h.NewVm(otherAgent), otherAgent), MountHarness.Ct);

        var results = await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct);
        var again = await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Detached));
        Assert.Empty(again);
        Assert.Null((await h.Store.GetAsync(bystander.MountId, MountHarness.Ct))!.DetachedAt);
    }

    [Fact]
    public async Task Detaching_an_already_detached_mount_succeeds_without_a_second_provider_call()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        var again = await h.Manager.DetachAsync(mount.MountId, DetachReason.KillSwitch, MountHarness.Ct);

        Assert.True(again.WasAlreadyDetached);
        Assert.Equal(1, h.Provider.Calls.Count(c => c.StartsWith("detach:", StringComparison.Ordinal)));
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Detach));
    }

    [Fact]
    public async Task Detach_all_keeps_going_after_one_failure_and_leaves_the_failed_mount_attached()
    {
        using var h = new MountHarness();
        var soul = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var ws = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        var failure = new IOException("share busy");
        h.Provider.FailDetach = failure;

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.HardTaint, MountHarness.Ct));

        Assert.Equal(2, ex.InnerExceptions.Count);
        // Both are still attached (the provider failed for both), and both failures were recorded.
        Assert.Null((await h.Store.GetAsync(soul.MountId, MountHarness.Ct))!.DetachedAt);
        Assert.Null((await h.Store.GetAsync(ws.MountId, MountHarness.Ct))!.DetachedAt);
        Assert.Equal(2, h.Audit.OfKind(VmMountAuditActions.DetachFailed).Count);

        h.Provider.FailDetach = null;
        var retry = await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.HardTaint, MountHarness.Ct);
        Assert.Equal(2, retry.Count);
    }

    [Fact]
    public async Task A_write_back_whose_bytes_do_not_match_the_listed_hash_is_rejected_and_the_host_profile_is_untouched()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{_guestProfile}/Default/Cookies", Encoding.UTF8.GetBytes("listed-content"));
        h.Channel.GetTransform = (_, bytes) => [.. bytes.Select(b => (byte)(b ^ 1))];

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.True(result.Detached);
        Assert.Equal(WriteBackOutcome.Rejected, result.WriteBack);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.WriteBackRejected));
        Assert.Empty(h.Audit.OfKind(VmMountAuditActions.WriteBack));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(h.AgentRoot(h.Agent), "staging")));
    }

    [Fact]
    public async Task A_guest_that_sends_more_bytes_than_it_listed_is_rejected()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("a", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GetTransform = (_, bytes) => [.. bytes, .. bytes];

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.Equal(WriteBackOutcome.Rejected, result.WriteBack);
        Assert.Equal("trusted", h.ReadHostProfile("a"));
    }

    public static TheoryData<string> HostileGuestPaths() =>
        ["../evil", "a/../../evil", "/etc/passwd", "C:/x", "a\\b", "CON", "x.", "a/b:stream", "id_rsa", "Default/server.pem", "a//b"];

    [Theory]
    [MemberData(nameof(HostileGuestPaths))]
    public async Task A_hostile_path_in_the_guest_listing_rejects_the_whole_write_back(string hostile)
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.ListTransform = listed => [.. listed, new GuestFileInfo(hostile, 1, new string('a', 64))];

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.Equal(WriteBackOutcome.Rejected, result.WriteBack);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
        Assert.False(File.Exists(Path.Combine(h.Root, "evil")));
        Assert.False(File.Exists(Path.Combine(h.AgentRoot(h.Agent), "evil")));
    }

    [Fact]
    public async Task Names_that_differ_only_by_case_are_rejected_because_they_would_collide_on_a_windows_host()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{_guestProfile}/Cookies", Encoding.UTF8.GetBytes("a"));
        h.Channel.GuestWrite($"{_guestProfile}/cookies", Encoding.UTF8.GetBytes("b"));

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.Equal(WriteBackOutcome.Rejected, result.WriteBack);
    }

    [Fact]
    public async Task A_profile_over_the_size_limit_is_rejected()
    {
        using var h = new MountHarness(limits: new ProfileLimits(10, 8, 100));
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{_guestProfile}/big", new byte[9]);

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.Equal(WriteBackOutcome.Rejected, result.WriteBack);
        Assert.False(h.HostProfileExists("big"));
    }

    [Fact]
    public async Task An_unreachable_guest_makes_the_write_back_fail_but_the_detach_still_happens()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.ListTransform = _ => throw new IOException("guest gone");

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.True(result.Detached);
        Assert.Equal(WriteBackOutcome.Failed, result.WriteBack);
        Assert.Contains($"detach:{mount.MountId}", h.Provider.Calls);
    }

    [Fact]
    public async Task An_explicit_write_back_keeps_the_mount_attached()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{_guestProfile}/Default/Cookies", Encoding.UTF8.GetBytes("mid-session"));

        var result = await h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct);

        Assert.Equal(WriteBackOutcome.Written, result.Outcome);
        Assert.Equal(1, result.FilesWritten);
        Assert.Equal("mid-session", h.ReadHostProfile("Default/Cookies"));
        Assert.Null((await h.Store.GetAsync(mount.MountId, MountHarness.Ct))!.DetachedAt);
    }

    [Fact]
    public async Task A_workspace_mount_cannot_be_written_back_as_a_profile()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct));

        Assert.Equal(MountRefusalReason.NotAttached, ex.Reason);
    }

    [Fact]
    public async Task A_canary_file_read_is_reported_to_the_sink_and_audited()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var at = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        var raised = await h.Provider.RaiseCanaryReadAsync(mount.MountId, ".canary/credentials.json", at);

        Assert.True(raised);
        var report = Assert.Single(h.Sink.Reports);
        Assert.Equal(h.Agent, report.AgentId);
        Assert.Equal(h.Vm, report.VmId);
        Assert.Equal(mount.MountId, report.MountId);
        Assert.Equal(CanaryUseKind.FileRead, report.Kind);
        Assert.Equal(at, report.At);
        Assert.Equal(64, report.CanaryId.Length);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.CanaryUse));
    }

    [Fact]
    public async Task A_read_of_a_file_that_is_not_a_canary_is_not_reported()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        await h.Provider.RaiseCanaryReadAsync(mount.MountId, "browser-profile/Default/Cookies", DateTimeOffset.UtcNow);

        Assert.Empty(h.Sink.Reports);
    }

    [Fact]
    public async Task A_canary_value_seen_elsewhere_is_reported_and_still_reported_after_the_mount_is_detached()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var json = await File.ReadAllTextAsync(Path.Combine(h.Soul(h.Agent), ".canary", "credentials.json"), MountHarness.Ct);
        var token = json.Split('"')[3];

        Assert.False(await h.Manager.ReportCanaryTokenUseAsync("not-a-canary", "egress", MountHarness.Ct));
        Assert.True(await h.Manager.ReportCanaryTokenUseAsync(token, "egress", MountHarness.Ct));

        var report = Assert.Single(h.Sink.Reports);
        Assert.Equal(CanaryUseKind.TokenSeen, report.Kind);
        Assert.Equal("egress", report.Source);
        Assert.False(report.Retired);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))), report.CanaryId);
        Assert.DoesNotContain(h.Audit.Entries, e => e.Payload.Contains(token, StringComparison.Ordinal));

        // Detaching retires the canary; it does not forget it. The value showing up later is still a leak.
        await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);
        Assert.All(h.Canaries.All, c => Assert.NotNull(c.RetiredAt));
        Assert.True(await h.Manager.ReportCanaryTokenUseAsync(token, "egress", MountHarness.Ct));
        Assert.True(h.Sink.Reports[^1].Retired);
    }

    [Fact]
    public async Task A_provider_failure_rolls_the_attach_back_completely()
    {
        using var h = new MountHarness();
        h.Provider.FailNextAttach = new IOException("share create failed");

        await Assert.ThrowsAsync<IOException>(() => h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct));

        Assert.All(h.Store.All, r => Assert.NotNull(r.DetachedAt));
        Assert.False(Directory.Exists(Path.Combine(h.Soul(h.Agent), ".canary")));
        Assert.Contains(h.Provider.Calls, c => c.StartsWith("detach:", StringComparison.Ordinal));
        var again = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        Assert.Equal(2, again.CanaryCount);
    }

    [Fact]
    public async Task A_failed_profile_copy_rolls_the_attach_back_and_detaches_the_share()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "x");
        h.Channel.FailPut = new IOException("guest down");

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(() => h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Channel, ex.Failure);
        Assert.Empty(h.Provider.Attached);
        Assert.All(h.Store.All, r => Assert.NotNull(r.DetachedAt));
        Assert.Empty(h.Audit.OfKind(VmMountAuditActions.Attach));
    }

    [Fact]
    public async Task A_failed_audit_write_rolls_the_attach_back()
    {
        using var h = new MountHarness();
        var failing = h.NewManager(audit: new ThrowingAudit());

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));

        Assert.Empty(h.Provider.Attached);
        Assert.All(h.Store.All, r => Assert.NotNull(r.DetachedAt));
    }

    [Fact]
    public async Task Concurrent_attaches_of_one_guest_path_let_exactly_one_win()
    {
        using var h = new MountHarness();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
                return (MountRefusalReason?)null;
            }
            catch (MountRefusedException ex)
            {
                return ex.Reason;
            }
        }));

        Assert.Equal(1, attempts.Count(r => r is null));
        Assert.Equal(7, attempts.Count(r => r == MountRefusalReason.AlreadyAttached));
        Assert.Single(h.Provider.Attached);
    }

    [Fact]
    public async Task Operations_on_one_vm_are_serialised_while_other_vms_run_in_parallel()
    {
        using var h = new MountHarness();
        var started = 0;
        var gate = new TaskCompletionSource();
        h.Provider.AttachGate = gate.Task;
        h.Provider.OnAttachStarted = () => Interlocked.Increment(ref started);
        var otherVm = h.NewVm();

        var first = h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 1);
        var sameVm = h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var differentVm = h.Manager.AttachAsync(h.WorkspaceSpec(otherVm), MountHarness.Ct);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 2);
        await Task.Delay(150, MountHarness.Ct);

        // The other VM started; the second operation on the first VM is still waiting for the lock.
        Assert.Equal(2, Volatile.Read(ref started));

        gate.SetResult();
        await Task.WhenAll(first, sameVm, differentVm);
        Assert.Equal(3, Volatile.Read(ref started));
        Assert.Equal(3, h.Provider.Attached.Count);
    }

    [Fact]
    public async Task A_detach_queued_behind_an_attach_runs_after_it()
    {
        using var h = new MountHarness();
        var gate = new TaskCompletionSource();
        var started = 0;
        h.Provider.AttachGate = gate.Task;
        h.Provider.OnAttachStarted = () => Interlocked.Increment(ref started);
        var attach = h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 1);
        var row = Assert.Single(h.Store.All);

        var detach = h.Manager.DetachAsync(row.Id, DetachReason.Clean, MountHarness.Ct);
        await Task.Delay(100, MountHarness.Ct);
        Assert.DoesNotContain($"detach:{row.Id}", h.Provider.Calls);

        gate.SetResult();
        await attach;
        var result = await detach;

        Assert.True(result.Detached);
        Assert.False(result.WasAlreadyDetached);
        Assert.Empty(h.Provider.Attached);
    }

    [Fact]
    public async Task A_containment_detach_cancels_an_attach_that_is_stuck_in_the_provider_and_the_attach_is_rolled_back()
    {
        using var h = new MountHarness();
        var started = 0;
        h.Provider.AttachGate = new TaskCompletionSource().Task;
        h.Provider.OnAttachStarted = () => Interlocked.Increment(ref started);
        var attach = h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 1);

        var results = await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct)
            .WaitAsync(TimeSpan.FromSeconds(5), MountHarness.Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
        Assert.Empty(h.Provider.Attached);
        Assert.All(h.Store.All, r => Assert.NotNull(r.DetachedAt));
        Assert.True(results.Count <= 1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(10, MountHarness.Ct);
        }
    }

    private sealed class ThrowingAudit : Bazaar.Server.Security.Audit.IAuditLog
    {
        public Task AppendAsync(Bazaar.Server.Security.Audit.AuditEntry e, CancellationToken ct = default) =>
            throw new InvalidOperationException("audit store down");
    }
}
