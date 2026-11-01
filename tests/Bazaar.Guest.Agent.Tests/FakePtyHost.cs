using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Bazaar.Guest.Agent.Tests;

/// <summary>Records terminal opens and hands the test the session it created.</summary>
internal sealed class FakePtyHost : IPtyHost
{
    private readonly TaskCompletionSource<FakePtySession> _opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the agent opens a terminal.</summary>
    public Task<FakePtySession> Opened => _opened.Task;

    /// <summary>Shell path of the last open.</summary>
    public string? ShellPath { get; private set; }

    /// <summary>Columns of the last open.</summary>
    public int Cols { get; private set; }

    /// <summary>Rows of the last open.</summary>
    public int Rows { get; private set; }

    /// <inheritdoc />
    public IPtySession Open(string shellPath, int cols, int rows)
    {
        ShellPath = shellPath;
        Cols = cols;
        Rows = rows;
        var session = new FakePtySession();
        _opened.TrySetResult(session);
        return session;
    }
}

/// <summary>A terminal session the test drives: it emits output and records input, resizes and close.</summary>
internal sealed class FakePtySession : IPtySession
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Input bytes the agent wrote, in order.</summary>
    public ConcurrentQueue<byte[]> Input { get; } = new();

    /// <summary>Sizes the agent applied, in order.</summary>
    public ConcurrentQueue<(int Cols, int Rows)> Resizes { get; } = new();

    /// <summary>Completes when the agent disposes the session.</summary>
    public Task Closed => _closed.Task;

    /// <inheritdoc />
    public ChannelReader<byte[]> Output => _output.Reader;

    /// <summary>Simulates shell output.</summary>
    public async Task EmitAsync(byte[] bytes) => await _output.Writer.WriteAsync(bytes);

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        Input.Enqueue(bytes.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows) => Resizes.Enqueue((cols, rows));

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _output.Writer.TryComplete();
        _closed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
