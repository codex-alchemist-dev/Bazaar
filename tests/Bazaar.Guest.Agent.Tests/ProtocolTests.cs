using System.Buffers.Binary;
using System.Text;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Hello_is_the_first_frame_and_carries_vm_id_and_nonce()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());

        var hello = await guest.ReceiveAsync<Hello>();

        Assert.Equal("vm-test", hello.VmId);
        Assert.Equal("nonce-test", hello.Nonce);
        Assert.False(string.IsNullOrEmpty(hello.AgentVersion));
    }

    [Fact]
    public async Task Heartbeat_from_the_host_is_echoed()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Heartbeat());

        await guest.ReceiveAsync<Heartbeat>();
    }

    [Fact]
    public async Task Agent_sends_heartbeats_on_its_own()
    {
        await using var root = new TempRoot();
        var options = root.Options() with { HeartbeatInterval = TimeSpan.FromMilliseconds(20) };
        await using var guest = new GuestHarness(options);
        await guest.ReceiveAsync<Hello>();

        // The host sends nothing, so the first heartbeat is the agent's own.
        await guest.ReceiveAsync<Heartbeat>();
    }

    [Fact]
    public async Task Unknown_frame_type_closes_the_channel_with_a_nonzero_exit()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendRawAsync(RawFrame("bogus", "{}"));

        Assert.NotEqual(0, await guest.Run.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken));
        Assert.Null(await guest.ReceiveOrNullAsync());
    }

    [Fact]
    public async Task Host_closing_its_side_ends_the_agent_with_exit_zero()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        guest.CloseInput();

        Assert.Equal(0, await guest.Run.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Guest_only_frame_from_the_host_closes_the_channel()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Exit("x", 0, false));

        Assert.Equal(1, await guest.Run.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Parse_reads_vm_id_nonce_roots_and_limits()
    {
        // Roots must be absolute on the running platform, so build them from the platform's own root path.
        var rootA = Path.Combine(Path.GetPathRoot(Path.GetFullPath("."))!, "srv", "a");
        var rootB = Path.Combine(Path.GetPathRoot(Path.GetFullPath("."))!, "srv", "b");
        var options = GuestAgentOptions.Parse(
            ["--vm-id", "vm-9", "--nonce", "n-9", "--root", rootA, "--root", rootB, "--max-execs", "2"]);

        Assert.Equal("vm-9", options.VmId);
        Assert.Equal("n-9", options.Nonce);
        Assert.Equal([rootA, rootB], options.AllowedRoots);
        Assert.Equal(2, options.MaxConcurrentExecs);
    }

    [Fact]
    public void Parse_requires_vm_id()
    {
        Assert.Throws<ArgumentException>(() => GuestAgentOptions.Parse(["--nonce", "n", "--root", "/srv/a"]));
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
