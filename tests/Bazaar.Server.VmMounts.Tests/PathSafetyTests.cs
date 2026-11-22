using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>The host path jail and the mode and guest path floors. Every refusal must leave nothing attached and write a refusal row.</summary>
public sealed class PathSafetyTests
{
    private static async Task AssertRefusedAsync(MountHarness h, MountSpec spec, MountRefusalReason reason)
    {
        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(spec, MountHarness.Ct));
        Assert.Equal(reason, ex.Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
    }

    [Fact]
    public async Task A_workspace_inside_the_agents_own_folder_attaches_with_the_servers_guest_path()
    {
        using var h = new MountHarness();
        Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), "repo"));

        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), "repo")), MountHarness.Ct);

        Assert.Equal(MountMode.Rw, mount.Mode);
        Assert.Equal("/mnt/bazaar/workspace", mount.GuestPath);
        Assert.Equal(Path.Combine(h.Workspace(h.Agent), "repo"), mount.HostPath);
        var request = Assert.Single(h.Provider.Attached);
        Assert.Equal(mount.HostPath, request.HostPath);
        Assert.Equal(0, mount.CanaryCount);
        Assert.Single(h.Store.All, r => r.DetachedAt is null);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Attach));
    }

    [Theory]
    [InlineData("{ws}/../soul")]
    [InlineData("{ws}/sub/../../soul")]
    [InlineData("{ws}/./sub")]
    [InlineData("workspace")]
    [InlineData("./workspace")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ws}/bad\0name")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("//server/share/x")]
    [InlineData("\\\\?\\C:\\agents\\x")]
    [InlineData("{ws}/sub.")]
    [InlineData("{ws}/sub ")]
    [InlineData("{ws}/CON")]
    [InlineData("{ws}/nul.txt")]
    [InlineData("{ws}/PROGRA~1")]
    [InlineData("{ws}/file:stream")]
    [InlineData("{ws}\\sub")]
    public async Task Malformed_traversing_or_aliasing_host_paths_are_invalid(string template)
    {
        using var h = new MountHarness();
        var path = template.Replace("{ws}", h.Workspace(h.Agent), StringComparison.Ordinal);

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: path), MountRefusalReason.InvalidPath);
    }

    [Fact]
    public async Task A_drive_letter_path_is_not_a_host_path_on_a_posix_host()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows a drive-letter path is a real absolute path and is refused as outside the agent root instead.");
        using var h = new MountHarness();

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: "C:\\agents\\x\\workspace"), MountRefusalReason.InvalidPath);
    }

    [Fact]
    public async Task A_path_outside_the_agents_root_is_refused()
    {
        using var h = new MountHarness();

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: h.Outside), MountRefusalReason.OutsideAgentRoot);
    }

    [Fact]
    public async Task The_agents_root_and_the_agents_folder_are_not_mountable()
    {
        using var h = new MountHarness();

        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: h.AgentsRoot), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.OutsideAgentRoot, ex.Reason);
        ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: h.AgentRoot(h.Agent)), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.OutsideAgentRoot, ex.Reason);
    }

    [Fact]
    public async Task A_soul_folder_cannot_be_mounted_as_a_workspace_and_the_reverse()
    {
        using var h = new MountHarness();

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: h.Soul(h.Agent)), MountRefusalReason.OutsideAgentRoot);
    }

    [Fact]
    public async Task Another_agents_folder_is_denied()
    {
        using var h = new MountHarness();
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: h.Workspace(other)), MountRefusalReason.DeniedPath);
    }

    [Theory]
    [InlineData(".ssh")]
    [InlineData("secrets")]
    [InlineData("audit")]
    [InlineData(".gnupg")]
    [InlineData(".aws")]
    public async Task Denied_folder_names_are_refused(string name)
    {
        using var h = new MountHarness();
        Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), name));

        await AssertRefusedAsync(
            h, h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), name)), MountRefusalReason.DeniedPath);
    }

    [Fact]
    public async Task The_deny_list_and_case_folding_hold_on_a_case_insensitive_volume()
    {
        using var h = new MountHarness(caseInsensitive: true);
        Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), ".SSH"));
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(
            h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), ".SSH")), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);

        // Another agent's folder, spelled in upper case, is still that agent's folder.
        var shouted = Path.Combine(h.AgentsRoot, other.ToString("N").ToUpperInvariant(), "workspace");
        ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: shouted), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);

        // The agent's own folder in another case is matched by the folding rule, so the refusal is about existence, not about the root.
        var mixed = Path.Combine(h.AgentsRoot, h.Agent.ToString("N").ToUpperInvariant(), "WORKSPACE");
        ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: mixed), MountHarness.Ct));
        if (OperatingSystem.IsWindows())
        {
            // The real volume is case-insensitive, so the folder exists: the folding rule matched, and the mount is only refused later on content.
            Assert.NotEqual(MountRefusalReason.OutsideAgentRoot, ex.Reason);
            Assert.NotEqual(MountRefusalReason.DeniedPath, ex.Reason);
        }
        else
        {
            Assert.Equal(MountRefusalReason.InvalidPath, ex.Reason);
        }

        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task A_case_variant_is_a_different_folder_on_a_case_sensitive_volume()
    {
        using var h = new MountHarness(caseInsensitive: false);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(
            h.WorkspaceSpec(hostPath: Path.Combine(h.AgentRoot(h.Agent), "WORKSPACE")), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.OutsideAgentRoot, ex.Reason);
    }

    [Fact]
    public async Task A_configured_deny_path_is_refused_whether_it_is_the_mount_or_inside_it()
    {
        using var h = new MountHarness(denyRelativeToAgentsRoot: ["{agent}/workspace/vault"]);
        var vault = Path.Combine(h.Workspace(h.Agent), "vault");
        Directory.CreateDirectory(vault);

        // The denied folder itself.
        await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: vault), MountHarness.Ct));
        // A mount that would contain it.
        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));

        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
    }

    [Fact]
    public async Task A_symlink_that_leaves_the_agents_root_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var link = Path.Combine(h.Workspace(h.Agent), "escape");
        Directory.CreateSymbolicLink(link, h.Outside);

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: link), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_that_stays_inside_is_still_refused_as_the_mount_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var real = Path.Combine(h.Workspace(h.Agent), "real");
        Directory.CreateDirectory(real);
        var alias = Path.Combine(h.Workspace(h.Agent), "alias");
        Directory.CreateSymbolicLink(alias, real);

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: alias), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_link_in_a_parent_component_is_refused_even_though_the_leaf_is_a_real_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        Directory.CreateDirectory(Path.Combine(h.Outside, "child"));
        var link = Path.Combine(h.Workspace(h.Agent), "junction");
        Directory.CreateSymbolicLink(link, h.Outside);

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: Path.Combine(link, "child")), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_link_outside_the_agents_root_that_points_in_is_refused_as_a_link()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var entrance = Path.Combine(h.Outside, "entrance");
        Directory.CreateSymbolicLink(entrance, h.Workspace(h.Agent));

        await AssertRefusedAsync(h, h.WorkspaceSpec(hostPath: entrance), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_link_inside_the_tree_that_leaves_it_blocks_the_mount_but_one_that_stays_does_not()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var ws = h.Workspace(h.Agent);
        Directory.CreateDirectory(Path.Combine(ws, "real"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "inner"), Path.Combine(ws, "real"));

        await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);
        await h.Manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, MountHarness.Ct);

        Directory.CreateSymbolicLink(Path.Combine(ws, "leak"), h.Outside);
        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.LinkNotAllowed, ex.Reason);
    }

    [Theory]
    [InlineData("id_rsa")]
    [InlineData("deploy.pem")]
    [InlineData("tls.KEY")]
    [InlineData("store.kdbx")]
    [InlineData(".env")]
    [InlineData("sub/id_ed25519")]
    public async Task A_tree_that_holds_key_material_is_not_mounted(string relative)
    {
        using var h = new MountHarness();
        var file = Path.Combine(h.Workspace(h.Agent), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "x", MountHarness.Ct);

        await AssertRefusedAsync(h, h.WorkspaceSpec(), MountRefusalReason.ContentRefused);
    }

    [Fact]
    public async Task A_tree_too_large_to_check_is_refused()
    {
        using var h = new MountHarness(maxScanEntries: 3);
        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(h.Workspace(h.Agent), $"f{i}.txt"), "x", MountHarness.Ct);

        await AssertRefusedAsync(h, h.WorkspaceSpec(), MountRefusalReason.ContentRefused);
    }

    [Fact]
    public async Task The_soul_mount_is_always_read_only_and_the_workspace_always_read_write()
    {
        using var h = new MountHarness();

        var soul = await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.SoulSpec() with { Mode = MountMode.Rw }, MountHarness.Ct));
        var ws = await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec() with { Mode = MountMode.Ro }, MountHarness.Ct));

        Assert.Equal(MountRefusalReason.ModeNotAllowed, soul.Reason);
        Assert.Equal(MountRefusalReason.ModeNotAllowed, ws.Reason);
        Assert.Empty(h.Provider.Calls);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/")]
    [InlineData("/mnt/bazaar/workspace/../../../etc")]
    [InlineData("/mnt/bazaar/workspace/sub")]
    [InlineData("/mnt/bazaar/soul")]
    [InlineData("mnt/bazaar/workspace")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    public async Task A_guest_path_that_is_not_the_servers_choice_is_refused(string guestPath)
    {
        using var h = new MountHarness();

        await AssertRefusedAsync(h, h.WorkspaceSpec() with { GuestPath = guestPath }, MountRefusalReason.GuestPathNotAllowed);
    }

    [Fact]
    public async Task Windows_guest_paths_match_the_servers_choice_ignoring_case_and_are_stored_in_the_servers_spelling()
    {
        using var h = new MountHarness();

        var bad = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(
            h.WorkspaceSpec() with { GuestOs = GuestOs.Windows, GuestPath = @"C:\Windows\System32" }, MountHarness.Ct));
        Assert.Equal(MountRefusalReason.GuestPathNotAllowed, bad.Reason);

        var ok = await h.Manager.AttachAsync(
            h.WorkspaceSpec(h.NewVm(os: GuestOs.Windows)) with { GuestOs = GuestOs.Windows, GuestPath = @"c:\bazaar\WORKSPACE" }, MountHarness.Ct);
        Assert.Equal(@"C:\Bazaar\Workspace", ok.GuestPath);
    }
}
