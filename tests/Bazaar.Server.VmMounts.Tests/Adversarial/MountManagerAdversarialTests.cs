using System.Text;
using System.Text.Json;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests.Adversarial;

/// <summary>The manager under a hostile or confused caller and guest: cross-agent paths, taint, canaries, repeats, foreign ids.</summary>
public sealed class MountManagerAdversarialTests
{
    [Fact]
    public async Task Attaching_for_agent_A_a_host_path_under_agent_B_is_refused_and_audited_with_the_reason()
    {
        using var h = new MountHarness();
        var b = Guid.CreateVersion7();
        h.CreateAgent(b);
        await File.WriteAllTextAsync(Path.Combine(h.Workspace(b), "b-private.txt"), "b", MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: h.Workspace(b)), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
        var row = Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
        using var doc = JsonDocument.Parse(row.Payload);
        Assert.Equal(h.Agent.ToString(), doc.RootElement.GetProperty("agent_id").GetString());
        Assert.Equal("DeniedPath", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Attaching_for_agent_A_the_soul_of_agent_B_plants_no_canaries_in_B()
    {
        using var h = new MountHarness();
        var b = Guid.CreateVersion7();
        h.CreateAgent(b);

        await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.SoulSpec() with { HostPath = h.Soul(b) }, MountHarness.Ct));

        Assert.False(Directory.Exists(Path.Combine(h.Soul(b), ".canary")));
        Assert.Empty(h.Canaries.All);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
    }

