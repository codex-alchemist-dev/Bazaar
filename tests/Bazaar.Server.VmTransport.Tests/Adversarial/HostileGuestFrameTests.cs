using System.Buffers.Binary;
using Bazaar.Contracts.Vms;

namespace Bazaar.Server.VmTransport.Tests.Adversarial;

/// <summary>An open channel, and a guest that sends frames it must not: the channel ends, or the frame is ignored, and nothing else is harmed.</summary>
public class HostileGuestFrameTests
{
    private static readonly string[] _noEnv = [];

    [Theory]
    [MemberData(nameof(HostOnlyFrames))]
    public async Task A_frame_type_only_the_host_may_send_ends_the_channel(GuestMessage frame)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(frame);

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Contains(frame.GetType().Name, channel.EndReason, StringComparison.Ordinal);
        }
    }

    public static TheoryData<GuestMessage> HostOnlyFrames() =>
    [
        new Exec("a", ["/bin/sh"], "/", [], 1),
        new GetFile("/etc/shadow"),
        new Cancel("a"),
        new PtyOpen("a", 1, 1),
        new PtyResize("a", 1, 1),
        new PtyClose("a"),
    ];

    [Theory]
    [InlineData("exec2")]
    [InlineData("")]
    [InlineData("OUTPUT")]
    [InlineData("hello\0")]
    public async Task An_unknown_type_id_ends_the_channel(string tag)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendRawAsync(ManualRig.RawFrame(tag, "{}"));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_truncated_frame_followed_by_close_ends_the_channel_as_faulted()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var frame = ManualRig.RawFrame("heartbeat", "{}");
            await rig.SendRawAsync(frame[..(frame.Length - 1)]);
            await rig.CloseGuestAsync();

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x40000000u)]
    [InlineData((uint)GuestFrameCodec.MaxFrameBytes + 1)]
    public async Task A_giant_declared_frame_length_ends_the_channel_without_a_body(uint length)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, length);
            await rig.SendRawAsync(prefix);

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task Frames_about_ids_the_host_never_issued_are_ignored_and_the_channel_stays_usable()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(new Output("nobody", OutputSource.Stdout, 0, new byte[1000]));
            await rig.SendAsync(new Exit("nobody", 0, false));
            await rig.SendAsync(new ExecError("nobody", ExecRefusalCodes.TooManyExecs, "no"));
            await rig.SendAsync(new PtyData("nobody", 0, [1, 2, 3]));
            await rig.SendAsync(new PtyExit("nobody", 0));

            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();
            await rig.SendAsync(new Exit(exec.Id, 0, false));

            Assert.Equal(0, (await run.WithinAsync()).ExitCode);
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task An_exit_for_another_execs_id_does_not_finish_this_exec()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var first = channel.ExecAsync(new ExecRequest(["a"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, CancellationToken.None);
            var firstExec = await rig.ReceiveAsync<Exec>();
            var second = channel.ExecAsync(new ExecRequest(["b"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, CancellationToken.None);
            var secondExec = await rig.ReceiveAsync<Exec>();

            // Only the second exec reports; the first must not be touched, and an invented id must not be either.
            await rig.SendAsync(new Exit("invented", 99, false));
            await rig.SendAsync(new Exit(secondExec.Id, 7, false));
            Assert.Equal(7, (await second.WithinAsync()).ExitCode);
            await Task.Delay(100);
            Assert.False(first.IsCompleted);

            await rig.SendAsync(new Exit(firstExec.Id, 3, false));
            Assert.Equal(3, (await first.WithinAsync()).ExitCode);
        }
    }

    [Fact]
    public async Task An_output_flood_beyond_the_cap_is_truncated_and_never_queues_more_than_the_cap()
    {
        const int cap = 64 * 1024;
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxOutputBytes = cap });
        await using (rig)
        await using (channel)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = 0L;
            // The callback is stuck, so every accepted chunk stays queued in the channel: the worst case for memory.
            var run = channel.ExecAsync(
                new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)),
                async chunk =>
                {
                    Interlocked.Add(ref delivered, chunk.Text.Length);
                    await release.Task;
                },
                CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();

            var block = new byte[16 * 1024];
            Array.Fill(block, (byte)'a');
            for (var i = 0; i < 300; i++)
            {
                await rig.SendAsync(new Output(exec.Id, OutputSource.Stdout, i, block));
            }

            await rig.SendAsync(new Exit(exec.Id, 0, false));
            await Task.Delay(100);
            release.SetResult();
            var result = await run.WithinAsync();

            Assert.True(result.OutputTruncated);
            Assert.True(Interlocked.Read(ref delivered) <= cap, $"{delivered} characters were delivered, over the {cap} cap.");
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task An_output_frame_without_bytes_ends_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendRawAsync(ManualRig.RawFrame("output", """{"id":"x","stream":0,"seq":0,"bytes":null}"""));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_pty_flood_beyond_the_pending_limit_ends_only_that_terminal()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxPendingPtyBytes = 8 * 1024 });
        await using (rig)
        await using (channel)
        {
            await using var flooded = await channel.OpenPtyAsync(80, 24, CancellationToken.None);
            var floodedOpen = await rig.ReceiveAsync<PtyOpen>();
            await using var healthy = await channel.OpenPtyAsync(80, 24, CancellationToken.None);
            var healthyOpen = await rig.ReceiveAsync<PtyOpen>();

            var block = new byte[1024];
            for (var i = 0; i < 20; i++)
            {
                await rig.SendAsync(new PtyData(floodedOpen.Id, i, block));
            }

            Assert.Equal(-1, await flooded.Exited.WithinAsync());
            Assert.Equal(floodedOpen.Id, (await rig.ReceiveAsync<PtyClose>()).Id);

            await rig.SendAsync(new PtyData(healthyOpen.Id, 0, [42]));
            Assert.Equal([42], await healthy.Output.ReadAsync().AsTask().WithinAsync());
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }
}
