using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// One shell on a Unix pty. A reader thread polls the master and feeds <see cref="Output"/>. A waiter thread reaps the
/// shell and completes <see cref="Exited"/>. Dispose closes the master (the kernel hangs up the session), sends SIGHUP to
/// everything left in the session, and after a grace period sends SIGKILL to whatever still runs in it.
/// </summary>
internal sealed class UnixPtySession : IPtySession
{
    private static readonly TimeSpan _drainAfterExit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _shutdownGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _reapTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _readerStopTimeout = TimeSpan.FromSeconds(2);
    private const int _readBufferBytes = 16 * 1024;
    private const int _pollIntervalMs = 50;
    private const int _outputCapacity = 256;

    private readonly int _master;
    private readonly int _pid;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_outputCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly Task<int> _waiter;
    private int _disposed;

    /// <summary>Takes ownership of the master fd and starts the reader and waiter for the shell <paramref name="pid"/>.</summary>
    public UnixPtySession(int master, int pid)
    {
        _master = master;
        _pid = pid;
        _reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = _reader.ContinueWith(_ => _output.Writer.TryComplete(), TaskScheduler.Default);
        _waiter = Task.Factory.StartNew(WaitForShell, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = Task.Run(async () =>
        {
            // When the shell has ended, give the reader a short time to drain, then stop it. A background job can hold
            // the slave open, and the output must still complete.
            await _waiter.ConfigureAwait(false);
            await Task.Delay(_drainAfterExit).ConfigureAwait(false);
            _stop.Cancel();
        });
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

        // A failed resize (the shell already gone) changes nothing the caller can act on.
        UnixPtyBackend.SetSize(_master, cols, rows);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        await WaitQuietlyAsync(_reader, _readerStopTimeout).ConfigureAwait(false);

        // Closing the master hangs up the slave: the kernel sends SIGHUP to the session's foreground group.
        UnixNative.close(_master);

        // Hang up everything in the session explicitly, in case a child ignores the hangup.
        foreach (var member in SessionMembers(_pid))
        {
            UnixNative.kill(member, UnixNative.SIGHUP);
        }

        UnixNative.kill(-_pid, UnixNative.SIGHUP);

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _shutdownGrace && (!_waiter.IsCompleted || SessionMembers(_pid).Count > 0))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        // Grace period over: kill what is left. SIGKILL cannot be ignored.
        foreach (var member in SessionMembers(_pid))
        {
            UnixNative.kill(member, UnixNative.SIGKILL);
        }

        if (!_waiter.IsCompleted)
        {
            UnixNative.kill(_pid, UnixNative.SIGKILL);
        }

        await WaitQuietlyAsync(_waiter, _reapTimeout).ConfigureAwait(false);
        _output.Writer.TryComplete();
        _stop.Dispose();
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

    /// <summary>Lists the live processes (not zombies) whose session id is <paramref name="sessionId"/>, from /proc.</summary>
    internal static IReadOnlyList<int> SessionMembers(int sessionId)
    {
        var members = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }

            try
            {
                // Fields after the parenthesised command name: state, ppid, pgrp, session.
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (fields[0] != "Z" && int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture) == sessionId)
                {
                    members.Add(pid);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException)
            {
                // The process ended while it was being read.
            }
        }

        return members;
    }

    private const int RetryCode = int.MinValue;

    private int WaitForShell()
    {
        while (true)
        {
            var code = WaitOnce(_pid);
            if (code != RetryCode)
            {
                return code;
            }
        }
    }

    private static unsafe int WaitOnce(int pid)
    {
        int status;
        var rc = UnixNative.waitpid(pid, &status, 0);
        if (rc == pid)
        {
            return DecodeStatus(status);
        }

        if (rc < 0 && Marshal.GetLastPInvokeError() == UnixNative.EINTR)
        {
            return RetryCode;
        }

        return -1;
    }

    private static int DecodeStatus(int status)
    {
        var signal = status & 0x7f;
        if (signal == 0)
        {
            return (status >> 8) & 0xff;
        }

        // Killed by a signal: report the shell convention 128 + signal.
        return signal == 0x7f ? -1 : 128 + signal;
    }

    private void ReadLoop()
    {
        var buffer = new byte[_readBufferBytes];
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var ready = Poll(_master);
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == UnixNative.EINTR)
                    {
                        continue;
                    }

                    return;
                }

                if (ready == 0)
                {
                    continue;
                }

                var count = ReadInto(_master, buffer);
                if (count > 0)
                {
                    var chunk = buffer.AsSpan(0, count).ToArray();
                    _output.Writer.WriteAsync(chunk, token).AsTask().GetAwaiter().GetResult();
                }
                else if (count == 0)
                {
                    return;
                }
                else
                {
                    // EIO means every holder of the slave has closed it: the normal end of output on Linux.
                    var error = Marshal.GetLastPInvokeError();
                    if (error == UnixNative.EINTR || error == UnixNative.EAGAIN)
                    {
                        continue;
                    }

                    return;
                }
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

    private static unsafe int Poll(int fd)
    {
        var pollFd = new PollFd { Fd = fd, Events = UnixNative.POLLIN };
        return UnixNative.poll(&pollFd, 1, _pollIntervalMs);
    }

    /// <summary>Reads into <paramref name="buffer"/>. Returns the count, 0 at end, or -1 with errno set.</summary>
    private static unsafe int ReadInto(int fd, byte[] buffer)
    {
        fixed (byte* pointer = buffer)
        {
            return (int)UnixNative.read(fd, pointer, (nuint)buffer.Length);
        }
    }

    private void WriteAll(byte[] data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var written = WriteSome(_master, data, offset);
            if (written < 0)
            {
                if (Marshal.GetLastPInvokeError() == UnixNative.EINTR)
                {
                    continue;
                }

                throw new IOException($"Writing to the terminal failed (errno {Marshal.GetLastPInvokeError()}).");
            }

            offset += written;
        }
    }

    private static unsafe int WriteSome(int fd, byte[] data, int offset)
    {
        fixed (byte* pointer = data)
        {
            return (int)UnixNative.write(fd, pointer + offset, (nuint)(data.Length - offset));
        }
    }
}
