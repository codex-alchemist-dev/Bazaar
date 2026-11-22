using System.Buffers.Binary;
using Bazaar.Contracts.Vms;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmTransport.Tests.Adversarial;

/// <summary>A guest that lies, stalls or sends garbage before the channel is open. Every case ends with the connection closed and no channel.</summary>
public class HostileGuestHandshakeTests
{
    private static readonly GuestHandshake _expected = new(AgentRig.VmId, AgentRig.Nonce);

    [Theory]
    [InlineData("vm-other", AgentRig.Nonce)]
    [InlineData(AgentRig.VmId + " ", AgentRig.Nonce)]
    [InlineData("VM-TEST", AgentRig.Nonce)]
    [InlineData(AgentRig.VmId, "nonce-tes")]
    [InlineData(AgentRig.VmId, AgentRig.Nonce + "x")]
    [InlineData(AgentRig.VmId, "NONCE-TEST")]
    public async Task A_hello_with_a_near_miss_vm_id_or_nonce_is_refused(string vmId, string nonce)
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendAsync(new Hello("evil", vmId, nonce));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));

        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    [Theory]
    [InlineData("""{"agentVersion":null,"vmId":"vm-test","nonce":"nonce-test"}""")]
    [InlineData("""{"agentVersion":"1","vmId":null,"nonce":"nonce-test"}""")]
    [InlineData("""{"agentVersion":"1","vmId":"vm-test","nonce":null}""")]
    [InlineData("""{"agentVersion":"1","vmId":"vm-test"}""")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"agentVersion":1,"vmId":2,"nonce":3}""")]
    public async Task A_hello_with_null_or_missing_fields_is_refused(string body)
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendRawAsync(ManualRig.RawFrame("hello", body));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));

        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    [Theory]
    [MemberData(nameof(NotHellos))]
    public async Task A_first_frame_that_is_anything_but_hello_is_refused(GuestMessage first)
    {
        await using var rig = await ManualRig.ConnectAsync();
        await rig.SendAsync(first);

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));

        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    public static TheoryData<GuestMessage> NotHellos() =>
    [
        new Heartbeat(),
        new Output("e", OutputSource.Stdout, 0, [1]),
        new Exit("e", 0, false),
        new PutFileResult("/x", true, null),
        new Exec("a", ["b"], "/", [], 1),
    ];

    [Fact]
    public async Task A_guest_that_sends_nothing_is_closed_when_the_hello_timeout_passes()
    {
        var time = new FakeTimeProvider();
        await using var rig = await ManualRig.ConnectAsync();
        var open = GuestChannel.OpenAsync(rig.Connection, _expected, new GuestChannelOptions { HelloTimeout = TimeSpan.FromSeconds(5) }, time);

        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => open.WithinAsync());
        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    [Fact]
    public async Task A_guest_that_trickles_half_a_frame_cannot_outlast_the_hello_timeout()
    {
        var time = new FakeTimeProvider();
        await using var rig = await ManualRig.ConnectAsync();
        var open = GuestChannel.OpenAsync(rig.Connection, _expected, new GuestChannelOptions { HelloTimeout = TimeSpan.FromSeconds(5) }, time);

        // A length prefix that promises a body, and one byte of it: the frame never completes.
        await rig.SendRawAsync([0, 0, 0, 50, 2]);
        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<GuestHandshakeException>(() => open.WithinAsync());
        Assert.Null(await rig.ReceiveOrNullAsync());
    }

    [Fact]
    public async Task A_truncated_first_frame_is_refused()
    {
        await using var rig = await ManualRig.ConnectAsync();
        var frame = ManualRig.RawFrame("hello", """{"agentVersion":"1","vmId":"vm-test","nonce":"nonce-test"}""");
        await rig.SendRawAsync(frame[..(frame.Length - 5)]);
        await rig.CloseGuestAsync();

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData((uint)GuestFrameCodec.MaxFrameBytes + 1)]
    public async Task A_giant_declared_length_in_the_first_frame_is_refused_without_reading_the_body(uint length)
    {
        await using var rig = await ManualRig.ConnectAsync();
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, length);
        await rig.SendRawAsync(prefix);

        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(rig.Connection, _expected));
    }

    [Fact]
    public async Task An_unknown_type_or_protocol_version_in_the_first_frame_is_refused()
    {
        await using var first = await ManualRig.ConnectAsync();
        await first.SendRawAsync(ManualRig.RawFrame("hello2", "{}"));
        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(first.Connection, _expected));

        await using var second = await ManualRig.ConnectAsync();
        await second.SendRawAsync(ManualRig.RawFrame("hello", """{"agentVersion":"1","vmId":"vm-test","nonce":"nonce-test"}""", version: 9));
        await Assert.ThrowsAsync<GuestHandshakeException>(() => GuestChannel.OpenAsync(second.Connection, _expected));
    }

    [Fact]
    public async Task A_second_hello_after_the_channel_is_open_ends_it()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await rig.SendAsync(new Hello("again", AgentRig.VmId, AgentRig.Nonce));

            await channel.Completion.WithinAsync();
            Assert.Equal(GuestChannelState.Faulted, channel.State);
        }
    }

    [Fact]
    public void The_file_transfer_timeout_must_be_above_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GuestChannelOptions { FileTransferTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GuestChannelOptions { FileTransferTimeout = TimeSpan.FromSeconds(-1) });
        Assert.Equal(TimeSpan.FromMinutes(30), new GuestChannelOptions().FileTransferTimeout);
    }
}
