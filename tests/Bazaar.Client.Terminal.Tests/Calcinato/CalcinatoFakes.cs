using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Terminal.Calcinato;

namespace Bazaar.Client.Terminal.Tests.Calcinato;

/// <summary>A console the test drives: key presses go in, everything the host writes is kept for assertions.</summary>
internal sealed class FakeConsole(ConsoleSize size) : IConsoleSurface
{
    private readonly BlockingCollection<ConsoleKeyInfo> _keys = [];
    private readonly StringBuilder _output = new();
    private readonly Lock _gate = new();
    private ConsoleSize _size = size;

    public bool TreatControlCAsInput { get; set; }

    public ConsoleSize WindowSize
    {
        get
        {
            lock (_gate)
            {
                return _size;
            }
        }
    }

    public string Output
    {
        get
        {
            lock (_gate)
            {
                return _output.ToString();
            }
        }
    }

    public ConsoleKeyInfo ReadKey() => _keys.Take();

    public void Write(string text)
    {
        lock (_gate)
        {
            _output.Append(text);
        }
    }

    public void Resize(ConsoleSize size)
    {
        lock (_gate)
        {
            _size = size;
        }
    }

    public void Type(string text)
    {
        foreach (char c in text)
        {
            Press(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }
    }

    public void Press(ConsoleKeyInfo info) => _keys.Add(info);

    public void Press(ConsoleKey key, char keyChar = '\0', bool control = false) =>
        _keys.Add(new ConsoleKeyInfo(keyChar, key, false, false, control));

    public void EndInput() => _keys.CompleteAdding();
}

/// <summary>A PTY stand-in: records bytes the host sends and lets the test emit shell output.</summary>
internal sealed class FakeShell : ITerminalSource
{
    private readonly Channel<TerminalChunk> _output = Channel.CreateUnbounded<TerminalChunk>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<byte> _written = [];
    private readonly List<(int Cols, int Rows)> _resizes = [];
    private readonly Lock _gate = new();
    private long _sequence;

    public bool IsRemote => false;

    public bool CanWrite => true;

    public bool Disposed { get; private set; }

    public ChannelReader<TerminalChunk> Output => _output.Reader;

    public Task<int> Exited => _exited.Task;

    public string Written
    {
        get
        {
            lock (_gate)
            {
                return Encoding.UTF8.GetString(_written.ToArray());
            }
        }
    }

    public IReadOnlyList<(int Cols, int Rows)> Resizes
    {
        get
        {
            lock (_gate)
            {
                return _resizes.ToArray();
            }
        }
    }

    public void Emit(string text) =>
        _output.Writer.TryWrite(new TerminalChunk(++_sequence, Encoding.UTF8.GetBytes(text)));

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        lock (_gate)
        {
            _written.AddRange(bytes.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        lock (_gate)
        {
            _resizes.Add((cols, rows));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _output.Writer.TryComplete();
        _exited.TrySetResult(0);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A host with a local backend that hands out <see cref="FakeShell"/> values.</summary>
internal sealed class FakeHost : ITerminalHost
{
    private readonly List<FakeShell> _shells = [];
    private readonly List<TerminalSpec> _specs = [];
    private readonly Lock _gate = new();

    public bool SupportsLocal { get; init; } = true;

    public IReadOnlyList<FakeShell> Shells
    {
        get
        {
            lock (_gate)
            {
                return _shells.ToArray();
            }
        }
    }

    public IReadOnlyList<TerminalSpec> Specs
    {
        get
        {
            lock (_gate)
            {
                return _specs.ToArray();
            }
        }
    }

    public ITerminalSource OpenLocal(TerminalSpec spec)
    {
        if (!SupportsLocal)
        {
            throw new NotSupportedException("This head has no local terminal backend.");
        }

        var shell = new FakeShell();
        lock (_gate)
        {
            _shells.Add(shell);
            _specs.Add(spec);
        }

        return shell;
    }

    public Task<ITerminalSource> AttachRemoteAsync(string terminalId, int cols, int rows, CancellationToken ct) =>
        throw new NotSupportedException("Not used by these tests.");
}

/// <summary>Polls a condition on the test clock until it holds, failing the test after five seconds.</summary>
internal static class Wait
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    public static async Task For(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow + _timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {because}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
