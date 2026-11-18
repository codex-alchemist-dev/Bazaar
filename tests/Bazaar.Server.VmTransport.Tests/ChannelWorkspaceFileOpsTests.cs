using Bazaar.Contracts.Vms;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>DeleteFile, ListFiles and ResolveRealPath through the real guest agent over the loopback transport, plus hand-driven edge cases.</summary>
public class ChannelWorkspaceFileOpsTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task Delete_then_list_shows_the_file_gone()
    {
        await using var rig = await AgentRig.StartAsync();
        Directory.CreateDirectory(rig.Root.Combine("src"));
        await File.WriteAllTextAsync(rig.Root.Combine("src/a.txt"), "x", _ct);
        await File.WriteAllTextAsync(rig.Root.Combine("src/b.txt"), "yy", _ct);

        await rig.Channel.DeleteFileAsync("src/a.txt", _ct).WithinAsync();
        var entries = await rig.Channel.ListFilesAsync("src", _ct).WithinAsync();

        Assert.Equal([new GuestDirectoryEntry("b.txt", 2, false)], entries);
    }

    [Fact]
    public async Task Deleting_a_missing_file_or_an_escaping_path_fails_and_the_channel_stays_usable()
    {
        await using var rig = await AgentRig.StartAsync();

        await Assert.ThrowsAsync<GuestFileException>(() => rig.Channel.DeleteFileAsync("missing.txt", _ct).WithinAsync());
        await Assert.ThrowsAsync<GuestFileException>(() => rig.Channel.DeleteFileAsync("../x.txt", _ct).WithinAsync());
        await Assert.ThrowsAsync<GuestFileException>(() => rig.Channel.DeleteFileAsync(rig.Root.Combine("abs.txt"), _ct).WithinAsync());

        Assert.Equal(GuestChannelState.Open, rig.Channel.State);
        Assert.Empty(await rig.Channel.ListFilesAsync("", _ct).WithinAsync());
    }

    [Fact]
    public async Task Listing_the_root_and_a_subdirectory_is_non_recursive()
    {
        await using var rig = await AgentRig.StartAsync();
        Directory.CreateDirectory(rig.Root.Combine("d/inner"));
        await File.WriteAllTextAsync(rig.Root.Combine("d/inner/deep.txt"), "x", _ct);
        await File.WriteAllTextAsync(rig.Root.Combine("top.txt"), "abc", _ct);

        var root = await rig.Channel.ListFilesAsync("", _ct).WithinAsync();
        var d = await rig.Channel.ListFilesAsync("d", _ct).WithinAsync();

        Assert.Equal([new GuestDirectoryEntry("d", 0, true), new GuestDirectoryEntry("top.txt", 3, false)], root);
        Assert.Equal([new GuestDirectoryEntry("inner", 0, true)], d);
    }

    [Fact]
    public async Task Listing_outside_the_root_or_a_missing_directory_fails_with_the_guests_reason()
    {
        await using var rig = await AgentRig.StartAsync();

        await Assert.ThrowsAsync<GuestFileException>(() => rig.Channel.ListFilesAsync("..", _ct).WithinAsync());
        await Assert.ThrowsAsync<GuestFileException>(() => rig.Channel.ListFilesAsync("nope", _ct).WithinAsync());
        Assert.Equal(GuestChannelState.Open, rig.Channel.State);
    }

    [Fact]
    public async Task Resolve_real_path_follows_an_inside_symlink_and_fails_closed_on_an_escape()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var rig = await AgentRig.StartAsync();
        using var outside = new TempRoot();
        Directory.CreateDirectory(rig.Root.Combine("real"));
        await File.WriteAllTextAsync(rig.Root.Combine("real/a.txt"), "x", _ct);
        await File.WriteAllTextAsync(outside.Combine("secret.txt"), "x", _ct);
        File.CreateSymbolicLink(rig.Root.Combine("alias"), rig.Root.Combine("real"));
        File.CreateSymbolicLink(rig.Root.Combine("out"), outside.Root);

        Assert.Equal("real/a.txt", await rig.Channel.ResolveRealPathAsync("alias/a.txt", _ct).WithinAsync());
        Assert.Null(await rig.Channel.ResolveRealPathAsync("out/secret.txt", _ct).WithinAsync());
        Assert.Null(await rig.Channel.ResolveRealPathAsync("../x", _ct).WithinAsync());
        Assert.Null(await rig.Channel.ResolveRealPathAsync(rig.Root.Combine("real/a.txt"), _ct).WithinAsync());
        Assert.Equal(".", await rig.Channel.ResolveRealPathAsync(".", _ct).WithinAsync());
    }

    [Fact]
    public async Task Requests_run_one_at_a_time_alongside_file_transfers()
    {
        await using var rig = await AgentRig.StartAsync();
        for (var i = 0; i < 20; i++)
        {
            await File.WriteAllTextAsync(rig.Root.Combine($"f{i}"), "x", _ct);
        }

        var tasks = Enumerable.Range(0, 20)
            .Select(i => i % 2 == 0
                ? rig.Channel.DeleteFileAsync($"f{i}", _ct)
                : rig.Channel.ListFilesAsync("", _ct))
            .ToArray();
        await Task.WhenAll(tasks).WithinAsync();

        Assert.Equal(10, (await rig.Channel.ListFilesAsync("", _ct).WithinAsync()).Count);
    }

    [Fact]
    public async Task Argument_checks_run_before_anything_is_sent()
    {
        await using var rig = await AgentRig.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Channel.DeleteFileAsync("", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Channel.ResolveRealPathAsync("", _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Channel.ListFilesAsync(null!, _ct));
    }

    [Fact]
    public async Task A_cancelled_request_still_lets_the_next_one_through()
    {
        await using var rig = await AgentRig.StartAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Channel.ListFilesAsync("", cts.Token));

        Assert.Empty(await rig.Channel.ListFilesAsync("", _ct).WithinAsync());
    }

    [Fact]
    public async Task The_host_writes_each_request_under_its_path_and_matches_the_answer()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var delete = channel.DeleteFileAsync("a.txt", _ct);
            Assert.Equal("a.txt", (await rig.ReceiveAsync<DeleteFile>()).Path);
            await rig.SendAsync(new DeleteFileResult("a.txt", true, null));
            await delete.WithinAsync();

            var list = channel.ListFilesAsync("d", _ct);
            Assert.Equal("d", (await rig.ReceiveAsync<ListFiles>()).Dir);
            await rig.SendAsync(new ListFilesResult("d", [new GuestFileEntry("x", 4, false)], null));
            Assert.Equal([new GuestDirectoryEntry("x", 4, false)], await list.WithinAsync());

            var resolve = channel.ResolveRealPathAsync("l", _ct);
            Assert.Equal("l", (await rig.ReceiveAsync<ResolveRealPath>()).Path);
            await rig.SendAsync(new ResolveRealPathResult("l", null, null));
            Assert.Null(await resolve.WithinAsync());
        }
    }

    [Fact]
    public async Task An_answer_nobody_asked_for_or_for_another_path_faults_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(new ListFilesResult("d", [], null));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }

        var (rig2, channel2) = await ManualRig.OpenAsync();
        await using (rig2)
        await using (channel2)
        {
            var delete = channel2.DeleteFileAsync("a.txt", _ct);
            await rig2.ReceiveAsync<DeleteFile>();
            await rig2.SendAsync(new DeleteFileResult("other.txt", true, null));

            await Assert.ThrowsAsync<GuestChannelException>(() => delete.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel2.State);
        }
    }

    [Fact]
    public async Task A_guest_that_never_answers_a_request_faults_the_channel_after_the_answer_deadline()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { FileAnswerTimeout = TimeSpan.FromMilliseconds(200) });
        await using (rig)
        await using (channel)
        {
            var list = channel.ListFilesAsync("", _ct);
            await rig.ReceiveAsync<ListFiles>();

            await Assert.ThrowsAsync<GuestChannelException>(() => list.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_guest_that_predates_protocol_three_is_refused_before_anything_is_sent()
    {
        var rig = await ManualRig.ConnectAsync();
        await using (rig)
        {
            await rig.SendAsync(new Hello("0.0.9", AgentRig.VmId, AgentRig.Nonce));
            await using var channel = await GuestChannel.OpenAsync(rig.Connection, new GuestHandshake(AgentRig.VmId, AgentRig.Nonce));

            Assert.Equal(0, channel.ProtocolVersion);
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() => channel.ListFilesAsync("", _ct));
            Assert.Contains("version 3", ex.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<NotSupportedException>(() => channel.DeleteFileAsync("a", _ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => channel.ResolveRealPathAsync("a", _ct));
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task A_path_over_the_length_limit_is_refused_by_the_host()
    {
        await using var rig = await AgentRig.StartAsync();
        var tooLong = new string('a', GuestPathRules.MaxPathChars + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Channel.ListFilesAsync(tooLong, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Channel.DeleteFileAsync(tooLong, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Channel.ResolveRealPathAsync(tooLong, _ct));
        Assert.Equal(GuestChannelState.Open, rig.Channel.State);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../up")]
    [InlineData("a/../b")]
    [InlineData("C:\\x")]
    [InlineData("a:b")]
    public async Task A_real_path_that_is_not_a_safe_relative_path_is_treated_as_null(string reported)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var resolve = channel.ResolveRealPathAsync("l", _ct);
            await rig.ReceiveAsync<ResolveRealPath>();
            await rig.SendAsync(new ResolveRealPathResult("l", reported, null));

            Assert.Null(await resolve.WithinAsync());
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData(null)]
    public async Task A_listing_with_a_bad_entry_name_faults_the_channel(string? name)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var list = channel.ListFilesAsync("d", _ct);
            await rig.ReceiveAsync<ListFiles>();
            await rig.SendAsync(new ListFilesResult("d", [new GuestFileEntry(name!, 1, false)], null));

            await Assert.ThrowsAsync<GuestChannelException>(() => list.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_listing_with_no_entry_list_and_no_error_faults_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var list = channel.ListFilesAsync("d", _ct);
            await rig.ReceiveAsync<ListFiles>();
            await rig.SendAsync(new ListFilesResult("d", null!, null));

            await Assert.ThrowsAsync<GuestChannelException>(() => list.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_request_on_an_ended_channel_fails_with_the_channel_exception()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        {
            await channel.DisposeAsync();

            await Assert.ThrowsAsync<GuestChannelException>(() => channel.DeleteFileAsync("a", _ct));
            await Assert.ThrowsAsync<GuestChannelException>(() => channel.ListFilesAsync("", _ct));
            await Assert.ThrowsAsync<GuestChannelException>(() => channel.ResolveRealPathAsync("a", _ct));
        }
    }
}