    [Fact]
    public async Task The_refusal_row_never_carries_a_raw_control_character_from_a_hostile_path()
    {
        using var h = new MountHarness();

        await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: h.Workspace(h.Agent) + "/evil\u001b[2Jname\nforged: line"), MountHarness.Ct));

        var row = Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
        Assert.DoesNotContain('\u001b', row.Payload);
        Assert.DoesNotContain('\n', row.Payload);
    }

    [Fact]
    public async Task A_hard_taint_detach_does_not_write_the_profile_back_and_never_reads_from_the_guest()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/Default/Cookies", Encoding.UTF8.GetBytes("planted-by-guest"));
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/Default/Extra", Encoding.UTF8.GetBytes("new"));

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.HardTaint, MountHarness.Ct);

        Assert.True(result.Detached);
        Assert.Equal(WriteBackOutcome.NotAttempted, result.WriteBack);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
        Assert.False(h.HostProfileExists("Default/Extra"));
        Assert.Equal(0, h.Channel.ListCalls);
        Assert.Equal(0, h.Channel.GetCalls);
        Assert.Empty(h.Audit.OfKind(VmMountAuditActions.WriteBack));
        Assert.False(Directory.Exists(Path.Combine(h.AgentRoot(h.Agent), "staging")) && Directory.EnumerateFileSystemEntries(Path.Combine(h.AgentRoot(h.Agent), "staging")).Any());
    }

    [Fact]
    public async Task A_kill_switch_detach_does_not_write_back_either_and_an_explicit_write_back_after_it_is_refused()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/Default/Cookies", Encoding.UTF8.GetBytes("planted"));

        await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct);
        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct));

        Assert.Equal(MountRefusalReason.NotAttached, ex.Reason);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
    }

    [Fact]
    public async Task A_hostile_guest_listing_with_a_file_and_folder_clash_is_rejected_at_write_back_and_the_host_profile_survives()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/a", [1]);
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/a/b", [2]);

        var result = await h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct);

        Assert.Equal(WriteBackOutcome.Rejected, result.Outcome);
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.WriteBackRejected));
        var staging = Path.Combine(h.AgentRoot(h.Agent), "staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public async Task A_staging_folder_that_is_a_symlink_makes_the_write_back_reject_and_writes_nothing_through_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/Default/Cookies", Encoding.UTF8.GetBytes("new"));
        Directory.CreateSymbolicLink(Path.Combine(h.AgentRoot(h.Agent), "staging"), h.Outside);

        var result = await h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct);

        Assert.NotEqual(WriteBackOutcome.Written, result.Outcome);
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Outside));
        Assert.Equal("trusted", h.ReadHostProfile("Default/Cookies"));
    }

    [Fact]
    public async Task A_canary_read_reported_by_the_provider_reaches_the_sink_and_the_audit_log()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var at = DateTimeOffset.UtcNow;

        Assert.True(await h.Provider.RaiseCanaryReadAsync(mount.MountId, ".canary/credentials.json", at));

        var report = Assert.Single(h.Sink.Reports);
        Assert.Equal(mount.MountId, report.MountId);
        Assert.Equal(h.Agent, report.AgentId);
        Assert.Equal(h.Vm, report.VmId);
        Assert.Equal(CanaryUseKind.FileRead, report.Kind);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.CanaryUse));
    }

    [Fact]
    public async Task A_read_of_a_path_that_is_not_a_canary_is_not_reported()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        await h.Provider.RaiseCanaryReadAsync(mount.MountId, "../../etc/passwd", DateTimeOffset.UtcNow);
        await h.Provider.RaiseCanaryReadAsync(mount.MountId, ".canary/not-planted.txt", DateTimeOffset.UtcNow);

        Assert.Empty(h.Sink.Reports);
    }

    [Fact]
    public async Task A_canary_token_seen_after_the_detach_is_still_reported()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var token = (await File.ReadAllTextAsync(Path.Combine(h.Soul(h.Agent), ".canary", "credentials.json"), MountHarness.Ct)).Split('"')[3];
        await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.True(await h.Manager.ReportCanaryTokenUseAsync(token, "egress\u001b[31m\nforged", MountHarness.Ct));
        Assert.False(await h.Manager.ReportCanaryTokenUseAsync("bzc_not-a-real-token", "egress", MountHarness.Ct));

        var report = Assert.Single(h.Sink.Reports);
        Assert.True(report.Retired);
        Assert.DoesNotContain('\u001b', report.Source);
        Assert.DoesNotContain('\n', report.Source);
    }

    [Fact]
    public async Task A_second_attach_of_the_same_kind_on_the_same_vm_is_refused_and_leaves_the_first_intact()
    {
        using var h = new MountHarness();
        var first = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.AlreadyAttached, ex.Reason);
        Assert.Single(h.Store.All, r => r.DetachedAt is null);
        Assert.Equal(first.MountId, Assert.Single(h.Store.All).Id);
        Assert.Single(h.Provider.Attached);
    }

    [Fact]
    public async Task A_second_soul_attach_is_refused_without_replanting_canaries_or_touching_the_guest_copy()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "trusted");
        await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var canaries = h.Canaries.All.Select(c => c.Fingerprint).ToArray();
        var puts = h.Channel.PutCalls;

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.AlreadyAttached, ex.Reason);
        Assert.Equal(canaries, h.Canaries.All.Select(c => c.Fingerprint).ToArray());
        Assert.Equal(puts, h.Channel.PutCalls);
    }

    [Fact]
    public async Task The_same_agents_soul_cannot_be_attached_to_a_second_vm_at_the_same_time()
    {
        using var h = new MountHarness();
        await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var second = h.NewVm();

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.SoulSpec(second), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.VmNotEligible, ex.Reason);
    }

    [Fact]
    public async Task Concurrent_double_attach_yields_exactly_one_mount()
    {
        using var h = new MountHarness();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
                return true;
            }
            catch (MountRefusedException)
            {
                return false;
            }
        }));

        Assert.Equal(1, attempts.Count(a => a));
        Assert.Single(h.Store.All, r => r.DetachedAt is null);
        Assert.Single(h.Provider.Attached);
    }

    [Fact]
    public async Task Detaching_an_unknown_mount_id_is_refused_and_touches_nothing()
    {
        using var h = new MountHarness();
        var mine = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.DetachAsync(Guid.CreateVersion7(), DetachReason.Clean, MountHarness.Ct));
        var ex2 = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.DetachAsync(Guid.Empty, DetachReason.HardTaint, MountHarness.Ct));

        Assert.Equal(MountRefusalReason.NotAttached, ex.Reason);
        Assert.Equal(MountRefusalReason.NotAttached, ex2.Reason);
        Assert.Null(Assert.Single(h.Store.All, r => r.Id == mine.MountId).DetachedAt);
        Assert.DoesNotContain(h.Provider.Calls, c => c.StartsWith("detach", StringComparison.Ordinal));
        Assert.Empty(h.Scrubber.Requests);
    }

    [Fact]
    public async Task Detaching_all_of_one_agent_never_detaches_another_agents_mounts()
    {
        using var h = new MountHarness();
        var b = Guid.CreateVersion7();
        h.CreateAgent(b);
        var bVm = h.NewVm(b);
        var mineMount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        var theirs = await h.Manager.AttachAsync(h.WorkspaceSpec(bVm, b), MountHarness.Ct);

        var results = await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct);

        Assert.Equal(mineMount.MountId, Assert.Single(results).MountId);
        Assert.Null(Assert.Single(h.Store.All, r => r.Id == theirs.MountId).DetachedAt);
        Assert.DoesNotContain(h.Scrubber.Requests, r => r.AgentId == b);
    }

    [Fact]
    public async Task A_repeated_detach_is_idempotent_and_does_not_scrub_or_call_the_provider_twice()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);
        var calls = h.Provider.Calls.Count;

        var again = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.True(again.WasAlreadyDetached);
        Assert.Equal(calls, h.Provider.Calls.Count);
    }

    [Fact]
    public async Task A_write_back_for_a_workspace_mount_or_an_unknown_id_is_refused()
    {
        using var h = new MountHarness();
        var ws = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        var a = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.WriteBackProfileAsync(ws.MountId, MountHarness.Ct));
        var b = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.WriteBackProfileAsync(Guid.CreateVersion7(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.NotAttached, a.Reason);
        Assert.Equal(MountRefusalReason.NotAttached, b.Reason);
        Assert.Equal(0, h.Channel.ListCalls);
    }

    [Fact]
    public async Task A_workspace_that_holds_a_canary_named_folder_with_an_escaping_link_never_reaches_the_provider()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var folder = Path.Combine(h.Workspace(h.Agent), ".canary");
        Directory.CreateDirectory(folder);
        Directory.CreateSymbolicLink(Path.Combine(folder, "door"), h.Outside);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.LinkNotAllowed, ex.Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
    }
}
