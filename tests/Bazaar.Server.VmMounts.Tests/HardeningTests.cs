using System.Text;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>Canary persistence and re-arming, VM eligibility, the single profile writer, link counts, deny paths and cleaned names.</summary>
public sealed class HardeningTests
{
    [Fact]
    public async Task Canary_fingerprints_are_persisted_and_the_watches_are_re_armed_after_a_restart()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        Assert.Equal(2, h.Canaries.All.Count);

        // A new manager over the same stores is a restarted server: it has no in-memory state.
        var restarted = h.NewManager();
        Assert.Equal(1, await restarted.RearmWatchesAsync(MountHarness.Ct));

        Assert.True(await h.Provider.RaiseCanaryReadAsync(mount.MountId, ".canary/credentials.json", DateTimeOffset.UtcNow));
        var report = Assert.Single(h.Sink.Reports);
        Assert.Equal(mount.MountId, report.MountId);
        var token = (await File.ReadAllTextAsync(Path.Combine(h.Soul(h.Agent), ".canary", "credentials.json"), MountHarness.Ct)).Split('"')[3];
        Assert.True(await restarted.ReportCanaryTokenUseAsync(token, "egress", MountHarness.Ct));
    }

    [Fact]
    public async Task Re_arming_skips_detached_mounts_and_workspace_mounts()
    {
        using var h = new MountHarness();
        var soul = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await h.Manager.DetachAsync(soul.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.Equal(0, await h.NewManager().RearmWatchesAsync(MountHarness.Ct));
    }

    [Fact]
    public async Task The_canary_sink_is_called_even_when_the_audit_write_fails()
    {
        using var h = new MountHarness();
        await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var token = (await File.ReadAllTextAsync(Path.Combine(h.Soul(h.Agent), ".canary", "credentials.json"), MountHarness.Ct)).Split('"')[3];
        var manager = h.NewManager(audit: new SelectiveThrowingAudit(VmMountAuditActions.CanaryUse));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReportCanaryTokenUseAsync(token, "egress", MountHarness.Ct));

        Assert.Single(h.Sink.Reports);
    }

    [Fact]
    public async Task A_vm_of_another_agent_is_refused()
    {
        using var h = new MountHarness();
        var stranger = h.NewVm(Guid.CreateVersion7());

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(stranger), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.VmNotEligible, ex.Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
    }

    [Fact]
    public async Task A_retired_missing_or_wrong_os_vm_is_refused()
    {
        using var h = new MountHarness();
        var retired = h.NewVm(retired: true);
        var windows = h.NewVm(os: GuestOs.Windows);

        foreach (var spec in new[]
        {
            h.WorkspaceSpec(retired),
            h.WorkspaceSpec(Guid.CreateVersion7()),
            h.WorkspaceSpec(windows),
        })
        {
            var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(spec, MountHarness.Ct));
            Assert.Equal(MountRefusalReason.VmNotEligible, ex.Reason);
        }

        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task A_profile_has_one_writer_so_a_second_soul_mount_on_another_vm_is_refused_until_the_first_detaches()
    {
        using var h = new MountHarness();
        var second = h.NewVm();
        var first = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.SoulSpec(second), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.VmNotEligible, ex.Reason);
        // The workspace is shared by both VMs of the agent.
        await h.Manager.AttachAsync(h.WorkspaceSpec(second), MountHarness.Ct);

        await h.Manager.DetachAsync(first.MountId, DetachReason.Clean, MountHarness.Ct);
        await h.Manager.AttachAsync(h.SoulSpec(second), MountHarness.Ct);
    }

    [Fact]
    public async Task A_guest_os_comes_from_the_vm_row_not_from_the_guest_path()
    {
        // Both layouts name the same paths, so the path cannot tell the OS. The VM row can.
        using var h = new MountHarness(windowsLayout: new GuestMountLayout("/mnt/bazaar/soul", "/mnt/bazaar/workspace", "/var/lib/bazaar/profile"));
        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.Equal(GuestOs.Linux, Assert.Single(h.Provider.DetachedRequests).GuestOs);
    }

    [Fact]
    public async Task A_file_with_more_than_one_hard_link_is_refused_where_the_host_can_tell()
    {
        using var h = new MountHarness();
        await File.WriteAllTextAsync(Path.Combine(h.Workspace(h.Agent), "shared.txt"), "x", MountHarness.Ct);
        await File.WriteAllTextAsync(Path.Combine(h.Workspace(h.Agent), "plain.txt"), "x", MountHarness.Ct);
        h.LinkProbe.Linked.Add("shared.txt");

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.LinkNotAllowed, ex.Reason);
        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task The_deny_list_is_case_insensitive_even_on_a_case_sensitive_volume()
    {
        using var h = new MountHarness(caseInsensitive: false, denyRelativeToAgentsRoot: ["{agent}/workspace/vault"]);
        Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), "vault"));

        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), "VAULT")), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);
    }

    [Fact]
    public async Task Guest_supplied_names_are_cleaned_and_truncated_in_results_and_audit_rows()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        var hostile = "evil\n{\"forged\":true}‮" + new string('x', 500);
        h.Channel.ListTransform = listed => [.. listed, new GuestFileInfo(hostile, 1, new string('a', 64))];

        var result = await h.Manager.WriteBackProfileAsync(mount.MountId, MountHarness.Ct);

        Assert.Equal(WriteBackOutcome.Rejected, result.Outcome);
        Assert.DoesNotContain('\n', result.Detail!);
        Assert.DoesNotContain('‮', result.Detail!);
        Assert.True(result.Detail!.Length < 200);
        var audit = h.Audit.OfKind(VmMountAuditActions.WriteBackRejected).Single().Payload;
        Assert.DoesNotContain("\\n", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), audit, StringComparison.Ordinal);
        Assert.False(Encoding.UTF8.GetBytes(audit).Length > 1000);
    }
}
