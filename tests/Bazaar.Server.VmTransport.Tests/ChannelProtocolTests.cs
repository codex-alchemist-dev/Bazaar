using Bazaar.Contracts.Vms;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>The handshake, frames a host must not accept, a fuzzed stream, and heartbeat loss.</summary>
public class ChannelProtocolTests
{
    private static readonly GuestHandshake _expected = new(AgentRig.VmId, AgentRig.Nonce);

    [Theory]
    [InlineData("other-vm", AgentRig.Nonce)]
    [InlineData(AgentRig.VmId, "stale-nonce")]
    [InlineData(AgentRig.VmId, "")]
    public async Task A_hello_with_the_wrong_vm_id_or_nonce_closes_the_connection(string vmId, string nonce)
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendAsync(new Hello("test", vmId, nonce));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));

        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    [Fact]
    public async Task A_first_frame_that_is_not_a_hello_closes_the_connection()
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendAsync(new Heartbeat());

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));

        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    [Fact]
    public async Task A_first_frame_with_an_unknown_type_closes_the_connection()
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendRawAsync(ManualRig.RawFrame("bogus", "{}"));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));
    }

    [Fact]
    public async Task A_guest_that_never_says_hello_is_closed_after_the_hello_timeout()
    {
        await using var rig = await ManualRig.ConnectAsync();

        var error = await Assert.ThrowsAsync<GuestHandshakeException>(
            () => GuestChannel.OpenAsync(rig.Connection, _expected, new GuestChannelOptions { HelloTimeout = TimeSpan.FromMilliseconds(100) }));

        Assert.Contains("in time", error.Message, StringComparison.Ordinal);
        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    [Fact]
    public async Task A_guest_that_closes_before_hello_is_a_handshake_failure()
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.CloseGuestAsync();

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));
    }

    [Fact]
    public async Task A_matching_hello_opens_the_channel_and_reports_the_agent_version()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            Assert.Equal(GuestChannelState.Open, channel.State);
            Assert.Equal("test", channel.AgentVersion);
            Assert.Null(channel.EndReason);
        }
    }

    [Fact]
    public async Task An_unknown_frame_type_closes_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendRawAsync(ManualRig.RawFrame("bogus", "{}"));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Contains("bogus", channel.EndReason, StringComparison.Ordinal);
            await Assert.ThrowsAsync<GuestChannelException>(() => channel.OpenPtyAsync(80, 24, CancellationToken.None));
        }
    }

    [Theory]
    [MemberData(nameof(HostOnlyFrames))]
    public async Task A_frame_only_the_host_may_send_closes_the_channel(GuestMessage frame)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(frame);

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    public static TheoryData<GuestMessage> HostOnlyFrames() =>
    [
        new Hello("x", "y", "z"),
        new Exec("a", ["b"], "/", [], 1),
        new Cancel("a"),
        new GetFile("/x"),
        new PtyOpen("a", 1, 1),
        new PtyResize("a", 1, 1),
        new PtyClose("a"),
    ];

    [Fact]
    public async Task An_unsolicited_file_answer_closes_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(new PutFileResult("/x", true, null));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_frame_over_the_size_limit_closes_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendRawAsync([0x7f, 0xff, 0xff, 0xff]);

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_guest_that_closes_cleanly_ends_the_channel_as_closed()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.CloseGuestAsync();

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Closed, channel.State);
        }
    }

    [Fact]
    public async Task Disposing_the_channel_closes_the_connection_so_the_guest_sees_the_end()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        {
            await channel.DisposeAsync();
            await channel.DisposeAsync();

            Assert.Null(await rig.ReceiveOrNullAsync());
            Assert.Equal(GuestChannelState.Closed, channel.State);
        }
    }

    [Fact]
    public async Task Fuzzed_bytes_after_a_good_hello_always_end_the_channel_without_a_hang_or_a_stray_exception()
    {
        var valid = new List<byte[]>();
        foreach (var message in new GuestMessage[]
        {
            new Output("e", OutputSource.Stdout, 0, [1, 2, 3]),
            new Exit("e", 0, false),
            new PtyData("p", 0, [4]),
            new PutFileResult("/x", true, null),
            new Heartbeat(),
        })
        {
            using var ms = new MemoryStream();
            await GuestFrameCodec.WriteAsync(ms, message, CancellationToken.None);
            valid.Add(ms.ToArray());
        }

        var random = new Random(20261118);
        for (var round = 0; round < 150; round++)
        {
            byte[] garbage;
            if (round % 2 == 0)
            {
                garbage = new byte[random.Next(1, 200)];
                random.NextBytes(garbage);
            }
            else
            {
                // A valid frame with a few bytes flipped, or cut short.
                garbage = [.. valid[random.Next(valid.Count)]];
                for (var flips = random.Next(1, 4); flips > 0; flips--)
                {
                    garbage[random.Next(garbage.Length)] ^= (byte)random.Next(1, 256);
                }

                if (random.Next(3) == 0)
                {
                    garbage = garbage[..random.Next(1, garbage.Length)];
                }
            }

            var (rig, channel) = await ManualRig.OpenAsync();
            await using (rig)
            await using (channel)
            {
                await rig.SendRawAsync(garbage);
                await rig.CloseGuestAsync();

                await channel.Completion.WithinAsync();
                Assert.NotEqual(GuestChannelState.Open, channel.State);
                Assert.NotNull(channel.EndReason);
            }
        }
    }

    [Fact]
    public async Task Three_silent_heartbeat_intervals_make_the_channel_lost_and_fail_open_execs()
    {
        var time = new FakeTimeProvider();
        var options = new GuestChannelOptions { HeartbeatInterval = TimeSpan.FromSeconds(30) };
        var (rig, channel) = await ManualRig.OpenAsync(options, time);
        await using (rig)
        await using (channel)
        {
            var run = channel.ExecAsync(
                new ExecRequest(["x"], "/", [], TimeSpan.FromMinutes(9)),
                _ => Task.CompletedTask,
                CancellationToken.None);
            await rig.ReceiveAsync<Exec>();

            // The first interval is covered by the Hello. Then three silent intervals pass.
            for (var i = 0; i < 3; i++)
            {
                await TickAsync(time, options.HeartbeatInterval);
                Assert.Equal(GuestChannelState.Open, channel.State);
            }

            await TickAsync(time, options.HeartbeatInterval);

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Lost, channel.State);
            await Assert.ThrowsAsync<GuestChannelException>(() => run.WithinAsync());
        }
    }

    [Fact]
    public async Task A_guest_that_answers_each_heartbeat_keeps_the_channel_open()
    {
        var time = new FakeTimeProvider();
        var options = new GuestChannelOptions { HeartbeatInterval = TimeSpan.FromSeconds(30) };
        var (rig, channel) = await ManualRig.OpenAsync(options, time);
        await using (rig)
        await using (channel)
        {
            for (var i = 0; i < 8; i++)
            {
                await TickAsync(time, options.HeartbeatInterval);
                await rig.ReceiveAsync<Heartbeat>();
                await rig.SendAsync(new Heartbeat());
            }

            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    /// <summary>Moves fake time one interval and lets the watchdog loop run. One interval at a time, because ticks coalesce.</summary>
    private static async Task TickAsync(FakeTimeProvider time, TimeSpan interval)
    {
        time.Advance(interval);
        await Task.Delay(50);
    }
}
