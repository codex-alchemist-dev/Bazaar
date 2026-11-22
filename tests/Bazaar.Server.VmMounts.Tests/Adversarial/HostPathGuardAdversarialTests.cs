using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests.Adversarial;

/// <summary>Hostile host paths against the path jail, driven through attach: every escape is refused before any provider call.</summary>
public sealed class HostPathGuardAdversarialTests
{
    private static async Task<MountRefusedException> RefusedAsync(MountHarness h, MountSpec spec, params MountRefusalReason[] allowed)
    {
        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(spec, MountHarness.Ct));
        if (allowed.Length > 0)
            Assert.Contains(ex.Reason, allowed);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
        Assert.Single(h.Audit.OfKind(VmMountAuditActions.Refused));
        return ex;
    }

    private static string Expand(MountHarness h, string template) =>
        template.Replace("{ws}", h.Workspace(h.Agent), StringComparison.Ordinal).Replace("{root}", h.AgentsRoot, StringComparison.Ordinal);

    [Theory]
    [InlineData("{ws}/..")]
    [InlineData("{ws}/../workspace")]
    [InlineData("{ws}/../../..")]
    [InlineData("{ws}/a/../../soul")]
    [InlineData("{ws}/.")]
    [InlineData("{ws}/./.")]
    [InlineData("{ws}/sub/./x")]
    [InlineData("{ws}/sub.")]
    [InlineData("{ws}/sub ")]
    [InlineData("{ws}/sub /x")]
    [InlineData("{ws}/sub./x")]
    [InlineData("{ws}//../soul")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\server\\share\\agents")]
    [InlineData("//server/share")]
    [InlineData("//server/share/agents")]
    [InlineData("\\\\?\\C:\\")]
    [InlineData("\\\\?\\UNC\\server\\share")]
    [InlineData("\\\\.\\pipe\\bazaar")]
    [InlineData("//./pipe/bazaar")]
    [InlineData("C:foo")]
    [InlineData("C:")]
    [InlineData("c:foo\\bar")]
    [InlineData("{ws}/file:stream")]
    [InlineData("{ws}/file::$DATA")]
    [InlineData("{ws}:stream")]
    [InlineData("{ws}/a\nb")]
    [InlineData("{ws}/a\tb")]
    [InlineData("{ws}/a\rb")]
    [InlineData("{ws}/a\u007Fb")]
    [InlineData("{ws}/a\u0001b")]
    [InlineData("relative/path")]
    [InlineData("~")]
    public async Task Traversal_device_stream_and_control_character_paths_are_invalid(string template)
    {
        using var h = new MountHarness();

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Expand(h, template)), MountRefusalReason.InvalidPath);
    }

    [Fact]
    public async Task A_path_of_4097_characters_is_invalid()
    {
        using var h = new MountHarness();
        var path = Expand(h, "{ws}/") + new string('a', 4097 - Expand(h, "{ws}/").Length);
        Assert.Equal(4097, path.Length);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: path), MountRefusalReason.InvalidPath);
    }

    [Fact]
    public async Task A_single_segment_over_255_characters_is_invalid()
    {
        using var h = new MountHarness();

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Expand(h, "{ws}/") + new string('a', 256)), MountRefusalReason.InvalidPath);
    }

    [Fact]
    public async Task Doubled_separators_never_reach_a_path_outside_the_workspace()
    {
        using var h = new MountHarness();
        Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), "sub"));

        // A doubled separator is only a spelling: GetFullPath folds it, so what is mounted is the canonical directory.
        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: Expand(h, "{ws}//sub")), MountHarness.Ct);

        Assert.Equal(Path.Combine(h.Workspace(h.Agent), "sub"), mount.HostPath);
        Assert.Equal(mount.HostPath, Assert.Single(h.Provider.Attached).HostPath);
    }

    [Theory]
    [InlineData("{ws}/CON")]
    [InlineData("{ws}/con")]
    [InlineData("{ws}/nul.txt")]
    [InlineData("{ws}/NUL.tar.gz")]
    [InlineData("{ws}/COM1")]
    [InlineData("{ws}/com9.dat")]
    [InlineData("{ws}/LPT9.log")]
    [InlineData("{ws}/AUX")]
    [InlineData("{ws}/PRN.x")]
    [InlineData("{ws}/sub/CON/deeper")]
    [InlineData("{ws}/PROGRA~1")]
    [InlineData("{ws}/sub/DOCUME~2")]
    [InlineData("{ws}/a|b")]
    [InlineData("{ws}/a<b")]
    [InlineData("{ws}/a?b")]
    [InlineData("{ws}/a*b")]
    [InlineData("{ws}/a\"b")]
    public async Task Windows_reserved_short_and_forbidden_names_are_invalid_on_every_host(string template)
    {
        using var h = new MountHarness();

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Expand(h, template)), MountRefusalReason.InvalidPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_agents_root_the_agent_root_and_a_bare_kind_mismatch_are_not_mountable(bool caseInsensitive)
    {
        using var h = new MountHarness(caseInsensitive: caseInsensitive);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: h.AgentsRoot), MountRefusalReason.OutsideAgentRoot);
    }

    [Fact]
    public async Task The_agent_root_itself_has_no_kind_folder_and_is_refused()
    {
        using var h = new MountHarness();

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: h.AgentRoot(h.Agent)), MountRefusalReason.OutsideAgentRoot);
    }

    [Fact]
    public async Task The_agent_root_itself_is_refused_for_a_soul_mount_too()
    {
        using var h = new MountHarness();

        await RefusedAsync(h, h.SoulSpec() with { HostPath = h.AgentRoot(h.Agent) }, MountRefusalReason.OutsideAgentRoot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_soul_path_for_a_workspace_mount_and_the_reverse_are_refused(bool caseInsensitive)
    {
        using var h = new MountHarness(caseInsensitive: caseInsensitive);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: h.Soul(h.Agent)), MountRefusalReason.OutsideAgentRoot);
    }

    [Fact]
    public async Task A_workspace_path_for_a_soul_mount_is_refused_and_no_canary_is_planted()
    {
        using var h = new MountHarness();

        await RefusedAsync(h, h.SoulSpec() with { HostPath = h.Workspace(h.Agent) }, MountRefusalReason.OutsideAgentRoot);

        Assert.False(Directory.Exists(Path.Combine(h.Workspace(h.Agent), ".canary")));
        Assert.Empty(h.Canaries.All);
    }

    [Fact]
    public async Task Another_agents_folders_and_files_are_denied_for_either_kind()
    {
        using var h = new MountHarness();
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);
        Directory.CreateDirectory(Path.Combine(h.Workspace(other), "deep"));

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(other), "deep")), MountRefusalReason.DeniedPath);
        Assert.Equal(MountRefusalReason.DeniedPath, (await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.SoulSpec() with { HostPath = h.Soul(other) }, MountHarness.Ct))).Reason);
        Assert.Equal(MountRefusalReason.DeniedPath, (await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: h.AgentRoot(other)), MountHarness.Ct))).Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
    }

    [Fact]
    public async Task A_kind_folder_that_is_a_symlink_to_another_agents_folder_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);
        Directory.Delete(h.Workspace(h.Agent));
        Directory.CreateSymbolicLink(h.Workspace(h.Agent), h.Workspace(other));

        await RefusedAsync(h, h.WorkspaceSpec(), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_kind_folder_that_is_a_symlink_to_another_agents_folder_is_refused_for_soul_too()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);
        Directory.Delete(h.Soul(h.Agent));
        Directory.CreateSymbolicLink(h.Soul(h.Agent), h.Soul(other));

        await RefusedAsync(h, h.SoulSpec(), MountRefusalReason.LinkNotAllowed);
        Assert.Empty(h.Canaries.All);
        Assert.False(Directory.Exists(Path.Combine(h.Soul(other), ".canary")));
    }

    [Fact]
    public async Task The_agent_root_itself_being_a_symlink_to_another_agent_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);
        Directory.Delete(h.AgentRoot(h.Agent), recursive: true);
        Directory.CreateSymbolicLink(h.AgentRoot(h.Agent), h.AgentRoot(other));

        await RefusedAsync(h, h.WorkspaceSpec(), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_in_the_middle_of_the_path_that_points_back_inside_is_still_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var real = Path.Combine(h.Workspace(h.Agent), "real");
        Directory.CreateDirectory(Path.Combine(real, "child"));
        Directory.CreateSymbolicLink(Path.Combine(h.Workspace(h.Agent), "alias"), real);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), "alias", "child")), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_at_the_leaf_to_a_file_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var file = Path.Combine(h.Outside, "secret.txt");
        await File.WriteAllTextAsync(file, "x", MountHarness.Ct);
        var link = Path.Combine(h.Workspace(h.Agent), "filelink");
        File.CreateSymbolicLink(link, file);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: link), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_broken_symlink_at_the_leaf_is_refused_as_a_link()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var link = Path.Combine(h.Workspace(h.Agent), "dangling");
        Directory.CreateSymbolicLink(link, Path.Combine(h.Outside, "does-not-exist"));

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: link), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_loop_at_the_leaf_is_refused_without_hanging()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var a = Path.Combine(h.Workspace(h.Agent), "a");
        var b = Path.Combine(h.Workspace(h.Agent), "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: a), MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_loop_outside_the_agents_root_is_refused_not_thrown_as_an_io_error()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var a = Path.Combine(h.Outside, "a");
        var b = Path.Combine(h.Outside, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Path.Combine(a, "x")), MountRefusalReason.LinkNotAllowed, MountRefusalReason.OutsideAgentRoot);
    }

    [Fact]
    public async Task A_missing_directory_and_a_file_are_not_mounts()
    {
        using var h = new MountHarness();
        await File.WriteAllTextAsync(Path.Combine(h.Workspace(h.Agent), "file.txt"), "x", MountHarness.Ct);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), "file.txt")), MountRefusalReason.InvalidPath);
    }

    [Theory]
    [InlineData("{agent}/workspace/vault", "vault")]
    [InlineData("{agent}/workspace/VAULT", "vault")]
    [InlineData("{agent}/workspace/Vault", "vault")]
    [InlineData("{agent}/workspace", "sub")]
    [InlineData("{agent}/workspace/vault", "workspace")]
    [InlineData("{agent}/workspace/vault/inner", "vault")]
    public async Task A_deny_path_equal_to_above_or_below_the_mount_is_refused_whatever_its_case(string deny, string mountSub)
    {
        using var h = new MountHarness(denyRelativeToAgentsRoot: [deny]);
        var ws = h.Workspace(h.Agent);
        Directory.CreateDirectory(Path.Combine(ws, "vault", "inner"));
        Directory.CreateDirectory(Path.Combine(ws, "sub"));
        var target = mountSub == "workspace" ? ws : Path.Combine(ws, mountSub);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: target), MountRefusalReason.DeniedPath);
    }

    [Fact]
    public async Task A_deny_path_that_is_a_sibling_does_not_block_a_mount_and_a_different_case_of_the_mount_does_not_slip_past()
    {
        using var h = new MountHarness(caseInsensitive: false, denyRelativeToAgentsRoot: ["{agent}/workspace/Vault"]);
        Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), "vault"));

        // On a case-sensitive host "vault" is another directory than "Vault", yet the guard folds deny comparisons: refusing is the safe side.
        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), "vault")), MountRefusalReason.DeniedPath);
    }

    [Theory]
    [InlineData(false, ".ssh")]
    [InlineData(false, "Secrets")]
    [InlineData(false, "AUDIT")]
    [InlineData(false, "keys")]
    [InlineData(false, "KEYS")]
    [InlineData(false, ".GnuPG")]
    [InlineData(false, ".kube")]
    [InlineData(false, ".canary")]
    [InlineData(true, ".ssh")]
    [InlineData(true, "Secrets")]
    [InlineData(true, "AUDIT")]
    [InlineData(true, "keys")]
    [InlineData(true, ".CANARY")]
    public async Task A_denied_segment_is_refused_at_any_depth_below_the_kind_folder_in_any_case(bool caseInsensitive, string segment)
    {
        using var h = new MountHarness(caseInsensitive: caseInsensitive);
        var path = Path.Combine(h.Workspace(h.Agent), "a", "b", segment, "c");
        Directory.CreateDirectory(path);

        // The segment itself, deeper below it, and as the first segment under the kind folder.
        await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: path), MountHarness.Ct));
        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), "a", "b", segment)), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);
        Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), segment));
        ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: Path.Combine(h.Workspace(h.Agent), segment)), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.DeniedPath, ex.Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
    }

    [Fact]
    public async Task A_denied_segment_under_a_soul_mount_is_refused_too()
    {
        using var h = new MountHarness();
        var path = Path.Combine(h.Soul(h.Agent), "Secrets");
        Directory.CreateDirectory(path);

        await RefusedAsync(h, h.SoulSpec() with { HostPath = path }, MountRefusalReason.DeniedPath);
        Assert.Empty(h.Canaries.All);
    }

    [Fact]
    public async Task An_agents_root_reached_through_a_symlink_mounts_only_the_resolved_real_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var viaLink = Path.Combine(h.Root, "agents-link");
        Directory.CreateSymbolicLink(viaLink, h.AgentsRoot);
        var options = new VmMountOptions { AgentsRoot = viaLink, CaseInsensitive = false };
        var manager = new MountManager(
            h.Store, h.Canaries, h.Provider, h.Transfer, h.Sink, h.Audit, h.Policy, h.Scrubber, h.LinkProbe, h.AttachGuard, options, h.Time);

        // An operator-configured link to the agents root is resolved once; the mount root handed to the provider is the real path.
        var mount = await manager.AttachAsync(h.WorkspaceSpec(hostPath: Path.Combine(viaLink, h.Agent.ToString("N"), "workspace")), MountHarness.Ct);
        Assert.Equal(h.Workspace(h.Agent), mount.HostPath);
        await manager.DetachAsync(mount.MountId, DetachReason.Clean, MountHarness.Ct);

        // A different link to the same root is a link on the path, whatever it points at.
        var other = Path.Combine(h.Root, "second-door");
        Directory.CreateSymbolicLink(other, h.AgentsRoot);
        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => manager.AttachAsync(
            h.WorkspaceSpec(hostPath: Path.Combine(other, h.Agent.ToString("N"), "workspace")), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.LinkNotAllowed, ex.Reason);

        // And a link that points at the outside is simply outside.
        ex = await Assert.ThrowsAsync<MountRefusedException>(() => manager.AttachAsync(
            h.WorkspaceSpec(hostPath: h.Outside), MountHarness.Ct));
        Assert.Equal(MountRefusalReason.OutsideAgentRoot, ex.Reason);
    }

    [Fact]
    public async Task A_path_through_a_link_that_leads_into_the_agents_root_from_outside_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var door = Path.Combine(h.Outside, "door");
        Directory.CreateSymbolicLink(door, h.AgentsRoot);

        await RefusedAsync(h, h.WorkspaceSpec(hostPath: Path.Combine(door, h.Agent.ToString("N"), "workspace")), MountRefusalReason.LinkNotAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_variants_of_the_agent_or_kind_folder_never_match_another_agent_or_kind(bool caseInsensitive)
    {
        using var h = new MountHarness(caseInsensitive: caseInsensitive);
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);
        var shoutedOther = Path.Combine(h.AgentsRoot, other.ToString("N").ToUpperInvariant(), "workspace");
        var shoutedSoul = Path.Combine(h.AgentRoot(h.Agent), "SOUL");

        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: shoutedOther), MountHarness.Ct));
        Assert.Contains(ex.Reason, new[] { MountRefusalReason.DeniedPath, MountRefusalReason.OutsideAgentRoot, MountRefusalReason.InvalidPath });
        ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(h.WorkspaceSpec(hostPath: shoutedSoul), MountHarness.Ct));
        Assert.Contains(ex.Reason, new[] { MountRefusalReason.OutsideAgentRoot, MountRefusalReason.InvalidPath });
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
    }
}
