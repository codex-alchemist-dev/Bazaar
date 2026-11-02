using System.IO.Pipelines;
using System.Text;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

/// <summary>Runs a guest agent over in-memory pipes and plays the host side of the channel.</summary>
internal sealed class GuestHarness : IAsyncDisposable
{
    /// <summary>Longest any single host read or wait may take. Only a hang reaches it.</summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private readonly Pipe _toAgent = new();
    private readonly Pipe _fromAgent = new();
    private readonly Stream _hostWrite;
    private readonly Stream _hostRead;
    private readonly Task<int> _run;
    private bool _inputClosed;

    /// <summary>Starts the agent. Its Hello is the first frame the host can read.</summary>
    public GuestHarness(GuestAgentOptions options, IPtyHost? ptyHost = null)
    {
        _hostWrite = _toAgent.Writer.AsStream(leaveOpen: true);
        _hostRead = _fromAgent.Reader.AsStream(leaveOpen: true);
        var transport = new StreamGuestTransport(
            _toAgent.Reader.AsStream(leaveOpen: true),
            _fromAgent.Writer.AsStream(leaveOpen: true));
        _run = RunAgentAsync(new GuestAgent(options, transport, ptyHost));
    }

    /// <summary>Completes with the agent's exit code.</summary>
    public Task<int> Run => _run;

    /// <summary>Sends one frame to the agent.</summary>
    public async Task SendAsync(GuestMessage message)
    {
        using var bound = Bounded();
        await GuestFrameCodec.WriteAsync(_hostWrite, message, bound.Token);
    }

    /// <summary>Sends raw bytes, for frames the host codec cannot encode and for file content.</summary>
    public async Task SendRawAsync(byte[] bytes)
    {
        using var bound = Bounded();
        await _hostWrite.WriteAsync(bytes, bound.Token);
        await _hostWrite.FlushAsync(bound.Token);
    }

    /// <summary>Reads the next frame from the agent, or null when the agent has finished.</summary>
    public async Task<GuestMessage?> ReceiveOrNullAsync()
    {
        using var bound = Bounded();
        return await GuestFrameCodec.ReadAsync(_hostRead, bound.Token);
    }

    /// <summary>Reads the next frame, which must exist.</summary>
    public async Task<GuestMessage> ReceiveAsync() =>
        await ReceiveOrNullAsync() ?? throw new InvalidOperationException("The agent closed the channel.");

    /// <summary>Reads the next frame and checks its type.</summary>
    public async Task<T> ReceiveAsync<T>() where T : GuestMessage
    {
        var message = await ReceiveAsync();
        return message as T ?? throw new InvalidOperationException($"Expected {typeof(T).Name} but got {message.GetType().Name}.");
    }

    /// <summary>Reads exactly <paramref name="count"/> raw bytes.</summary>
    public async Task<byte[]> ReceiveRawAsync(int count)
    {
        using var bound = Bounded();
        var bytes = new byte[count];
        await _hostRead.ReadExactlyAsync(bytes, bound.Token);
        return bytes;
    }

    /// <summary>Sends a heartbeat and requires the very next frame to be its echo, so nothing was queued before it.</summary>
    public async Task SyncAsync()
    {
        await SendAsync(new Heartbeat());
        await ReceiveAsync<Heartbeat>();
    }

    /// <summary>Reads frames until the exec's Exit, keeping the exec's output.</summary>
    public async Task<ExecResult> CollectExecAsync(string id)
    {
        var outputs = new List<Output>();
        while (true)
        {
            var message = await ReceiveAsync();
            if (message is Output output && output.Id == id)
            {
                outputs.Add(output);
            }
            else if (message is Exit exit && exit.Id == id)
            {
                return new ExecResult(outputs, exit);
            }
        }
    }

    /// <summary>Reads frames until the first output chunk of the exec arrives.</summary>
    public async Task<Output> ReceiveFirstOutputAsync(string id)
    {
        while (true)
        {
            if (await ReceiveAsync() is Output output && output.Id == id)
            {
                return output;
            }
        }
    }

    /// <summary>Closes the host side of the channel, as a host that is done would.</summary>
    public void CloseInput()
    {
        if (!_inputClosed)
        {
            _inputClosed = true;
            _toAgent.Writer.Complete();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _fromAgent.Reader.Complete();   // a blocked agent writer fails instead of waiting for a reader
        CloseInput();
        await _run.WaitAsync(Bound);
        await _hostWrite.DisposeAsync();
        await _hostRead.DisposeAsync();
    }

    private async Task<int> RunAgentAsync(GuestAgent agent)
    {
        try
        {
            return await agent.RunAsync(CancellationToken.None);
        }
        finally
        {
            _fromAgent.Writer.Complete();   // the host sees end of stream once the agent is gone
        }
    }

    private static CancellationTokenSource Bounded() => new(Bound);
}

/// <summary>An exec's output chunks and its final Exit.</summary>
internal sealed record ExecResult(IReadOnlyList<Output> Outputs, Exit Exit)
{
    /// <summary>All bytes of one stream, in order.</summary>
    public byte[] Bytes(OutputSource stream) =>
        Outputs.Where(output => output.Stream == stream).SelectMany(output => output.Bytes).ToArray();

    /// <summary>All bytes of one stream as UTF-8 text.</summary>
    public string Text(OutputSource stream) => Encoding.UTF8.GetString(Bytes(stream));

    /// <summary>The Seq values of one stream, in arrival order.</summary>
    public IReadOnlyList<long> Seqs(OutputSource stream) =>
        Outputs.Where(output => output.Stream == stream).Select(output => output.Seq).ToArray();
}

/// <summary>A temporary directory that is removed when the test ends.</summary>
internal sealed class TempRoot : IAsyncDisposable
{
    /// <summary>Creates an empty directory under the system temp path.</summary>
    public TempRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-guest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Full path of the directory.</summary>
    public string Root { get; }

    /// <summary>Options that allow this directory and nothing else.</summary>
    public GuestAgentOptions Options(int maxExecs = 4) => new()
    {
        VmId = "vm-test",
        Nonce = "nonce-test",
        AllowedRoots = [Root],
        MaxConcurrentExecs = maxExecs,
        HeartbeatInterval = TimeSpan.FromHours(1),
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync()
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

        return ValueTask.CompletedTask;
    }
}
