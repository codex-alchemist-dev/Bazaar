using System.Text;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Containment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>The guest never keeps a cookie copy outside an attached Soul mount; the attach guard; the layout read APIs.</summary>
public sealed class GuestCleanupAndLayoutTests
{
    private const string _guestProfile = MountHarness.LinuxProfile;

    private static bool GuestHoldsProfile(MountHarness h) =>
        h.Channel.Files.Keys.Any(k => k.StartsWith(_guestProfile + "/", StringComparison.Ordinal));

    [Theory]
    [InlineData(DetachReason.Clean)]
    [InlineData(DetachReason.HardTaint)]
    [InlineData(DetachReason.KillSwitch)]
    public async Task After_every_soul_detach_the_guest_holds_no_profile_copy(DetachReason reason)
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "c");
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        Assert.True(GuestHoldsProfile(h), "The copy exists while the mount is attached.");
        h.Channel.GuestWrite($"{_guestProfile}/Default/New", Encoding.UTF8.GetBytes("session"));

        var result = await h.Manager.DetachAsync(mount.MountId, reason, MountHarness.Ct);

        Assert.True(result.GuestProfileDeleted);
        Assert.False(GuestHoldsProfile(h));
        Assert.Equal(2, h.Channel.DeleteCalls);
    }

    [Fact]
    public async Task A_workspace_detach_does_not_touch_the_guest_profile()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        Assert.False(result.GuestProfileDeleted);
        Assert.Equal(0, h.Channel.DeleteCalls);
    }

    [Fact]
    public async Task A_clean_detach_reports_a_failed_guest_cleanup_after_detaching()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.FailDelete = new IOException("guest gone");

        await Assert.ThrowsAsync<ProfileTransferException>(() => h.Manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct));

        Assert.NotNull((await h.Store.GetAsync(mount.MountId, MountHarness.Ct))!.DetachedAt);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.GuestCleanupFailed));
    }

    [Fact]
    public async Task A_containment_detach_tolerates_a_failed_cleanup_because_the_scrubber_covers_it()
    {
        using var h = new MountHarness();
        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);
        h.Channel.FailDelete = new IOException("guest gone");

        var result = await h.Manager.DetachAsync(mount.MountId, DetachReason.HardTaint, MountHarness.Ct);

        Assert.True(result.Detached);
        Assert.True(result.GuestScrubbed);
        Assert.False(result.GuestProfileDeleted);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.GuestCleanupFailed));
    }

    [Fact]
    public async Task A_rolled_back_soul_attach_also_deletes_the_guest_copy()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("Default/Cookies", "c");
        h.Channel.FailPut = new IOException("guest down");

        await Assert.ThrowsAsync<ProfileTransferException>(() => h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct));

        // One delete inside the push, one in the rollback.
        Assert.Equal(2, h.Channel.DeleteCalls);
    }

    [Fact]
    public async Task The_attach_guard_runs_for_soul_attaches_before_any_provider_work_and_can_refuse()
    {
        using var h = new MountHarness();
        h.AttachGuard.Refuse = true;

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.AttachGuardRefused, ex.Reason);
        Assert.Equal(1, h.AttachGuard.Calls);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
        Assert.Empty(h.Canaries.All);
        Assert.False(Directory.Exists(Path.Combine(h.Soul(h.Agent), ".canary")));
        Assert.Equal(0, h.Channel.PutCalls + h.Channel.DeleteCalls);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
    }

    [Fact]
    public async Task The_attach_guard_is_asked_once_per_soul_attach_before_the_provider_and_not_for_workspaces()
    {
        using var h = new MountHarness();

        await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        Assert.Equal(0, h.AttachGuard.Calls);
        await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        Assert.Equal(1, h.AttachGuard.Calls);
        // The workspace attach had made one provider call by then; the Soul attach had made none of its own.
        Assert.Equal(1, h.AttachGuard.ProviderCallsSeen);
    }

    [Fact]
    public async Task The_default_attach_guard_allows_everything()
    {
        await new AllowAllMountAttachGuard().CheckSoulAttachAsync(Guid.CreateVersion7(), MountHarness.Ct);
    }

    [Fact]
    public void The_options_are_the_single_source_of_guest_layouts_and_registration_exposes_all_three_read_apis()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VmMounts:AgentsRoot"] = Path.GetFullPath(Path.GetTempPath()) })
            .Build();
        using var provider = new ServiceCollection().AddBazaarVmMounts(config).BuildServiceProvider();

        var layouts = provider.GetRequiredService<IGuestMountLayoutProvider>();

        Assert.Equal(GuestMountLayout.DefaultLinux, layouts.GetLayout(GuestOs.Linux));
        Assert.Equal(GuestMountLayout.DefaultWindows, layouts.GetLayout(GuestOs.Windows));
        Assert.IsType<AllowAllMountAttachGuard>(provider.GetRequiredService<IMountAttachGuard>());
        Assert.NotNull(provider.GetRequiredService<IWorkShareLayout>());
    }

    [Fact]
    public void A_custom_layout_is_what_the_provider_reports()
    {
        using var h = new MountHarness(windowsLayout: new GuestMountLayout(@"D:\S", @"D:\W", @"D:\P"));

        Assert.Equal(new GuestMountLayout(@"D:\S", @"D:\W", @"D:\P"), h.Options.GetLayout(GuestOs.Windows));
    }

    [Fact]
    public void The_work_share_and_the_passport_folder_never_overlap_for_any_agent()
    {
        using var h = new MountHarness();
        var layout = h.Options;
        var agents = Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7()).ToArray();

        foreach (var agent in agents)
        {
            var work = layout.WorkShareRoot(agent);
            var passport = layout.PassportFolder(agent);
            Assert.NotEqual(work, passport, StringComparer.OrdinalIgnoreCase);
            Assert.False(IsUnder(work, passport) || IsUnder(passport, work));
            Assert.Equal(h.Workspace(agent), work);
            Assert.Equal(h.Soul(agent), passport);
            foreach (var other in agents.Where(a => a != agent))
            {
                Assert.False(IsUnder(layout.WorkShareRoot(other), work) || IsUnder(work, layout.WorkShareRoot(other)));
                Assert.False(IsUnder(layout.PassportFolder(other), work) || IsUnder(work, layout.PassportFolder(other)));
                Assert.False(IsUnder(layout.WorkShareRoot(other), passport) || IsUnder(passport, layout.WorkShareRoot(other)));
            }
        }
    }

    [Theory]
    [InlineData("soul", "soul")]
    [InlineData("soul", "SOUL")]
    [InlineData("a/b", "workspace")]
    [InlineData("..", "workspace")]
    [InlineData("soul", "staging")]
    public void Folder_names_that_would_make_the_shares_overlap_or_escape_are_refused_at_start(string soul, string workspace)
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var options = new VmMountOptions { AgentsRoot = root, SoulFolderName = soul, WorkspaceFolderName = workspace };

        Assert.Throws<ArgumentException>(() => new MountManager(
            new InMemoryScopedMountStore(), new InMemoryCanaryStore(), new Providers.FakeMountProvider(), new FakeProfileTransferStub(),
            new RecordingCanarySink(), new RecordingAuditLog(), new Paths.DefaultMountContentPolicy(), new FakeGuestScrubber(),
            new FakeHardLinkProbe(), new AllowAllMountAttachGuard(), options));
    }

    private static bool IsUnder(string parent, string child) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class FakeProfileTransferStub : IProfileTransfer
    {
        public Task<ProfileManifest> PushAsync(Guid vmId, GuestOs guestOs, string hostProfileDirectory, string guestProfileDirectory, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProfileManifest> PullAsync(Guid vmId, GuestOs guestOs, string guestProfileDirectory, string hostStagingDirectory, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteGuestCopyAsync(Guid vmId, string guestProfileDirectory, CancellationToken ct) => throw new NotSupportedException();
    }
}
