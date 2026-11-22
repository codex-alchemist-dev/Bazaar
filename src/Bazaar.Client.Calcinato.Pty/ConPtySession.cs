using System.Runtime.InteropServices;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// One shell under a ConPTY. A reader thread copies the output pipe into <see cref="Output"/>. A waiter thread completes
/// <see cref="Exited"/>. Dispose terminates the job (the whole tree), then closes the pseudo console.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ConPtySession : IPtySession
{
    private static readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(5);
    private const int _readBufferBytes = 16 * 1024;
    private const int _outputCapacity = 256;

    private readonly nint _pseudoConsole;
    private readonly nint _inputWrite;
    private readonly nint _outputRead;
    private readonly nint _process;
    private readonly uint _processId;
    private readonly nint _job;
    private readonly nint _attributes;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_outputCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly Task<int> _waiter;
    private int _disposed;

    /// <summary>Takes ownership of every handle and starts the reader and the waiter.</summary>
    public ConPtySession(nint pseudoConsole, nint inputWrite, nint outputRead, nint process, uint processId, nint job, nint attributes)
    {
        _pseudoConsole = pseudoConsole;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _process = process;
        _processId = processId;
        _job = job;
        _attributes = attributes;
        _reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _waiter = Task.Factory.StartNew(WaitForShell, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = _reader.ContinueWith(_ => _output.Writer.TryComplete(), TaskScheduler.Default);
    }

    /// <inheritdoc />
    public ChannelReader<byte[]> Output => _output.Reader;

    /// <inheritdoc />
    public Task<int> Exited => _waiter;

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var copy = bytes.ToArray();
        return new ValueTask(Task.Run(() => WriteAll(copy), ct));
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Win32Native.ResizePseudoConsole(_pseudoConsole, ConPtyBackend.ToCoord(cols, rows));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Kill the tree first: the job holds the shell and everything it started.
        Win32Native.TerminateJobObject(_job, 1);
        Win32Native.CloseHandle(_job);
        Win32Native.CloseHandle(_inputWrite);

        // ClosePseudoConsole can wait for the output to be drained, so it runs off this thread with a bound.
        var close = Task.Run(() => Win32Native.ClosePseudoConsole(_pseudoConsole));
        await WaitQuietlyAsync(close, _closeTimeout).ConfigureAwait(false);

        await WaitQuietlyAsync(_waiter, _closeTimeout).ConfigureAwait(false);
        _stop.Cancel();
        await WaitQuietlyAsync(_reader, _closeTimeout).ConfigureAwait(false);

        Win32Native.CloseHandle(_outputRead);
        Win32Native.CloseHandle(_process);
        Win32Native.DeleteProcThreadAttributeList(_attributes);
        Marshal.FreeHGlobal(_attributes);
        _output.Writer.TryComplete();
        _stop.Dispose();
    }

    private int WaitForShell()
    {
        Win32Native.WaitForSingleObject(_process, Win32Native.Infinite);
        return ExitCode(_process);
    }

    private static unsafe int ExitCode(nint process)
    {
        uint code;
        if (Win32Native.GetExitCodeProcess(process, &code) == 0)
        {
            return -1;
        }

        return unchecked((int)code);
    }

    private static async Task WaitQuietlyAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Left running: the caller has already done what it can.
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[_readBufferBytes];
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // A broken pipe is the normal end: the pseudo console closed.
                var read = ReadChunk(_outputRead, buffer);
                if (read <= 0)
                {
                    return;
                }

                var chunk = buffer.AsSpan(0, read).ToArray();
                _output.Writer.WriteAsync(chunk, token).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped while waiting for the consumer.
        }
        catch (ChannelClosedException)
        {
            // Disposed while output was arriving.
        }
    }

    /// <summary>Returns the bytes read, or 0 when the read failed or the pipe is closed.</summary>
    private static unsafe int ReadChunk(nint handle, byte[] buffer)
    {
        uint read;
        int ok;
        fixed (byte* pointer = buffer)
        {
            ok = Win32Native.ReadFile(handle, pointer, (uint)buffer.Length, &read, nint.Zero);
        }

        return ok == 0 ? 0 : (int)read;
    }

    private void WriteAll(byte[] data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var written = WriteChunk(_inputWrite, data, offset);
            if (written < 0)
            {
                throw new IOException($"Writing to the console failed (Win32 error {Marshal.GetLastPInvokeError()}).");
            }

            offset += written;
        }
    }

    /// <summary>Returns the bytes written, or -1 on failure.</summary>
    private static unsafe int WriteChunk(nint handle, byte[] data, int offset)
    {
        uint written;
        int ok;
        fixed (byte* pointer = data)
        {
            ok = Win32Native.WriteFile(handle, pointer + offset, (uint)(data.Length - offset), &written, nint.Zero);
        }

        return ok == 0 ? -1 : (int)written;
    }
}
