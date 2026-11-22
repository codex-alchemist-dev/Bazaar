using Bazaar.Contracts.Vms;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>Review fixes: stuck guests, terminal limits, answer deadlines, sink failures and malformed Hello fields.</summary>
public class ChannelHardeningTests
{
    private static readonly string[] _noEnv = [];

    [Fact]
    public async Task A_guest_that_stops_reading_faults_a_file_put_within_the_write_deadline()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { WriteTimeout = TimeSpan.FromMilliseconds(300) });
        await using (rig)
        await using (channel)
        {
            var content = new byte[4 * 1024 * 1024];

            // The rig never reads, so the pipe fills after a few slices and the next write stalls.
            await Assert.ThrowsAsync<GuestChannelException>(
                () => channel.PutFileAsync("/g/big", new MemoryStream(content), new string('0', 64), CancellationToken.None).WithinAsync());

            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Contains("broke off", channel.EndReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_guest_that_stops_reading_faults_the_channel_and_fails_an_exec_in_flight()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { WriteTimeout = TimeSpan.FromMilliseconds(300) });
        await using (rig)
        await using (channel)
        {
            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, CancellationToken.None);
            await rig.ReceiveAsync<Exec>();
            await using var pty = await channel.OpenPtyAsync(80, 24, CancellationToken.None);

            var flood = Task.Run(async () =>
            {
                var block = new byte[32 * 1024];
                while (true)
                {
                    await pty.WriteAsync(block, CancellationToken.None);
                }
            });

            await Assert.ThrowsAsync<GuestChannelException>(() => flood.WithinAsync());
            await Assert.ThrowsAsync<GuestChannelException>(() => run.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Contains("stopped reading", channel.EndReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_terminal_whose_reader_falls_behind_the_byte_limit_is_ended_and_the_guest_told()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxPendingPtyBytes = 100 });
        await using (rig)
        await using (channel)
        {
            var pty = await channel.OpenPtyAsync(80, 24, CancellationToken.None);
            var open = await rig.ReceiveAsync<PtyOpen>();

            await rig.SendAsync(new PtyData(open.Id, 0, new byte[60]));
            await rig.SendAsync(new PtyData(open.Id, 1, new byte[60]));

            Assert.Equal(-1, await pty.Exited.WithinAsync());
            Assert.Equal(open.Id, (await rig.ReceiveAsync<PtyClose>()).Id);
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task A_reader_that_keeps_up_never_hits_the_byte_limit()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxPendingPtyBytes = 100 });
        await using (rig)
        await using (channel)
        {
            var pty = await channel.OpenPtyAsync(80, 24, CancellationToken.None);
            var open = await rig.ReceiveAsync<PtyOpen>();

            for (var i = 0; i < 10; i++)
            {
                await rig.SendAsync(new PtyData(open.Id, i, new byte[60]));
                Assert.Equal(60, (await pty.Output.ReadAsync(new CancellationTokenSource(Bound.Wait).Token)).Length);
            }

            Assert.False(pty.Exited.IsCompleted);
        }
    }

    [Fact]
    public async Task A_pty_frame_without_bytes_closes_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendRawAsync(ManualRig.RawFrame("ptyData", """{"id":"x","seq":0,"bytes":null}"""));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task Terminals_over_the_per_channel_cap_are_refused()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxOpenPtys = 1 });
        await using (rig)
        await using (channel)
        {
            await using var first = await channel.OpenPtyAsync(80, 24, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.OpenPtyAsync(80, 24, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Concurrent_terminal_writes_reach_the_wire_numbered_in_order()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await using var pty = await channel.OpenPtyAsync(80, 24, CancellationToken.None);
            await rig.ReceiveAsync<PtyOpen>();

            var writes = Enumerable.Range(0, 40).Select(i => Task.Run(async () => await pty.WriteAsync(new[] { (byte)i }, CancellationToken.None))).ToArray();
            var seqs = new List<long>();
            for (var i = 0; i < 40; i++)
            {
                seqs.Add((await rig.ReceiveAsync<PtyData>()).Seq);
            }

            await Task.WhenAll(writes).WithinAsync();
            Assert.Equal(Enumerable.Range(0, 40).Select(i => (long)i), seqs);
        }
    }

    [Fact]
    public async Task A_guest_that_never_answers_a_file_request_faults_the_channel_after_the_answer_deadline()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { FileAnswerTimeout = TimeSpan.FromMilliseconds(200) });
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();

            await Assert.ThrowsAsync<GuestChannelException>(() => get.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_sink_that_throws_any_exception_fails_that_read_and_keeps_the_stream_in_step()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new ThrowingSink(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            var content = new byte[200_000];
            await rig.SendAsync(new PutFile("/g/f", content.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content))));
            await rig.SendRawAsync(content);

            var error = await Assert.ThrowsAsync<GuestFileException>(() => get.WithinAsync());
            Assert.Contains("sink", error.Message, StringComparison.OrdinalIgnoreCase);
            var next = channel.GetFileAsync("/g/g", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new GetFileError("/g/g", "gone"));
            await Assert.ThrowsAsync<GuestFileException>(() => next.WithinAsync());
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task A_local_timeout_is_reported_even_when_the_guests_late_exit_does_not_say_so()
    {
        var options = new GuestChannelOptions { TimeoutGrace = TimeSpan.FromMilliseconds(50), CancelGrace = TimeSpan.FromSeconds(10) };
        var (rig, channel) = await ManualRig.OpenAsync(options);
        await using (rig)
        await using (channel)
        {
            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(1)), _ => Task.CompletedTask, CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();
            await rig.ReceiveAsync<Cancel>();
            await rig.SendAsync(new Exit(exec.Id, 137, TimedOut: false));

            var result = await run.WithinAsync();

            Assert.True(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Equal(137, result.ExitCode);
        }
    }

    [Fact]
    public async Task A_hello_with_missing_fields_is_a_handshake_failure()
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendRawAsync(ManualRig.RawFrame("hello", """{"agentVersion":null,"vmId":null,"nonce":null}"""));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, new GuestHandshake(AgentRig.VmId, AgentRig.Nonce)));
    }

    [Theory]
    [InlineData("", "n")]
    [InlineData("vm", "")]
    public async Task An_empty_expected_vm_id_or_nonce_is_refused_before_anything_is_checked(string vmId, string nonce)
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendAsync(new Hello("t", vmId, nonce));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, new GuestHandshake(vmId, nonce)));
        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    private sealed class ThrowingSink : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("sink exploded");
    }
}
