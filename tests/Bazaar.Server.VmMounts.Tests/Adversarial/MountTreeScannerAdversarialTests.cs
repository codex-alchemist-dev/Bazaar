using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests.Adversarial;

/// <summary>A hostile guest controls the workspace tree. The scan (run at attach through the manager) must refuse what it can see.</summary>
public sealed class MountTreeScannerAdversarialTests
{
    private static readonly string _canary = ".canary";

    private static async Task<MountRefusedException> RefusedAsync(MountHarness h, MountRefusalReason reason, MountSpec? spec = null)
    {
        var ex = await Assert.ThrowsAsync<MountRefusedException>(() => h.Manager.AttachAsync(spec ?? h.WorkspaceSpec(), MountHarness.Ct));
        Assert.Equal(reason, ex.Reason);
        Assert.Empty(h.Provider.Calls);
        Assert.Empty(h.Store.All);
        return ex;
    }

    [Fact]
    public async Task A_symlink_to_outside_is_refused_at_the_root_and_at_depth()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var deep = Path.Combine(h.Workspace(h.Agent), "a", "b", "c");
        Directory.CreateDirectory(deep);
        Directory.CreateSymbolicLink(Path.Combine(deep, "escape"), h.Outside);

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_file_symlink_to_outside_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var target = Path.Combine(h.Outside, "host-secret");
        await File.WriteAllTextAsync(target, "x", MountHarness.Ct);
        File.CreateSymbolicLink(Path.Combine(h.Workspace(h.Agent), "notes.txt"), target);

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_to_another_agents_workspace_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var other = Guid.CreateVersion7();
        h.CreateAgent(other);
        Directory.CreateSymbolicLink(Path.Combine(h.Workspace(h.Agent), "neighbour"), h.Workspace(other));

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_to_a_parent_of_the_mount_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        Directory.CreateSymbolicLink(Path.Combine(h.Workspace(h.Agent), "up"), h.AgentRoot(h.Agent));

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_chain_that_ends_outside_is_refused_even_when_the_first_hops_stay_inside()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var ws = h.Workspace(h.Agent);
        Directory.CreateSymbolicLink(Path.Combine(ws, "hop3"), h.Outside);
        Directory.CreateSymbolicLink(Path.Combine(ws, "hop2"), Path.Combine(ws, "hop3"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "hop1"), Path.Combine(ws, "hop2"));

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_symlink_to_inside_is_allowed_and_not_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var ws = h.Workspace(h.Agent);
        Directory.CreateDirectory(Path.Combine(ws, "real"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "inner"), Path.Combine(ws, "real"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "inner2"), Path.Combine(ws, "inner"));

        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        Assert.Equal(ws, mount.HostPath);
    }

    [Fact]
    public async Task An_inside_link_named_like_key_material_is_still_judged_by_the_content_policy()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var ws = h.Workspace(h.Agent);
        Directory.CreateDirectory(Path.Combine(ws, "real"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "id_rsa"), Path.Combine(ws, "real"));

        await RefusedAsync(h, MountRefusalReason.ContentRefused);
    }

    [Fact]
    public async Task A_symlink_loop_terminates_with_a_refusal_not_an_exception_or_a_hang()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var ws = h.Workspace(h.Agent);
        Directory.CreateSymbolicLink(Path.Combine(ws, "a"), Path.Combine(ws, "b"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "b"), Path.Combine(ws, "a"));

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed).WaitAsync(TimeSpan.FromSeconds(10), MountHarness.Ct);
    }

    [Fact]
    public async Task A_self_referencing_symlink_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var ws = h.Workspace(h.Agent);
        Directory.CreateSymbolicLink(Path.Combine(ws, "me"), Path.Combine(ws, "me"));

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed).WaitAsync(TimeSpan.FromSeconds(10), MountHarness.Ct);
    }

    [Fact]
    public async Task A_symlink_back_to_the_mount_root_is_not_followed_and_does_not_loop_the_scan()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var ws = h.Workspace(h.Agent);
        Directory.CreateDirectory(Path.Combine(ws, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "sub", "again"), ws);

        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct).WaitAsync(TimeSpan.FromSeconds(10), MountHarness.Ct);

        Assert.Equal(ws, mount.HostPath);
    }

    [Fact]
    public async Task A_broken_symlink_that_points_outside_the_tree_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        Directory.CreateSymbolicLink(Path.Combine(h.Workspace(h.Agent), "dangling"), Path.Combine(h.Outside, "nowhere"));

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_file_with_more_than_one_hard_link_is_refused_where_the_probe_can_tell()
    {
        using var h = new MountHarness();
        var dir = Path.Combine(h.Workspace(h.Agent), "src", "deep");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "shared.txt"), "x", MountHarness.Ct);
        h.LinkProbe.Linked.Add("shared.txt");

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_probe_that_cannot_tell_does_not_refuse_ordinary_files()
    {
        using var h = new MountHarness();
        await File.WriteAllTextAsync(Path.Combine(h.Workspace(h.Agent), "plain.txt"), "x", MountHarness.Ct);

        var mount = await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct);

        Assert.Equal(h.Workspace(h.Agent), mount.HostPath);
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(1, 1)]
    public async Task A_tree_with_more_entries_than_the_limit_is_refused(int entries, int limit)
    {
        using var h = new MountHarness(maxScanEntries: limit);
        for (var i = 0; i < entries; i++)
            await File.WriteAllTextAsync(Path.Combine(h.Workspace(h.Agent), $"f{i}.txt"), "x", MountHarness.Ct);

        if (entries > limit)
            await RefusedAsync(h, MountRefusalReason.ContentRefused);
        else
            Assert.NotNull(await h.Manager.AttachAsync(h.WorkspaceSpec(), MountHarness.Ct));
    }

    [Fact]
    public async Task Empty_directories_count_against_the_entry_limit()
    {
        using var h = new MountHarness(maxScanEntries: 10);
        for (var i = 0; i < 50; i++)
            Directory.CreateDirectory(Path.Combine(h.Workspace(h.Agent), $"d{i}"));

        await RefusedAsync(h, MountRefusalReason.ContentRefused);
    }

    [Theory]
    [InlineData("a/b/c/d/e/id_rsa")]
    [InlineData("a/b/id_ed25519.pub.bak/x")]
    [InlineData("a/.ssh/config")]
    [InlineData("deep/.aws/credentials")]
    [InlineData("x/y/server.PEM")]
    [InlineData("x/y/store.p12")]
    [InlineData("x/.env")]
    [InlineData("x/.netrc")]
    [InlineData("x/.git-credentials")]
    [InlineData("x/Secrets/readme.txt")]
    [InlineData("x/AUDIT/log")]
    [InlineData("x/.kube/config")]
    [InlineData("x/keepass.KDBX")]
    public async Task Key_material_at_depth_is_refused_in_any_case(string relative)
    {
        using var h = new MountHarness();
        var file = Path.Combine(h.Workspace(h.Agent), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "x", MountHarness.Ct);

        await RefusedAsync(h, MountRefusalReason.ContentRefused);
    }

    [Fact]
    public async Task A_workspace_cannot_hide_a_symlink_to_outside_in_a_folder_named_like_the_canary_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        using var h = new MountHarness();
        var folder = Path.Combine(h.Workspace(h.Agent), _canary);
        Directory.CreateDirectory(folder);
        Directory.CreateSymbolicLink(Path.Combine(folder, "escape"), h.Outside);

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_workspace_cannot_hide_key_material_in_a_folder_named_like_the_canary_folder()
    {
        using var h = new MountHarness();
        var folder = Path.Combine(h.Workspace(h.Agent), _canary);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "id_rsa"), "x", MountHarness.Ct);

        await RefusedAsync(h, MountRefusalReason.ContentRefused);
    }

    [Fact]
    public async Task A_workspace_cannot_hide_a_hard_link_or_a_flood_of_entries_in_the_canary_named_folder()
    {
        using var h = new MountHarness(maxScanEntries: 5);
        var folder = Path.Combine(h.Workspace(h.Agent), _canary);
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 20; i++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"f{i}"), "x", MountHarness.Ct);

        await RefusedAsync(h, MountRefusalReason.ContentRefused);
    }

    [Fact]
    public async Task A_canary_named_file_at_the_workspace_root_with_a_hard_link_is_refused()
    {
        using var h = new MountHarness();
        await File.WriteAllTextAsync(Path.Combine(h.Workspace(h.Agent), _canary), "x", MountHarness.Ct);
        h.LinkProbe.Linked.Add(_canary);

        await RefusedAsync(h, MountRefusalReason.LinkNotAllowed);
    }

    [Fact]
    public async Task A_stale_canary_folder_in_the_soul_share_does_not_block_an_attach_and_is_replaced()
    {
        using var h = new MountHarness();
        var stale = Path.Combine(h.Soul(h.Agent), _canary);
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "old.txt"), "stale", MountHarness.Ct);

        var mount = await h.Manager.AttachAsync(h.SoulSpec(), MountHarness.Ct);

        Assert.Equal(2, mount.CanaryCount);
        Assert.False(File.Exists(Path.Combine(stale, "old.txt")));
    }

    [Fact]
    public async Task The_canary_exemption_is_root_level_only_even_for_soul()
    {
        using var h = new MountHarness();
        var nested = Path.Combine(h.Soul(h.Agent), "sub", _canary);
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "id_rsa"), "x", MountHarness.Ct);

        await RefusedAsync(h, MountRefusalReason.ContentRefused, h.SoulSpec());
    }
}
