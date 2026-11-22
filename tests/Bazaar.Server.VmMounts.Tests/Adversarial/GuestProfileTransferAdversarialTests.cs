using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Paths;
using Bazaar.Server.VmMounts.Profiles;

namespace Bazaar.Server.VmMounts.Tests.Adversarial;

/// <summary>A hostile guest answers the profile pull. Every lie must reject the whole pull, leave no staging directory, and write nothing else.</summary>
public sealed class GuestProfileTransferAdversarialTests : IDisposable
{
    private const string _guestDir = "/var/lib/bazaar/profile";

    private readonly string _root = Directory.CreateTempSubdirectory("vmmounts-adv-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static GuestFileInfo Honest(string path, string content) =>
        new(path, Encoding.UTF8.GetByteCount(content), Sha(Encoding.UTF8.GetBytes(content)));

    private GuestProfileTransfer NewTransfer(HostileChannel channel, ProfileLimits? limits = null) =>
        new(channel, new DefaultMountContentPolicy(), new VmMountOptions { AgentsRoot = _root, ProfileLimits = limits ?? ProfileLimits.Default });

    /// <summary>A work folder that holds the staging path and a sentinel next to it, so any stray write shows up in a snapshot of the tree.</summary>
    private (string Staging, string Sentinel) Layout()
    {
        var work = Path.Combine(_root, "work");
        var sentinel = Path.Combine(work, "sentinel");
        Directory.CreateDirectory(sentinel);
        File.WriteAllText(Path.Combine(sentinel, "keep.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        File.WriteAllText(Path.Combine(_root, "elsewhere", "keep.txt"), "keep");
        return (Path.Combine(work, "staging"), sentinel);
    }

    private string[] Snapshot() =>
        [.. Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Select(p => p + (File.Exists(p) ? ":" + File.ReadAllText(p) : string.Empty)).Order(StringComparer.Ordinal)];

    private async Task<ProfileTransferException> RejectedAsync(
        HostileChannel channel, ProfileTransferFailure failure, ProfileLimits? limits = null, string? staging = null)
    {
        var (defaultStaging, sentinel) = Layout();
        staging ??= defaultStaging;
        var before = Snapshot();

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => NewTransfer(channel, limits).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, MountHarness.Ct));

        Assert.Equal(failure, ex.Failure);
        Assert.False(Directory.Exists(staging), "staging must not exist after a rejected pull");
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(sentinel, "keep.txt"), MountHarness.Ct));
        Assert.Equal(before, Snapshot());
        return ex;
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("../../x")]
    [InlineData("a/../../x")]
    [InlineData("a/../x")]
    [InlineData("./x")]
    [InlineData("a/./x")]
    [InlineData("/etc/passwd")]
    [InlineData("/x")]
    [InlineData("a\\b")]
    [InlineData("..\\x")]
    [InlineData("\\x")]
    [InlineData("C:x")]
    [InlineData("C:\\x")]
    [InlineData("C:/x")]
    [InlineData("a:b")]
    [InlineData("file:stream")]
    [InlineData("file::$DATA")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("a/NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9.log")]
    [InlineData("PROGRA~1")]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("a./b")]
    [InlineData("a /b")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("//server/share/x")]
    public async Task An_unsafe_listed_name_rejects_the_whole_pull(string name)
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("fine.txt", 1, Sha([1])), new GuestFileInfo(name, 1, Sha([1]))] };
        channel.Content["fine.txt"] = [1];
        channel.Content[name] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);

        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_name_33_segments_deep_is_rejected_and_32_is_the_limit()
    {
        var deep33 = string.Join('/', Enumerable.Repeat("d", 32)) + "/f";
        var channel = new HostileChannel { Listing = [new GuestFileInfo(deep33, 1, Sha([1]))] };
        channel.Content[deep33] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_name_longer_than_1024_characters_is_rejected()
    {
        var name = string.Join('/', Enumerable.Repeat(new string('a', 100), 11));
        Assert.True(name.Length > 1024);
        var channel = new HostileChannel { Listing = [new GuestFileInfo(name, 1, Sha([1]))] };
        channel.Content[name] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData("Default/Cookies", "default/cookies")]
    [InlineData("dir/file", "DIR/FILE")]
    public async Task Two_names_that_differ_only_by_case_are_rejected(string first, string second)
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo(first, 1, Sha([1])), new GuestFileInfo(second, 1, Sha([1]))] };
        channel.Content[first] = [1];
        channel.Content[second] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
        Assert.Equal(0, channel.Gets);
    }

    [Theory]
    [InlineData("a", "a/b")]
    [InlineData("a/b", "a")]
    [InlineData("A", "a/b")]
    [InlineData("a/b", "a/b/c/d")]
    [InlineData("x/y/z", "X/Y")]
    public async Task A_file_and_a_folder_of_the_same_name_are_rejected_as_invalid_not_as_an_io_error(string first, string second)
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo(first, 1, Sha([1])), new GuestFileInfo(second, 1, Sha([1]))] };
        channel.Content[first] = [1];
        channel.Content[second] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_shared_name_prefix_that_is_not_a_whole_segment_is_fine()
    {
        var (staging, _) = Layout();
        var channel = new HostileChannel { Listing = [Honest("ab", "1"), Honest("a/b", "2"), Honest("a.b", "3")] };
        channel.Content["ab"] = "1"u8.ToArray();
        channel.Content["a/b"] = "2"u8.ToArray();
        channel.Content["a.b"] = "3"u8.ToArray();

        var manifest = await NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, MountHarness.Ct);

        Assert.Equal(3, manifest.Count);
        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(staging, "a", "b"), MountHarness.Ct));
    }

    [Fact]
    public async Task More_files_than_the_limit_are_rejected()
    {
        var files = Enumerable.Range(0, 4).Select(i => $"f{i}").ToArray();
        var channel = new HostileChannel { Listing = [.. files.Select(f => new GuestFileInfo(f, 1, Sha([1])))] };
        foreach (var f in files)
            channel.Content[f] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid, new ProfileLimits(3, 100, 1000));
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_single_file_over_the_per_file_limit_is_rejected_before_any_download()
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("big", 101, Sha([1]))] };
        channel.Content["big"] = new byte[101];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid, new ProfileLimits(10, 100, 1000));
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_total_over_the_limit_is_rejected_before_any_download()
    {
        var channel = new HostileChannel { Listing = [.. Enumerable.Range(0, 5).Select(i => new GuestFileInfo($"f{i}", 100, Sha([1])))] };

        await RejectedAsync(channel, ProfileTransferFailure.Invalid, new ProfileLimits(10, 100, 450));
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_total_that_would_overflow_a_long_is_rejected()
    {
        var channel = new HostileChannel
        {
            Listing = [new GuestFileInfo("a", long.MaxValue, Sha([1])), new GuestFileInfo("b", long.MaxValue, Sha([1]))],
        };

        await RejectedAsync(channel, ProfileTransferFailure.Invalid, new ProfileLimits(10, long.MaxValue, long.MaxValue));
        Assert.Equal(0, channel.Gets);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public async Task A_negative_length_is_rejected(long length)
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("f", length, Sha([1]))] };
        channel.Content["f"] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
    }

    [Fact]
    public async Task Negative_lengths_cannot_cancel_out_a_total_over_the_limit()
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("a", 900, Sha([1])), new GuestFileInfo("b", -500, Sha([1])), new GuestFileInfo("c", 900, Sha([1]))] };

        await RejectedAsync(channel, ProfileTransferFailure.Invalid, new ProfileLimits(10, 1000, 1000));
        Assert.Equal(0, channel.Gets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde ")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\0")]
    public async Task A_bad_sha_is_rejected(string sha)
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("f", 1, sha)] };
        channel.Content["f"] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_null_sha_is_rejected()
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("f", 1, null!)] };
        channel.Content["f"] = [1];

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
    }

    [Fact]
    public async Task Content_longer_than_listed_is_rejected()
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("f", 3, Sha("abc"u8.ToArray()))] };
        channel.Content["f"] = "abcdef"u8.ToArray();

        await RejectedAsync(channel, ProfileTransferFailure.HashMismatch);
    }

    [Fact]
    public async Task Content_shorter_than_listed_is_rejected()
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("f", 6, Sha("abcdef"u8.ToArray()))] };
        channel.Content["f"] = "abc"u8.ToArray();

        await RejectedAsync(channel, ProfileTransferFailure.HashMismatch);
    }

    [Fact]
    public async Task Empty_content_for_a_listed_non_empty_file_is_rejected()
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("f", 3, Sha("abc"u8.ToArray()))] };
        channel.Content["f"] = [];

        await RejectedAsync(channel, ProfileTransferFailure.HashMismatch);
    }

    [Fact]
    public async Task The_right_length_with_the_wrong_hash_is_rejected()
    {
        var channel = new HostileChannel { Listing = [new GuestFileInfo("f", 3, Sha("abc"u8.ToArray()))] };
        channel.Content["f"] = "abd"u8.ToArray();

        await RejectedAsync(channel, ProfileTransferFailure.HashMismatch);
    }

    [Fact]
    public async Task One_bad_file_among_good_ones_rejects_everything_already_staged()
    {
        var channel = new HostileChannel
        {
            Listing = [Honest("a", "1"), Honest("b", "2"), new GuestFileInfo("c", 1, Sha("3"u8.ToArray()))],
        };
        channel.Content["a"] = "1"u8.ToArray();
        channel.Content["b"] = "2"u8.ToArray();
        channel.Content["c"] = "X"u8.ToArray();

        await RejectedAsync(channel, ProfileTransferFailure.HashMismatch);
        Assert.Equal(3, channel.Gets);
    }

    [Theory]
    [InlineData("id_rsa")]
    [InlineData("sub/id_ed25519")]
    [InlineData("x.pem")]
    [InlineData("a/b/store.P12")]
    [InlineData(".ssh/config")]
    [InlineData("Secrets/token")]
    [InlineData(".env")]
    [InlineData("keys.kdbx")]
    public async Task A_name_the_content_policy_refuses_rejects_the_pull_before_any_download(string name)
    {
        var channel = new HostileChannel { Listing = [Honest("fine", "1"), Honest(name, "k")] };
        channel.Content["fine"] = "1"u8.ToArray();
        channel.Content[name] = "k"u8.ToArray();

        await RejectedAsync(channel, ProfileTransferFailure.Invalid);
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task A_channel_that_throws_mid_file_after_writing_part_of_it_is_a_channel_failure_and_leaves_nothing()
    {
        var channel = new HostileChannel { Listing = [Honest("a", "1"), Honest("b", "22"), Honest("c", "3")] };
        channel.Content["a"] = "1"u8.ToArray();
        channel.Content["b"] = "22"u8.ToArray();
        channel.Content["c"] = "3"u8.ToArray();
        channel.ThrowAfterFirstByteOn = "b";

        await RejectedAsync(channel, ProfileTransferFailure.Channel);
    }

    [Fact]
    public async Task A_channel_that_throws_while_listing_is_a_channel_failure()
    {
        var channel = new HostileChannel { ThrowOnList = true };

        await RejectedAsync(channel, ProfileTransferFailure.Channel);
    }

    [Fact]
    public async Task A_channel_that_writes_more_than_listed_then_throws_still_ends_as_a_rejection_with_nothing_left()
    {
        var channel = new HostileChannel { Listing = [Honest("a", "1")] };
        channel.Content["a"] = new byte[10_000];

        await RejectedAsync(channel, ProfileTransferFailure.HashMismatch);
    }

    [Fact]
    public async Task A_listing_that_is_empty_pulls_an_empty_profile_without_writing_outside_staging()
    {
        var (staging, sentinel) = Layout();
        var before = Snapshot();
        var channel = new HostileChannel { Listing = [] };

        var manifest = await NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, MountHarness.Ct);

        Assert.Equal(0, manifest.Count);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(sentinel, "keep.txt"), MountHarness.Ct));
        Assert.Equal(before, Snapshot().Where(p => !p.StartsWith(staging, StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task An_honest_pull_writes_only_inside_staging()
    {
        var (staging, sentinel) = Layout();
        var before = Snapshot();
        var channel = new HostileChannel { Listing = [Honest("Default/Cookies", "c"), Honest("Local State", "s")] };
        channel.Content["Default/Cookies"] = "c"u8.ToArray();
        channel.Content["Local State"] = "s"u8.ToArray();

        var manifest = await NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, MountHarness.Ct);

        Assert.Equal(2, manifest.Count);
        Assert.Equal("c", await File.ReadAllTextAsync(Path.Combine(staging, "Default", "Cookies"), MountHarness.Ct));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(sentinel, "keep.txt"), MountHarness.Ct));
        Assert.Equal(before, Snapshot().Where(p => !p.StartsWith(staging, StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task A_staging_directory_that_is_a_symlink_to_an_empty_directory_is_refused_and_the_target_stays_empty()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        var (staging, _) = Layout();
        var target = Path.Combine(_root, "elsewhere-empty");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(staging, target);
        var channel = new HostileChannel { Listing = [Honest("f", "x")] };
        channel.Content["f"] = "x"u8.ToArray();

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Invalid, ex.Failure);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Equal(0, channel.Lists);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task A_staging_directory_that_is_a_broken_symlink_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        var (staging, _) = Layout();
        var target = Path.Combine(_root, "not-yet");
        Directory.CreateSymbolicLink(staging, target);
        var channel = new HostileChannel { Listing = [Honest("f", "x")] };
        channel.Content["f"] = "x"u8.ToArray();

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Invalid, ex.Failure);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task A_staging_path_whose_parent_is_a_file_is_a_transfer_error_not_a_raw_io_error()
    {
        var (_, _) = Layout();
        var blocker = Path.Combine(_root, "blocker");
        await File.WriteAllTextAsync(blocker, "x", MountHarness.Ct);
        var channel = new HostileChannel { Listing = [Honest("f", "x")] };
        channel.Content["f"] = "x"u8.ToArray();

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, Path.Combine(blocker, "staging"), MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Invalid, ex.Failure);
        Assert.Equal("x", await File.ReadAllTextAsync(blocker, MountHarness.Ct));
    }

    [Fact]
    public async Task A_non_empty_staging_directory_is_refused_and_left_alone()
    {
        var (staging, _) = Layout();
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "old"), "old", MountHarness.Ct);
        var channel = new HostileChannel { Listing = [Honest("f", "x")] };
        channel.Content["f"] = "x"u8.ToArray();

        var ex = await Assert.ThrowsAsync<ProfileTransferException>(
            () => NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, MountHarness.Ct));

        Assert.Equal(ProfileTransferFailure.Invalid, ex.Failure);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(staging, "old"), MountHarness.Ct));
    }

    [Fact]
    public async Task A_cancelled_pull_leaves_no_staging_directory()
    {
        var (staging, _) = Layout();
        using var cts = new CancellationTokenSource();
        var channel = new HostileChannel { Listing = [Honest("a", "1"), Honest("b", "2")], OnGet = () => cts.Cancel() };
        channel.Content["a"] = "1"u8.ToArray();
        channel.Content["b"] = "2"u8.ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewTransfer(channel).PullAsync(Guid.NewGuid(), GuestOs.Linux, _guestDir, staging, cts.Token));

        Assert.False(Directory.Exists(staging));
    }

    /// <summary>A guest that says whatever the test tells it to, and keeps count of what the host asked.</summary>
    private sealed class HostileChannel : IMountGuestChannel
    {
        private int _gets;
        private int _lists;

        public IReadOnlyList<GuestFileInfo> Listing { get; init; } = [];

        public Dictionary<string, byte[]> Content { get; } = [];

        public bool ThrowOnList { get; init; }

        public string? ThrowAfterFirstByteOn { get; set; }

        public Action? OnGet { get; init; }

        public int Gets => _gets;

        public int Lists => _lists;

        public Task PutFileAsync(Guid vmId, string guestPath, Stream content, string sha256, CancellationToken ct) => Task.CompletedTask;

        public async Task GetFileAsync(Guid vmId, string guestPath, Stream sink, CancellationToken ct)
        {
            Interlocked.Increment(ref _gets);
            OnGet?.Invoke();
            ct.ThrowIfCancellationRequested();
            var relative = guestPath[(_guestDir.Length + 1)..];
            var bytes = Content[relative];
            if (ThrowAfterFirstByteOn == relative)
            {
                await sink.WriteAsync(bytes.AsMemory(0, 1), ct);
                throw new IOException("guest went away");
            }

            await sink.WriteAsync(bytes, ct);
        }

        public Task<IReadOnlyList<GuestFileInfo>> ListFilesAsync(Guid vmId, string guestDirectory, CancellationToken ct)
        {
            Interlocked.Increment(ref _lists);
            return ThrowOnList ? throw new IOException("guest went away") : Task.FromResult(Listing);
        }

        public Task DeleteDirectoryAsync(Guid vmId, string guestDirectory, CancellationToken ct) => Task.CompletedTask;
    }
}
