using System.Security.Cryptography;
using Bazaar.Contracts.Vms;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmTransport.Tests.Adversarial;

/// <summary>A guest that answers file requests wrongly, endlessly or slowly.</summary>
public class HostileGuestFileTests
{
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task A_put_file_nobody_asked_for_ends_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(new PutFile("/g/f", 1, Sha256([1])));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_get_file_error_nobody_asked_for_ends_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(new GetFileError("/g/f", "x"));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_put_file_result_while_a_read_is_pending_ends_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFileResult("/g/f", true, null));

            await Assert.ThrowsAsync<GuestChannelException>(() => get.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_put_file_result_for_another_path_than_the_write_asked_for_ends_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var put = channel.PutFileAsync("/g/a", new MemoryStream([1]), Sha256([1]), CancellationToken.None);
            await rig.ReceiveAsync<PutFile>();
            await rig.SendRawAsync([1]);
            await rig.SendAsync(new PutFileResult("/g/other", true, null));

            await Assert.ThrowsAsync<GuestChannelException>(() => put.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_content_answer_for_a_different_path_than_the_read_asked_for_ends_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var sink = new MemoryStream();
            var get = channel.GetFileAsync("/g/wanted", sink, CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFile("/g/other", 3, Sha256([1, 2, 3])));
            await rig.SendRawAsync([1, 2, 3]);

            await Assert.ThrowsAsync<GuestChannelException>(() => get.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Equal(0, sink.Length);
        }
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public async Task A_negative_size_ends_the_channel(long size)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFile("/g/f", size, Sha256([])));

            await Assert.ThrowsAsync<GuestChannelException>(() => get.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Theory]
    [InlineData(5L)]
    [InlineData(long.MaxValue)]
    public async Task A_size_over_the_limit_faults_the_channel_at_the_header_and_names_the_limit(long size)
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxFileBytes = 4 });
        await using (rig)
        await using (channel)
        {
            var sink = new MemoryStream();
            var get = channel.GetFileAsync("/g/f", sink, CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            // No content follows: a host that kept reading "to stay in step" would hang here instead.
            await rig.SendAsync(new PutFile("/g/f", size, Sha256([])));

            var failure = await Assert.ThrowsAsync<GuestFileException>(() => get.WithinAsync());
            Assert.Contains("4-byte limit", failure.Message, StringComparison.Ordinal);
            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Equal(0, sink.Length);
        }
    }

    [Fact]
    public async Task A_content_hash_mismatch_fails_the_read_and_keeps_the_stream_in_step()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFile("/g/f", 3, Sha256([9, 9, 9])));
            await rig.SendRawAsync([1, 2, 3]);

            var failure = await Assert.ThrowsAsync<GuestFileException>(() => get.WithinAsync());
            Assert.Contains("SHA-256", failure.Message, StringComparison.Ordinal);
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task Content_shorter_than_declared_followed_by_close_fails_the_read_and_faults_the_channel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFile("/g/f", 100, Sha256(new byte[100])));
            await rig.SendRawAsync(new byte[10]);
            await rig.CloseGuestAsync();

            await Assert.ThrowsAsync<GuestChannelException>(() => get.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public async Task A_guest_that_trickles_in_limit_content_loses_the_channel_when_the_transfer_deadline_passes()
    {
        var time = new FakeTimeProvider();
        var options = new GuestChannelOptions { FileTransferTimeout = TimeSpan.FromSeconds(10), HeartbeatInterval = TimeSpan.FromHours(1) };
        var (rig, channel) = await ManualRig.OpenAsync(options, time);
        await using (rig)
        await using (channel)
        {
            var get = channel.GetFileAsync("/g/f", new MemoryStream(), CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFile("/g/f", 1000, Sha256(new byte[1000])));
            await rig.SendRawAsync([0]);
            await Task.Delay(100);
            Assert.Equal(GuestChannelState.Open, channel.State);

            time.Advance(TimeSpan.FromSeconds(11));

            await Assert.ThrowsAsync<GuestChannelException>(() => get.WithinAsync());
            Assert.Equal(GuestChannelState.Faulted, channel.State);
            Assert.Contains("took longer", channel.EndReason, StringComparison.Ordinal);

            // The file gate was released: the failure does not strand later callers on a dead channel.
            await Assert.ThrowsAsync<GuestChannelException>(() => channel.GetFileAsync("/g/g", new MemoryStream(), CancellationToken.None).WithinAsync());
        }
    }

    [Fact]
    public async Task A_transfer_that_finishes_inside_the_deadline_is_not_affected_by_it()
    {
        var time = new FakeTimeProvider();
        var options = new GuestChannelOptions { FileTransferTimeout = TimeSpan.FromSeconds(10), HeartbeatInterval = TimeSpan.FromHours(1) };
        var (rig, channel) = await ManualRig.OpenAsync(options, time);
        await using (rig)
        await using (channel)
        {
            var sink = new MemoryStream();
            var get = channel.GetFileAsync("/g/f", sink, CancellationToken.None);
            await rig.ReceiveAsync<GetFile>();
            await rig.SendAsync(new PutFile("/g/f", 3, Sha256([1, 2, 3])));
            await rig.SendRawAsync([1, 2, 3]);
            await get.WithinAsync();
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(50);

            Assert.Equal([1, 2, 3], sink.ToArray());
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }
}
