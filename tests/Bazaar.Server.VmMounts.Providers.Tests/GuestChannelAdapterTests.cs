using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Guest;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class GuestChannelAdapterTests : IDisposable
{
    private const string _hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const string _profile = "/var/lib/bazaar/profile";

    private readonly MountEnv _env;
    private readonly FakeStore _store = new();
    private readonly FakeGuestChannel _channel = new();
    private readonly Guid _vm = Guid.NewGuid();

    public GuestChannelAdapterTests() : this(b =>
    {
        b.Limits = new ProfileLimits(100, 10, 100);
        b.GuestIoTimeout = TimeSpan.FromMilliseconds(200);
    })
    {
    }

    private GuestChannelAdapterTests(Action<VmMountOptionsBuilder> configure)
    {
        _env = new MountEnv(configure);
        _store.Vms[_vm] = GuestOs.Linux;
    }

    public void Dispose() => _env.Dispose();

    private GuestChannelMountAdapter Adapter(FakeGuestChannel? channel = null, bool noChannel = false) =>
        new(new FakeChannelProvider(noChannel ? null : channel ?? _channel), _store, _env.Options, _env.Options);

    [Fact]
    public async Task A_put_under_the_profile_folder_reaches_the_channel_with_a_lower_case_hash()
    {
        using var content = new MemoryStream("hello"u8.ToArray());

        await Adapter().PutFileAsync(_vm, _profile + "/Default/Cookies", content, _hash, TestContext.Current.CancellationToken);

        var put = Assert.Single(_channel.Puts);
        Assert.Equal(_profile + "/Default/Cookies", put.Path);
        Assert.Equal(_hash.ToLowerInvariant(), put.Sha256);
        Assert.Equal("hello"u8.ToArray(), put.Bytes);
    }

    [Fact]
    public async Task A_get_under_the_profile_folder_fills_the_sink()
    {
        using var sink = new MemoryStream();

        await Adapter().GetFileAsync(_vm, _profile + "/Default/Cookies", sink, TestContext.Current.CancellationToken);

        Assert.Equal("data"u8.ToArray(), sink.ToArray());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/var/lib/bazaar/profile/../../../etc/passwd")]
    [InlineData("/var/lib/bazaar/profile/..")]
    [InlineData("/var/lib/bazaar/profile-evil/a")]
    [InlineData("/var/lib/bazaar")]
    [InlineData("/")]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("/var/lib/bazaar/profile/a\0b")]
    [InlineData("/var/lib/bazaar/profile/a\nb")]
    [InlineData("/var/lib/bazaar/profile//double")]
    [InlineData("/var/lib/bazaar/profile/trailing.")]
    [InlineData("/var/lib/bazaar/profile/a:stream")]
    [InlineData("/var/lib/bazaar/profile\\a")]
    [InlineData("C:\\Bazaar\\Profile\\a")]
    [InlineData("\\\\server\\share\\a")]
    public async Task Paths_that_are_not_under_the_profile_or_a_mount_are_refused_and_never_sent(string path)
    {
        var adapter = Adapter();
        using var content = new MemoryStream([1]);

        var put = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => adapter.PutFileAsync(_vm, path, content, _hash, TestContext.Current.CancellationToken));
        var get = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => adapter.GetFileAsync(_vm, path, new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.PathNotAllowed, put.Reason);
        Assert.Equal(MountGuestFailure.PathNotAllowed, get.Reason);
        Assert.Empty(_channel.Puts);
        Assert.Empty(_channel.Gets);
    }

    [Fact]
    public async Task A_path_in_the_other_guest_os_format_is_refused()
    {
        var windows = Guid.NewGuid();
        _store.Vms[windows] = GuestOs.Windows;

        // The Windows VM must not accept a Linux-shaped path, and the Linux VM must not accept a Windows one.
        await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(windows, _profile + "/a", new MemoryStream(), TestContext.Current.CancellationToken));
        await Adapter().GetFileAsync(windows, @"C:\Bazaar\Profile\a", new MemoryStream(), TestContext.Current.CancellationToken);
        Assert.Equal([@"C:\Bazaar\Profile\a"], _channel.Gets);
    }

    [Fact]
    public async Task Windows_paths_compare_case_insensitively_and_reject_traversal()
    {
        var windows = Guid.NewGuid();
        _store.Vms[windows] = GuestOs.Windows;

        await Adapter().GetFileAsync(windows, @"c:\bazaar\profile\Default\x", new MemoryStream(), TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(windows, @"C:\Bazaar\Profile\..\Windows\x", new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.PathNotAllowed, ex.Reason);
    }

    [Fact]
    public async Task An_unknown_vm_is_refused()
    {
        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(Guid.NewGuid(), _profile + "/a", new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.PathNotAllowed, ex.Reason);
    }

    [Fact]
    public async Task Mount_paths_of_this_vm_are_allowed_for_reads_and_writes_only_when_read_write()
    {
        _store.Rows.Add(new ScopedMountRow(Guid.NewGuid(), _env.AgentId, _vm, MountKind.Soul, "x", "/mnt/bazaar/soul", MountMode.Ro, null));
        _store.Rows.Add(new ScopedMountRow(Guid.NewGuid(), _env.AgentId, _vm, MountKind.Workspace, "x", "/mnt/bazaar/workspace", MountMode.Rw, null));
        var adapter = Adapter();
        using var content = new MemoryStream([1]);

        await adapter.GetFileAsync(_vm, "/mnt/bazaar/soul/a", new MemoryStream(), TestContext.Current.CancellationToken);
        await adapter.PutFileAsync(_vm, "/mnt/bazaar/workspace/a", content, _hash, TestContext.Current.CancellationToken);
        content.Position = 0;
        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => adapter.PutFileAsync(_vm, "/mnt/bazaar/soul/a", content, _hash, TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.PathNotAllowed, ex.Reason);
        Assert.Single(_channel.Puts);
    }

    [Fact]
    public async Task Another_vms_mount_does_not_open_a_path()
    {
        _store.Rows.Add(new ScopedMountRow(Guid.NewGuid(), _env.AgentId, Guid.NewGuid(), MountKind.Workspace, "x", "/srv/other", MountMode.Rw, null));

        await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(_vm, "/srv/other/a", new MemoryStream(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_detached_mount_does_not_open_a_path()
    {
        _store.Rows.Add(new ScopedMountRow(
            Guid.NewGuid(), _env.AgentId, _vm, MountKind.Workspace, "x", "/srv/gone", MountMode.Rw, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(_vm, "/srv/gone/a", new MemoryStream(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_open_channel_fails_closed()
    {
        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter(noChannel: true).GetFileAsync(_vm, _profile + "/a", new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.ChannelUnavailable, ex.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz" + "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData(_hash + "00")]
    public async Task A_hash_that_is_not_64_hex_digits_is_refused(string hash)
    {
        using var content = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().PutFileAsync(_vm, _profile + "/a", content, hash, TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.InvalidArgument, ex.Reason);
        Assert.Empty(_channel.Puts);
    }

    [Fact]
    public async Task An_oversize_seekable_put_is_refused_before_the_channel_is_touched()
    {
        // The limit in these tests is 10 bytes.
        await using var content = new HugeStream(11);

        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().PutFileAsync(_vm, _profile + "/a", content, _hash, TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.SizeLimit, ex.Reason);
        Assert.Empty(_channel.Puts);
    }

    [Fact]
    public async Task A_forward_only_stream_is_buffered_within_the_cap_and_refused_beyond_it()
    {
        await Adapter().PutFileAsync(_vm, _profile + "/ok", new ForwardOnlyStream(new byte[10]), _hash, TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().PutFileAsync(_vm, _profile + "/big", new ForwardOnlyStream(new byte[11]), _hash, TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.SizeLimit, ex.Reason);
        Assert.Equal([_profile + "/ok"], _channel.Puts.Select(p => p.Path));
        Assert.Equal(10, _channel.Puts[0].Bytes.Length);
    }

    [Fact]
    public async Task A_guest_that_sends_more_than_the_cap_is_cut_off()
    {
        _channel.OnGet = async (_, sink, ct) =>
        {
            await sink.WriteAsync(new byte[6], ct);
            await sink.WriteAsync(new byte[6], ct);
        };
        using var sink = new MemoryStream();

        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(_vm, _profile + "/a", sink, TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.SizeLimit, ex.Reason);
        Assert.True(sink.Length <= 10);
    }

    [Fact]
    public async Task A_stuck_guest_times_out_with_the_server_side_limit()
    {
        _channel.OnGet = (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(_vm, _profile + "/a", new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.Timeout, ex.Reason);
    }

    [Fact]
    public async Task A_stuck_put_times_out_too()
    {
        _channel.OnPut = (_, _, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        using var content = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().PutFileAsync(_vm, _profile + "/a", content, _hash, TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.Timeout, ex.Reason);
    }

    [Fact]
    public async Task A_cancelled_caller_gets_cancellation_not_a_timeout()
    {
        _channel.OnGet = (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        using var cts = new CancellationTokenSource();
        var pending = Adapter().GetFileAsync(_vm, _profile + "/a", new MemoryStream(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Guest_refusals_and_channel_failures_are_mapped_and_cleaned()
    {
        _channel.OnGet = (_, _, _) => throw new GuestFileException("denied\u001b[31m\r\nINJECTED line");
        var refused = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(_vm, _profile + "/a", new MemoryStream(), TestContext.Current.CancellationToken));
        _channel.OnGet = (_, _, _) => throw new GuestChannelException("channel ended");
        var failed = await Assert.ThrowsAsync<MountGuestChannelException>(
            () => Adapter().GetFileAsync(_vm, _profile + "/a", new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(MountGuestFailure.GuestRefused, refused.Reason);
        Assert.DoesNotContain('\u001b', refused.Message);
        Assert.DoesNotContain('\n', refused.Message);
        Assert.IsType<GuestFileException>(refused.InnerException);
        Assert.Equal(MountGuestFailure.ChannelFailed, failed.Reason);
    }

    [Fact]
    public async Task Listing_and_directory_delete_are_not_supported()
    {
        var adapter = Adapter();

        var list = await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ListFilesAsync(_vm, _profile, TestContext.Current.CancellationToken));
        var delete = await Assert.ThrowsAsync<NotSupportedException>(() => adapter.DeleteDirectoryAsync(_vm, _profile, TestContext.Current.CancellationToken));

        Assert.Contains("guest protocol", list.Message, StringComparison.Ordinal);
        Assert.Contains("guest protocol", delete.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_provider_has_no_channel()
    {
        Assert.Null(await new UnavailableGuestChannelProvider().TryGetAsync(_vm, TestContext.Current.CancellationToken));
    }
}
