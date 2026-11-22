using System.Threading.Channels;
using Avalonia.Headless;
using Avalonia.Threading;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Session;

namespace Bazaar.Client.Calcinato.Avalonia.Tests;

/// <summary>A byte stream the test drives. Records writes and resizes. No PTY is involved.</summary>
internal sealed class FakeSource(bool canWrite = true) : ITerminalSource
{
    private readonly Channel<TerminalChunk> _output = Channel.CreateUnbounded<TerminalChunk>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<byte[]> _writes = [];
    private readonly List<(int Cols, int Rows)> _resizes = [];

    public bool IsRemote => false;

    public bool CanWrite { get; } = canWrite;

    public System.Threading.Channels.ChannelReader<TerminalChunk> Output => _output.Reader;

    public Task<int> Exited => _exited.Task;

    public IReadOnlyList<byte[]> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToArray();
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

    public void Emit(string text) => _output.Writer.TryWrite(new TerminalChunk(0, System.Text.Encoding.UTF8.GetBytes(text)));

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        lock (_gate)
        {
            _writes.Add(bytes.ToArray());
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
        _output.Writer.TryComplete();
        _exited.TrySetResult(0);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Opens <see cref="FakeSource"/> streams for the session manager.</summary>
internal sealed class FakeHost : ITerminalHost
{
    public List<FakeSource> Sources { get; } = [];

    public bool SupportsLocal => true;

    public ITerminalSource OpenLocal(TerminalSpec spec)
    {
        var source = new FakeSource();
        Sources.Add(source);
        return source;
    }

    public Task<ITerminalSource> AttachRemoteAsync(string terminalId, int cols, int rows, CancellationToken ct) =>
        throw new NotSupportedException("The fake host has no remote terminals.");
}

/// <summary>Helpers for headless tests: pump the UI thread until a condition holds, and run a ValueTask to completion.</summary>
internal static class Pump
{
    public static void Until(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {because}");
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Runs one layout and render pass, so bounds set on the window reach the controls.</summary>
    public static void Layout()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static void Run(ValueTask task) => task.AsTask().GetAwaiter().GetResult();
}
