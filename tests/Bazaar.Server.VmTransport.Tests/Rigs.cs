using System.Buffers.Binary;
using System.Text;
using Bazaar.Contracts.Vms;
using Bazaar.Guest.Agent;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>Longest any single wait in these tests may take. Only a hang reaches it.</summary>
internal static class Bound
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public static Task<T> WithinAsync<T>(this Task<T> task) => task.WaitAsync(Wait);

    public static Task WithinAsync(this Task task) => task.WaitAsync(Wait);
}

/// <summary>A temporary directory that is removed when the test ends.</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-vmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory does not affect other tests.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}

/// <summary>A real guest agent connected to a real host channel over the loopback listener.</summary>
internal sealed class AgentRig : IAsyncDisposable
{
    public const string PeerId = "peer-1";
    public const string VmId = "vm-test";
    public const string Nonce = "nonce-test";

    private readonly Task<int> _agentRun;
    private readonly LoopbackGuestEnd _guestEnd;

    private AgentRig(LoopbackListener listener, TempRoot root, GuestChannel channel, Task<int> agentRun, LoopbackGuestEnd guestEnd)
    {
        Listener = listener;
        Root = root;
        Channel = channel;
        _agentRun = agentRun;
        _guestEnd = guestEnd;
    }

    public LoopbackListener Listener { get; }

    public TempRoot Root { get; }

    public GuestChannel Channel { get; }

    public static async Task<AgentRig> StartAsync(GuestChannelOptions? options = null, TimeProvider? time = null, IPtyHost? pty = null)
    {
        var listener = new LoopbackListener();
        var root = new TempRoot();
        var accept = listener.AcceptAsync(PeerId, CancellationToken.None);
        var end = listener.Connect(PeerId);
        var agentOptions = new GuestAgentOptions
        {
            VmId = VmId,
            Nonce = Nonce,
            AllowedRoots = [root.Root],
            HeartbeatInterval = TimeSpan.FromHours(1),
        };
        var agent = new GuestAgent(agentOptions, new StreamGuestTransport(end.FromHost, end.ToHost), pty);
        var run = Task.Run(() => agent.RunAsync(CancellationToken.None));
        var channel = await GuestChannel.OpenAsync(await accept.AsTask().WithinAsync(), new GuestHandshake(VmId, Nonce), options, time);
        return new AgentRig(listener, root, channel, run, end);
    }

    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync();
        await _agentRun.WithinAsync();
        await _guestEnd.ToHost.DisposeAsync();
        await _guestEnd.FromHost.DisposeAsync();
        await Listener.DisposeAsync();
        Root.Dispose();
    }
}

/// <summary>A hand-driven guest: the test writes the frames, so it can send what a real agent never would.</summary>
internal sealed class ManualRig : IAsyncDisposable
{
    private readonly LoopbackGuestEnd _end;

    private ManualRig(LoopbackListener listener, LoopbackGuestEnd end, GuestConnection connection)
    {
        Listener = listener;
        _end = end;
        Connection = connection;
    }

    public LoopbackListener Listener { get; }

    public GuestConnection Connection { get; }

    /// <summary>Connects a guest and returns the rig without opening the channel, so the test controls what the first frame is.</summary>
    public static async Task<ManualRig> ConnectAsync()
    {
        var listener = new LoopbackListener();
        var accept = listener.AcceptAsync(AgentRig.PeerId, CancellationToken.None);
        var end = listener.Connect(AgentRig.PeerId);
        return new ManualRig(listener, end, await accept.AsTask().WithinAsync());
    }

    /// <summary>Connects a guest, sends a matching Hello, and opens the channel.</summary>
    public static async Task<(ManualRig Rig, GuestChannel Channel)> OpenAsync(GuestChannelOptions? options = null, TimeProvider? time = null)
    {
        var rig = await ConnectAsync();
        await rig.SendAsync(new Hello("test", AgentRig.VmId, AgentRig.Nonce));
        var channel = await GuestChannel.OpenAsync(rig.Connection, new GuestHandshake(AgentRig.VmId, AgentRig.Nonce), options, time);
        return (rig, channel);
    }

    public async Task SendAsync(GuestMessage message)
    {
        using var bound = new CancellationTokenSource(Bound.Wait);
        await GuestFrameCodec.WriteAsync(_end.ToHost, message, bound.Token);
    }

    public async Task SendRawAsync(byte[] bytes)
    {
        using var bound = new CancellationTokenSource(Bound.Wait);
        await _end.ToHost.WriteAsync(bytes, bound.Token);
        await _end.ToHost.FlushAsync(bound.Token);
    }

    /// <summary>Reads the next frame the host wrote, or null when the host closed the connection.</summary>
    public async Task<GuestMessage?> ReceiveOrNullAsync()
    {
        using var bound = new CancellationTokenSource(Bound.Wait);
        return await GuestFrameCodec.ReadAsync(_end.FromHost, bound.Token);
    }

    public async Task<T> ReceiveAsync<T>() where T : GuestMessage
    {
        var message = await ReceiveOrNullAsync() ?? throw new InvalidOperationException("The host closed the connection.");
        return message as T ?? throw new InvalidOperationException($"Expected {typeof(T).Name} but got {message.GetType().Name}.");
    }

    /// <summary>Ends the guest's direction, as a guest that exits would.</summary>
    public ValueTask CloseGuestAsync() => _end.ToHost.DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        await _end.ToHost.DisposeAsync();
        await _end.FromHost.DisposeAsync();
        await Listener.DisposeAsync();
    }

    /// <summary>Builds a frame with an arbitrary tag and body, for frames the codec itself would refuse to write.</summary>
    public static byte[] RawFrame(string tag, string json, byte version = GuestFrameCodec.Version)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag);
        var body = Encoding.UTF8.GetBytes(json);
        var counted = 2 + tagBytes.Length + body.Length;
        var frame = new byte[4 + counted];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)counted);
        frame[4] = version;
        frame[5] = (byte)tagBytes.Length;
        tagBytes.CopyTo(frame, 6);
        body.CopyTo(frame, 6 + tagBytes.Length);
        return frame;
    }
}
