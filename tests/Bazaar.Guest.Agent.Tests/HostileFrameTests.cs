using System.Buffers.Binary;
using System.Text;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

/// <summary>Frames that decode as JSON but are missing fields or carry out-of-range values must end the channel, not the process.</summary>
public sealed class HostileFrameTests
{
    [Theory]
    [InlineData("exec", "{}")]
    [InlineData("exec", """{"id":"a","argv":null,"cwd":"x","envAllowlist":null,"timeoutSec":5}""")]
    [InlineData("exec", """{"id":"a","argv":["x"],"cwd":"x","envAllowlist":null,"timeoutSec":5}""")]
    [InlineData("putFile", "{}")]
    [InlineData("getFile", "{}")]
    [InlineData("cancel", "{}")]
    [InlineData("ptyOpen", "{}")]
    [InlineData("ptyData", "{}")]
    [InlineData("ptyResize", "{}")]
    [InlineData("ptyClose", "{}")]
    public async Task A_frame_with_missing_fields_closes_the_channel_and_does_not_crash_the_agent(string tag, string body)
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendRawAsync(RawFrame(tag, body));

        Assert.Equal(1, await guest.Run.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken));
    }

    private static byte[] RawFrame(string tag, string body)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var counted = 2 + tagBytes.Length + bodyBytes.Length;
        var frame = new byte[4 + counted];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)counted);
        frame[4] = GuestFrameCodec.Version;
        frame[5] = (byte)tagBytes.Length;
        tagBytes.CopyTo(frame, 6);
        bodyBytes.CopyTo(frame, 6 + tagBytes.Length);
        return frame;
    }
}
