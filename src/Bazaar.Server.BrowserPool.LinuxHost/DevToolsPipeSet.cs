using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>Makes and removes the FIFOs. A fake stands in for it in tests that must run off Linux.</summary>
internal interface IFifoFactory
{
    /// <summary>Creates the directory (mode 0700, not a symbolic link) and each FIFO (mode 0600). A stale file at a FIFO path is replaced.</summary>
    void Create(string directory, string commandsFifo, string repliesFifo);

    /// <summary>Opens a FIFO read-write. This never blocks, whether or not a peer exists. The handle is close-on-exec.</summary>
    SafeFileHandle OpenReadWrite(string path);

    /// <summary>Opens a FIFO read-only. This blocks until a writer exists, so call it only while a writer is open. The handle is close-on-exec.</summary>
    SafeFileHandle OpenRead(string path);

    /// <summary>Removes a FIFO. Never throws for a file that is already gone.</summary>
    void Delete(string path);
}

/// <summary><see cref="IFifoFactory"/> over <c>mkfifo(3)</c>.</summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LibcFifoFactory : IFifoFactory
{
    private const UnixFileMode _directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode _fifoMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public void Create(string directory, string commandsFifo, string repliesFifo)
    {
        Directory.CreateDirectory(directory, _directoryMode);
        var info = new DirectoryInfo(directory);
        if (info.LinkTarget is not null)
        {
            throw new PoolHostException("The pipe directory is a symbolic link.");
        }

        // The creation mode is cut by the umask; make it exactly 0700.
        File.SetUnixFileMode(directory, _directoryMode);
        foreach (var path in new[] { commandsFifo, repliesFifo })
        {
            Delete(path);
            if (MakeFifo(path, (uint)_fifoMode) != 0)
            {
                throw new PoolHostException("Could not create a DevTools pipe: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            File.SetUnixFileMode(path, _fifoMode);
        }
    }

    public SafeFileHandle OpenReadWrite(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    public SafeFileHandle OpenRead(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A FIFO that cannot be removed is reported by the next Create (it fails on the same path); cleanup must not throw.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [LibraryImport("libc", EntryPoint = "mkfifo", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MakeFifo(string path, uint mode);
}

/// <summary>
/// The host's ends of one session's DevTools pipe, and the FIFOs behind them. Opening never blocks:
/// <list type="bullet">
/// <item>Commands (browser descriptor 3): the host opens the FIFO read-write. A read-write open of a FIFO succeeds with no peer, and
/// the host is the only writer, so closing it ends the browser's input.</item>
/// <item>Replies (browser descriptor 4): the host first opens it read-write as a temporary writer, then opens the read end (which
/// then succeeds at once). The temporary writer stops the reader from seeing end of file before the browser has opened its end, and
/// it is closed when the first bytes arrive or the session ends; after that the reader sees end of file when the browser is gone.</item>
/// </list>
/// Every handle is close-on-exec (the .NET default), so the browser's process never inherits the host's ends.
/// </summary>
internal sealed class DevToolsPipeSet : IDisposable
{
    private readonly IFifoFactory _fifos;
    private readonly string _commandsFifo;
    private readonly string _repliesFifo;
    private readonly SafeFileHandle? _commandsHandle;
    private readonly SafeFileHandle? _replyReader;
    private SafeFileHandle? _replyKeepAlive;
    private int _disposed;

    private DevToolsPipeSet(IFifoFactory fifos, string commandsFifo, string repliesFifo, SafeFileHandle commands, SafeFileHandle keepAlive, SafeFileHandle reader)
    {
        _fifos = fifos;
        _commandsFifo = commandsFifo;
        _repliesFifo = repliesFifo;
        _commandsHandle = commands;
        _replyKeepAlive = keepAlive;
        _replyReader = reader;
        Pipe = new PoolDevToolsPipe(
            new FileStream(commands, FileAccess.Write, 1, false),
            new ReleasingReadStream(new FileStream(reader, FileAccess.Read, 1, false), ReleaseKeepAlive));
    }

    /// <summary>The streams handed to the caller.</summary>
    public PoolDevToolsPipe Pipe { get; }

    /// <summary>Creates the FIFOs and opens the host's ends. On any failure everything made so far is removed.</summary>
    public static DevToolsPipeSet Open(IFifoFactory fifos, string directory, string commandsFifo, string repliesFifo)
    {
        fifos.Create(directory, commandsFifo, repliesFifo);
        SafeFileHandle? commands = null;
        SafeFileHandle? keepAlive = null;
        SafeFileHandle? reader = null;
        try
        {
            commands = fifos.OpenReadWrite(commandsFifo);
            keepAlive = fifos.OpenReadWrite(repliesFifo);
            reader = fifos.OpenRead(repliesFifo);
            return new DevToolsPipeSet(fifos, commandsFifo, repliesFifo, commands, keepAlive, reader);
        }
        catch
        {
            reader?.Dispose();
            keepAlive?.Dispose();
            commands?.Dispose();
            fifos.Delete(commandsFifo);
            fifos.Delete(repliesFifo);
            throw;
        }
    }

    /// <summary>Closes the temporary writer on the replies FIFO. Safe to call more than once.</summary>
    public void ReleaseKeepAlive() => Interlocked.Exchange(ref _replyKeepAlive, null)?.Dispose();

    /// <summary>
    /// The browser has exited by itself: close the commands side, remove both FIFOs and let the replies stream reach end of file, but
    /// leave it readable so a reply the browser wrote just before it exited is not lost. The replies stream closes itself at end of
    /// file; a caller that never reads it leaves one handle for the finalizer.
    /// </summary>
    public void BrowserExited()
    {
        ReleaseKeepAlive();
        Pipe.ToBrowser.Dispose();
        _commandsHandle?.Dispose();
        _fifos.Delete(_commandsFifo);
        _fifos.Delete(_repliesFifo);
    }

    /// <summary>Ends the pipe at once: closes every handle and removes both FIFOs. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The temporary writer first: with the browser gone, a blocked reader then sees end of file.
        ReleaseKeepAlive();
        Pipe.ToBrowser.Dispose();
        Pipe.FromBrowser.Dispose();
        _commandsHandle?.Dispose();
        _replyReader?.Dispose();
        _fifos.Delete(_commandsFifo);
        _fifos.Delete(_repliesFifo);
    }

    /// <summary>A read-only view that tells the set once the browser has certainly opened its end (the first bytes arrived).</summary>
    private sealed class ReleasingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action _onFirstData;
        private int _seen;
        private bool _ended;

        public ReleasingReadStream(Stream inner, Action onFirstData)
        {
            _inner = inner;
            _onFirstData = onFirstData;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => _ended ? 0 : Seen(_inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _ended ? 0 : Seen(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Seen(int read)
        {
            if (read > 0 && Interlocked.Exchange(ref _seen, 1) == 0)
            {
                _onFirstData();
            }

            if (read == 0)
            {
                // End of file: nothing more will come, so release the handle now.
                _ended = true;
                _inner.Dispose();
            }

            return read;
        }
    }
}
