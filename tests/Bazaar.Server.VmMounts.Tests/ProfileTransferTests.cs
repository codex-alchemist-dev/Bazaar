using System.Text;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests;

public sealed class ProfileTransferTests
{
    [Fact]
    public async Task Push_replaces_the_guest_copy_and_returns_a_stable_manifest()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("b/two", "2");
        h.WriteHostProfile("a", "1");
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/stale", [1]);
        var dir = h.ProfileDirectory(h.Agent);

        var first = await h.Transfer.PushAsync(h.Vm, GuestOs.Linux, dir, MountHarness.LinuxProfile, MountHarness.Ct);
        var second = await h.Transfer.PushAsync(h.Vm, GuestOs.Linux, dir, MountHarness.LinuxProfile, MountHarness.Ct);

        Assert.Equal(["a", "b/two"], first.Files.Select(f => f.RelativePath));
        Assert.Equal(first.Digest, second.Digest);
        Assert.DoesNotContain($"{MountHarness.LinuxProfile}/stale", h.Channel.Files.Keys);
    }

    [Fact]
    public async Task Push_refuses_a_host_profile_that_holds_key_material()
    {
        using var h = new MountHarness();
        h.WriteHostProfile("id_rsa", "k");

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => h.Transfer.PushAsync(h.Vm, GuestOs.Linux, h.ProfileDirectory(h.Agent), MountHarness.LinuxProfile, MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Invalid, ex.Failure);
        Assert.Equal(0, h.Channel.PutCalls);
    }

    [Fact]
    public async Task Pull_into_a_non_empty_staging_directory_is_refused_without_deleting_it()
    {
        using var h = new MountHarness();
        var staging = Path.Combine(h.Root, "staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "leftover"), "x", MountHarness.Ct);

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => h.Transfer.PullAsync(h.Vm, GuestOs.Linux, MountHarness.LinuxProfile, staging, MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Invalid, ex.Failure);
        Assert.True(File.Exists(Path.Combine(staging, "leftover")));
    }

    [Fact]
    public async Task Pull_reports_a_mismatch_as_a_hash_failure_and_removes_the_staging_directory()
    {
        using var h = new MountHarness();
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/f", Encoding.UTF8.GetBytes("abc"));
        h.Channel.GetTransform = (_, _) => Encoding.UTF8.GetBytes("abd");
        var staging = Path.Combine(h.Root, "stg");

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => h.Transfer.PullAsync(h.Vm, GuestOs.Linux, MountHarness.LinuxProfile, staging, MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.HashMismatch, ex.Failure);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task Pull_rejects_a_listing_without_a_valid_sha256()
    {
        using var h = new MountHarness();
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/f", [1]);
        h.Channel.ListTransform = l => [.. l.Select(f => f with { Sha256 = "zz" })];

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => h.Transfer.PullAsync(h.Vm, GuestOs.Linux, MountHarness.LinuxProfile, Path.Combine(h.Root, "stg"), MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Invalid, ex.Failure);
    }

    [Fact]
    public async Task Pull_of_a_clean_profile_lands_every_file_in_staging()
    {
        using var h = new MountHarness();
        h.Channel.GuestWrite($"{MountHarness.LinuxProfile}/Default/Cookies", Encoding.UTF8.GetBytes("c"));
        var staging = Path.Combine(h.Root, "stg");

        var manifest = await h.Transfer.PullAsync(h.Vm, GuestOs.Linux, MountHarness.LinuxProfile, staging, MountHarness.Ct);

        Assert.Equal(1, manifest.Count);
        Assert.Equal("c", await File.ReadAllTextAsync(Path.Combine(staging, "Default", "Cookies"), MountHarness.Ct));
    }
}
