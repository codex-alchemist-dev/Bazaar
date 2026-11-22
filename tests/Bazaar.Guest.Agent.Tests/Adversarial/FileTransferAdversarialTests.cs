using System.Security.Cryptography;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests.Adversarial;

/// <summary>File frames through a real agent: whatever the path or the content does, nothing lands outside the roots and no temporary file stays.</summary>
public sealed class FileTransferAdversarialTests
{
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<PutFileResult> PutAsync(GuestHarness guest, string path, byte[] content, string? sha = null)
    {
        await guest.SendAsync(new PutFile(path, content.Length, sha ?? Sha256(content)));
        await guest.SendRawAsync(content);
        var result = await guest.ReceiveAsync<PutFileResult>();
        await guest.SyncAsync();
        return result;
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("./a.txt")]
    [InlineData("..\\..\\a.txt")]
    [InlineData("")]
    public async Task A_relative_or_empty_put_path_is_refused_and_creates_nothing(string path)
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        var result = await PutAsync(guest, path, "x"u8.ToArray());

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFileSystemEntries(root.Root));
    }

    [Fact]
    public async Task A_put_through_dot_dot_into_a_sibling_with_the_root_as_a_name_prefix_is_refused()
    {
        await using var root = new TempRoot();
        var evil = root.Root + "-evil";
        Directory.CreateDirectory(evil);
        try
        {
            await using var guest = new GuestHarness(root.Options());
            await guest.ReceiveAsync<Hello>();

            var viaName = await PutAsync(guest, Path.Combine(evil, "f"), "x"u8.ToArray());
            var viaDots = await PutAsync(guest, Path.Combine(root.Root, "..", Path.GetFileName(evil), "g"), "x"u8.ToArray());

            Assert.False(viaName.Ok);
            Assert.False(viaDots.Ok);
            Assert.Empty(Directory.GetFileSystemEntries(evil));
        }
        finally
        {
            Directory.Delete(evil, recursive: true);
        }
    }

    [Fact]
    public async Task A_put_to_the_root_itself_is_refused()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        var result = await PutAsync(guest, root.Root, "x"u8.ToArray());

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(root.Root));
    }

    [Fact]
    public async Task A_put_through_a_chain_or_loop_of_symlinks_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Creates symbolic links.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        File.CreateSymbolicLink(Path.Combine(root.Root, "l2"), outside.Root);
        File.CreateSymbolicLink(Path.Combine(root.Root, "l1"), Path.Combine(root.Root, "l2"));
        File.CreateSymbolicLink(Path.Combine(root.Root, "loopA"), Path.Combine(root.Root, "loopB"));
        File.CreateSymbolicLink(Path.Combine(root.Root, "loopB"), Path.Combine(root.Root, "loopA"));
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        var chain = await PutAsync(guest, Path.Combine(root.Root, "l1", "planted"), "x"u8.ToArray());
        var loop = await PutAsync(guest, Path.Combine(root.Root, "loopA", "planted"), "x"u8.ToArray());
        var leaf = await PutAsync(guest, Path.Combine(root.Root, "l1"), "x"u8.ToArray());

        Assert.False(chain.Ok);
        Assert.False(loop.Ok);
        Assert.False(leaf.Ok);
        Assert.Empty(Directory.GetFileSystemEntries(outside.Root));
    }

    [Fact]
    public async Task A_put_onto_a_file_symlink_that_points_outside_does_not_write_the_target()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Creates symbolic links.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        var victim = Path.Combine(outside.Root, "victim");
        await File.WriteAllTextAsync(victim, "original", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(root.Root, "link"), victim);
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        var result = await PutAsync(guest, Path.Combine(root.Root, "link"), "overwritten"u8.ToArray());

        Assert.False(result.Ok);
        Assert.Equal("original", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_get_of_a_symlink_to_a_file_outside_the_root_is_refused_without_a_header()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Creates symbolic links.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        var secret = Path.Combine(outside.Root, "secret");
        await File.WriteAllTextAsync(secret, "top secret", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(root.Root, "link"), secret);
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new GetFile(Path.Combine(root.Root, "link")));
        var error = await guest.ReceiveAsync<GetFileError>();
        await guest.SyncAsync();

        Assert.Contains("outside", error.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("passwd")]
    [InlineData("")]
    public async Task A_get_with_a_traversal_relative_or_empty_path_is_refused(string suffix)
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var path = suffix.StartsWith("..", StringComparison.Ordinal) ? Path.Combine(root.Root, suffix) : suffix;

        await guest.SendAsync(new GetFile(path));

        Assert.IsType<GetFileError>(await guest.ReceiveAsync());
        await guest.SyncAsync();
    }

    [Fact]
    public async Task A_failed_hash_leaves_no_partial_file_and_no_target()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var target = Path.Combine(root.Root, "t.bin");
        var content = new byte[200_000];
        Random.Shared.NextBytes(content);

        var result = await PutAsync(guest, target, content, Sha256([1]));

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFileSystemEntries(root.Root));
    }

    [Fact]
    public async Task A_put_whose_content_is_cut_short_leaves_no_partial_file()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var target = Path.Combine(root.Root, "t.bin");

        await guest.SendAsync(new PutFile(target, 100_000, Sha256(new byte[100_000])));
        await guest.SendRawAsync(new byte[1000]);
        guest.CloseInput();
        await guest.Run.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFileSystemEntries(root.Root));
    }

    [Fact]
    public async Task A_put_that_is_refused_for_its_hash_format_still_drains_its_content_and_creates_nothing()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        // Uppercase hex is not the canonical lower-case form the guest accepts.
        var content = "x"u8.ToArray();
        var result = await PutAsync(guest, Path.Combine(root.Root, "u"), content, Sha256(content).ToUpperInvariant());

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFileSystemEntries(root.Root));
    }

    [Fact]
    public async Task A_negative_size_closes_the_channel_and_creates_nothing()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new PutFile(Path.Combine(root.Root, "n"), -5, Sha256([])));
        var result = await guest.ReceiveAsync<PutFileResult>();

        Assert.False(result.Ok);
        Assert.NotEqual(0, await guest.Run.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFileSystemEntries(root.Root));
    }

    [Fact]
    public async Task A_posix_path_with_backslashes_is_one_file_name_under_the_root()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Backslash is a separator on Windows.");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var parent = Directory.GetParent(root.Root)!.FullName;
        var path = root.Root + "/..\\..\\escaped";

        var result = await PutAsync(guest, path, "x"u8.ToArray());

        Assert.False(File.Exists(Path.Combine(parent, "escaped")));
        if (result.Ok)
        {
            Assert.All(Directory.GetFileSystemEntries(root.Root), entry => Assert.Equal(root.Root, Path.GetDirectoryName(entry)));
        }
    }
}
