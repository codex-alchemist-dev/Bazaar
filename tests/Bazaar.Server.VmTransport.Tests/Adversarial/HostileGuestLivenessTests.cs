using Bazaar.Contracts.Vms;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmTransport.Tests.Adversarial;

/// <summary>A guest that goes quiet, or never reads: the host must give up on it, not wait.</summary>
public class HostileGuestLivenessTests
{
    [Fact]
    public async Task A_silent_guest_is_lost_after_the_allowed_missed_heartbeats()
    {
        var time = new FakeTimeProvider();
        var options = new GuestChannelOptions { HeartbeatInterval = TimeSpan.FromSeconds(30), MissedHeartbeatLimit = 3 };
        var (rig, channel) = await ManualRig.OpenAsync(options, time);
        await using (rig)
        await using (channel)
        {
            for (var i = 0; i < 4; i++)
            {
                time.Advance(options.HeartbeatInterval);
                await Task.Delay(50);
            }

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Lost, channel.State);
        }
    }

    [Fact]
    public async Task A_guest_that_never_reads_faults_the_channel_within_the_write_timeout()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { WriteTimeout = TimeSpan.FromMilliseconds(300) });
        await using (rig)
        await using (channel)
        {
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

            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Contains("stopped reading", channel.EndReason, StringComparison.Ordinal);
        }
    }
}
