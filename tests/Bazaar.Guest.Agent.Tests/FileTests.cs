using System.Security.Cryptography;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

public sealed class FileTests
{
    [Fact]
    public async Task Put_file_writes_the_content_when_the_hash_matches()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var content = "hello from the host"u8.ToArray();
        var target = Path.Combine(root.Root, "a.txt");

        await guest.SendAsync(new PutFile(target, content.Length, Sha256(content)));
        await guest.SendRawAsync(content);
        var result = await guest.ReceiveAsync<PutFileResult>();
        await guest.SyncAsync();

        Assert.Equal(target, result.Path);
        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Put_file_with_a_wrong_hash_removes_the_partial_file_and_reports_it()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var content = "tampered bytes"u8.ToArray();
        var target = Path.Combine(root.Root, "b.txt");

        await guest.SendAsync(new PutFile(target, content.Length, Sha256("something else"u8.ToArray())));
        await guest.SendRawAsync(content);
        var result = await guest.ReceiveAsync<PutFileResult>();
        await guest.SyncAsync();

        Assert.False(result.Ok);
        Assert.Contains("SHA-256", result.Error);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(root.Root));
    }

    [Fact]
    public async Task Get_file_round_trips_with_its_size_and_hash()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var content = new byte[150_000];
        Random.Shared.NextBytes(content);
        var target = Path.Combine(root.Root, "big.bin");
        await File.WriteAllBytesAsync(target, content, TestContext.Current.CancellationToken);

        await guest.SendAsync(new GetFile(target));
        var header = await guest.ReceiveAsync<PutFile>();
        var received = await guest.ReceiveRawAsync((int)header.Size);

        Assert.Equal(content.Length, header.Size);
        Assert.Equal(Sha256(content), header.Sha256);
        Assert.Equal(content, received);
        await guest.SyncAsync();
    }

    [Fact]
    public async Task Put_file_with_dotdot_escape_is_refused_and_its_content_is_drained()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var content = "escape"u8.ToArray();
        var escaped = Path.Combine(root.Root, "..", "bazaar-escape-" + Guid.NewGuid().ToString("N") + ".txt");

        await guest.SendAsync(new PutFile(escaped, content.Length, Sha256(content)));
        await guest.SendRawAsync(content);

        // The result arrives only if the refused content was read, so the framing stayed in step.
        await ExpectRefusedAsync(guest, escaped);
        await guest.SyncAsync();
        Assert.False(File.Exists(Path.GetFullPath(escaped)));
    }

    [Fact]
    public async Task Put_file_with_an_absolute_path_outside_the_root_is_refused()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var content = "outside"u8.ToArray();
        var outside = Path.Combine(Path.GetTempPath(), "bazaar-escape-" + Guid.NewGuid().ToString("N") + ".txt");

        await guest.SendAsync(new PutFile(outside, content.Length, Sha256(content)));
        await guest.SendRawAsync(content);
        await ExpectRefusedAsync(guest, outside);
        await guest.SyncAsync();

        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Put_file_through_a_symlink_that_leaves_the_root_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        File.CreateSymbolicLink(Path.Combine(root.Root, "link"), outside.Root);
        var content = "planted"u8.ToArray();
        var throughLink = Path.Combine(root.Root, "link", "planted.txt");

        await guest.SendAsync(new PutFile(throughLink, content.Length, Sha256(content)));
        await guest.SendRawAsync(content);
        await ExpectRefusedAsync(guest, throughLink);
        await guest.SyncAsync();

        Assert.False(File.Exists(Path.Combine(outside.Root, "planted.txt")));
    }

    [Fact]
    public async Task Put_file_with_a_malformed_hash_is_refused_and_reported()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var content = "bad hash"u8.ToArray();
        var target = Path.Combine(root.Root, "c.txt");

        await guest.SendAsync(new PutFile(target, content.Length, "NOT-A-HASH"));
        await guest.SendRawAsync(content);
        var result = await guest.ReceiveAsync<PutFileResult>();
        await guest.SyncAsync();

        Assert.False(result.Ok);
        Assert.Equal("the hash is malformed", result.Error);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Get_file_outside_the_root_sends_get_file_error_and_no_file()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var outside = Path.Combine(Path.GetTempPath(), "bazaar-escape-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outside, "secret", TestContext.Current.CancellationToken);
        try
        {
            await guest.SendAsync(new GetFile(outside));

            var error = await guest.ReceiveAsync<GetFileError>();
            Assert.Equal(outside, error.Path);
            Assert.Contains("outside the allowed roots", error.Error);
            await guest.SyncAsync();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Get_file_for_a_missing_file_sends_get_file_error()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var missing = Path.Combine(root.Root, "missing.bin");

        await guest.SendAsync(new GetFile(missing));

        var error = await guest.ReceiveAsync<GetFileError>();
        Assert.Equal(missing, error.Path);
        Assert.False(string.IsNullOrEmpty(error.Error));
        await guest.SyncAsync();
    }

    private static async Task ExpectRefusedAsync(GuestHarness guest, string path)
    {
        var result = await guest.ReceiveAsync<PutFileResult>();
        Assert.Equal(path, result.Path);
        Assert.False(result.Ok);
        Assert.Equal("the path is outside the allowed roots", result.Error);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
