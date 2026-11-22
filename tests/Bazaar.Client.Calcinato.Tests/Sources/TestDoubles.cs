using System.Collections.Concurrent;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Tests.Sources;

/// <summary>In-memory PTY session. Output and exit are driven by the test.</summary>
internal sealed class FakePtySession : IPtySession
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<byte[]> _writes = new();
    private readonly ConcurrentQueue<(int Cols, int Rows)> _resizes = new();

    public ChannelReader<byte[]> Output => _output.Reader;

    public Task<int> Exited => _exited.Task;

    public bool Disposed { get; private set; }

    public IReadOnlyCollection<byte[]> Writes => _writes.ToArray();

    public IReadOnlyCollection<(int Cols, int Rows)> Resizes => _resizes.ToArray();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        _writes.Enqueue(bytes.ToArray());
        return ValueTask.CompletedTask;
    }

    public void Resize(int cols, int rows) => _resizes.Enqueue((cols, rows));

    public void Emit(byte[] bytes) => _output.Writer.TryWrite(bytes);

    public void Finish(int code)
    {
        _exited.TrySetResult(code);
        _output.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Finish(-1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>PTY backend that hands out one fake session per Open call.</summary>
internal sealed class FakePtyBackend(bool isSupported) : IPtyBackend
{
    public bool IsSupported { get; } = isSupported;

    public List<TerminalSpec> Opened { get; } = [];

    public FakePtySession? Last { get; private set; }

    public IPtySession Open(TerminalSpec spec)
    {
        Opened.Add(spec);
        Last = new FakePtySession();
        return Last;
    }
}

/// <summary>Wire fake: the test pushes server frames and inspects the frames the source sent.</summary>
internal sealed class FakeWire : ITerminalWire
{
    private readonly Channel<TerminalFrame> _incoming = Channel.CreateUnbounded<TerminalFrame>();
    private readonly ConcurrentQueue<TerminalFrame> _sent = new();

    public ChannelReader<TerminalFrame> Incoming => _incoming.Reader;

    public IReadOnlyList<TerminalFrame> Sent => _sent.ToArray();

    public ValueTask SendAsync(TerminalFrame frame, CancellationToken ct)
    {
        _sent.Enqueue(frame);
        return ValueTask.CompletedTask;
    }

    public void Push(TerminalFrame frame) => _incoming.Writer.TryWrite(frame);

    public void Close() => _incoming.Writer.TryComplete();

    public IEnumerable<T> SentOf<T>() where T : TerminalFrame => Sent.OfType<T>();
}

/// <summary>Hub fake that returns one shared wire and records the ids asked for.</summary>
internal sealed class FakeHub(ITerminalWire wire) : ITerminalWireHub
{
    public List<string> Requested { get; } = [];

    public ITerminalWire ForTerminal(string id)
    {
        Requested.Add(id);
        return wire;
    }
}

internal static class Eventually
{
    /// <summary>Polls until the condition holds, failing after five seconds.</summary>
    public static async Task TrueAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Reads the next item, failing after five seconds.</summary>
    public static async Task<T> NextAsync<T>(ChannelReader<T> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadAsync(cts.Token);
    }
}
