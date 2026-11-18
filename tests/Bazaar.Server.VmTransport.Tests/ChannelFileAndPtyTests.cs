using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Vms;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>File transfer and terminals through the real guest agent over the loopback transport.</summary>
public class ChannelFileAndPtyTests
{
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task A_file_put_then_got_comes_back_byte_for_byte()
    {
        await using var rig = await AgentRig.StartAsync();
        var content = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(content);
        var path = rig.Root.Combine("data.bin");

        await rig.Channel.PutFileAsync(path, new MemoryStream(content), Sha256(content), CancellationToken.None).WithinAsync();
        var sink = new MemoryStream();
        await rig.Channel.GetFileAsync(path, sink, CancellationToken.None).WithinAsync();

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(content, sink.ToArray());
    }

    [Fact]
    public async Task An_empty_file_round_trips()
    {
        await using var rig = await AgentRig.StartAsync();
        var path = rig.Root.Combine("empty");

        await rig.Channel.PutFileAsync(path, new MemoryStream(), Sha256([]), CancellationToken.None).WithinAsync();
        var sink = new MemoryStream();
        await rig.Channel.GetFileAsync(path, sink, CancellationToken.None).WithinAsync();

        Assert.Empty(sink.ToArray());
    }

    [Fact]
    public async Task A_put_whose_hash_is_wrong_is_rejected_by_the_guest_and_leaves_no_file()
    {
        await using var rig = await AgentRig.StartAsync();
        var path = rig.Root.Combine("bad.bin");

        var error = await Assert.ThrowsAsync<GuestFileException>(
            () => rig.Channel.PutFileAsync(path, new MemoryStream([1, 2, 3]), Sha256([9]), CancellationToken.None).WithinAsync());

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        await rig.Channel.PutFileAsync(path, new MemoryStream([1, 2, 3]), Sha256([1, 2, 3]), CancellationToken.None).WithinAsync();
    }

    [Fact]
    public async Task A_put_outside_the_allowed_roots_is_refused_and_the_channel_stays_usable()
    {
        await using var rig = await AgentRig.StartAsync();

        await Assert.ThrowsAsync<GuestFileException>(
            () => rig.Channel.PutFileAsync("/etc/bazaar-vmt-nope", new MemoryStream([1]), Sha256([1]), CancellationToken.None).WithinAsync());

        var path = rig.Root.Combine("ok.bin");
        await rig.Channel.PutFileAsync(path, new MemoryStream([1]), Sha256([1]), CancellationToken.None).WithinAsync();
        Assert.Equal(GuestChannelState.Open, rig.Channel.State);
    }

    [Fact]
    public async Task Getting_a_missing_file_fails_with_the_guests_reason()
    {
        await using var rig = await AgentRig.StartAsync();

        await Assert.ThrowsAsync<GuestFileException>(
            () => rig.Channel.GetFileAsync(rig.Root.Combine("missing"), new MemoryStream(), CancellationToken.None).WithinAsync());
        await Assert.ThrowsAsync<GuestFileException>(
            () => rig.Channel.GetFileAsync("/etc/hostname", new MemoryStream(), CancellationToken.None).WithinAsync());
        Assert.Equal(GuestChannelState.Open, rig.Channel.State);
    }

    [Fact]
    public async Task A_got_file_whose_bytes_do_not_match_the_announced_hash_is_rejected()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var sink = new MemoryStream();
            var get = channel.GetFileAsync("/g/f", sink, CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();

            await rig.SendAsync(new PutFile("/g/f", 3, Sha256([9, 9, 9])));
            await rig.SendRawAsync([1, 2, 3]);

            var error = await Assert.ThrowsAsync<GuestFileException>(() => get.WithinAsync());
            Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_drained_and_refused_without_desyncing_the_stream()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxFileBytes = 4 });
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFile("/g/f", 10, Sha256(new byte[10])));
            await rig.SendRawAsync(new byte[10]);

            await Assert.ThrowsAsync<GuestFileException>(() => get.WithinAsync());
            var next = channel.GetFileAsync("/g/f2", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new GetFileError("/g/f2", "gone"));
            await Assert.ThrowsAsync<GuestFileException>(() => next.WithinAsync());
        }
    }

    [Fact]
    public async Task Content_that_comes_before_any_get_closes_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(new PutFile("/g/f", 0, Sha256([])));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_put_needs_a_seekable_stream_and_a_well_formed_hash()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => channel.PutFileAsync("/p", new NonSeekable(), Sha256([]), CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(
                () => channel.PutFileAsync("/p", new MemoryStream(), "not-a-hash", CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_terminal_runs_a_shell_echoes_input_and_reports_its_exit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The PTY adapter is Linux-guest only (forkpty); Windows guests use ConPTY, tested separately.");
        await using var rig = await AgentRig.StartAsync();
        await using var pty = await rig.Channel.OpenPtyAsync(80, 24, CancellationToken.None);

        await pty.WriteAsync(Encoding.UTF8.GetBytes("echo vmt-$((20+22))\n"), CancellationToken.None);
        var seen = new StringBuilder();
        while (!seen.ToString().Contains("vmt-42", StringComparison.Ordinal))
        {
            using var bound = new CancellationTokenSource(Bound.Wait);
            seen.Append(Encoding.UTF8.GetString(await pty.Output.ReadAsync(bound.Token)));
        }

        await pty.ResizeAsync(100, 30, CancellationToken.None);
        await pty.WriteAsync(Encoding.UTF8.GetBytes("exit 5\n"), CancellationToken.None);

        Assert.Equal(5, await pty.Exited.WithinAsync());
    }

    [Fact]
    public async Task Closing_a_terminal_sends_ptyclose_once_and_ends_its_output()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var pty = await channel.OpenPtyAsync(80, 24, CancellationToken.None);
            var open = await rig.ReceiveAsync<PtyOpen>();
            Assert.Equal((80, 24), (open.Cols, open.Rows));

            await pty.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
            var data = await rig.ReceiveAsync<PtyData>();
            Assert.Equal([1, 2], data.Bytes);
            Assert.Equal(open.Id, data.Id);

            await rig.SendAsync(new PtyData(open.Id, 0, [7]));
            Assert.Equal([7], await pty.Output.ReadAsync(new CancellationTokenSource(Bound.Wait).Token));

            await pty.CloseAsync();
            await pty.DisposeAsync();
            var close = await rig.ReceiveAsync<PtyClose>();

            Assert.Equal(open.Id, close.Id);
            Assert.Equal(-1, await pty.Exited.WithinAsync());
            Assert.False(await pty.Output.WaitToReadAsync(new CancellationTokenSource(Bound.Wait).Token));
        }
    }

    private sealed class NonSeekable : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
