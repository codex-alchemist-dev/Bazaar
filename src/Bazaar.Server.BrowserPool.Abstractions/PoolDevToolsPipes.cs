using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.BrowserPool.Abstractions;

/// <summary>
/// The two byte streams of a browser's <c>--remote-debugging-pipe</c>: what the host writes goes to the browser's descriptor 3, and
/// what the browser writes on its descriptor 4 is read from <see cref="FromBrowser"/>. Each message is UTF-8 JSON ended by one NUL
/// byte (see <see cref="DevToolsMessageWriter"/> and <see cref="DevToolsMessageReader"/>). The host owns both streams and disposes
/// them when the browser ends; the caller must not dispose them.
/// </summary>
/// <param name="ToBrowser">Write-only: commands to the browser.</param>
/// <param name="FromBrowser">Read-only: replies and events from the browser. Reaches end of stream when the browser has gone.</param>
public sealed record PoolDevToolsPipe(Stream ToBrowser, Stream FromBrowser);

/// <summary>
/// An optional capability of an <see cref="IPoolVmHost"/>: it can hand out the DevTools pipe of a session it started. A host that
/// cannot (or was not configured to) does not implement it; <see cref="IPoolVmHost"/> and <see cref="PoolProcess"/> are unchanged.
/// </summary>
public interface IPoolDevToolsPipes
{
    /// <summary>
    /// The pipe of a running session. False when the session is unknown, has ended, or was started without a pipe. The pipe is only
    /// valid until the session ends; after that the streams are disposed.
    /// </summary>
    bool TryGetPipe(string sessionId, [NotNullWhen(true)] out PoolDevToolsPipe? pipe);
}

/// <summary>A DevTools message was malformed or too large, or the pipe closed in the middle of one. The stream must be abandoned.</summary>
public sealed class DevToolsFramingException : IOException
{
    /// <summary>Creates the exception.</summary>
    public DevToolsFramingException(string message)
        : base(message)
    {
    }
}

/// <summary>Limits shared by the DevTools framing helpers.</summary>
public static class DevToolsFraming
{
    /// <summary>The default largest message in bytes (16 MiB), not counting the terminating NUL.</summary>
    public const int DefaultMaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>The upper bound a caller may configure (256 MiB).</summary>
    public const int HardMaxMessageBytes = 256 * 1024 * 1024;

    internal static int CheckMax(int maxMessageBytes)
    {
        if (maxMessageBytes is < 1 or > HardMaxMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes), "The message size limit must be 1 to " + HardMaxMessageBytes + " bytes.");
        }

        return maxMessageBytes;
    }
}

/// <summary>
/// Writes DevTools messages: the JSON bytes followed by one NUL. A message that contains a NUL or exceeds the limit is refused
/// before anything is written. Writes are serialised, so two callers never interleave their bytes in the pipe.
/// </summary>
public sealed class DevToolsMessageWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly int _max;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a writer over the stream. It does not take ownership of the stream.</summary>
    public DevToolsMessageWriter(Stream stream, int maxMessageBytes = DevToolsFraming.DefaultMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _max = DevToolsFraming.CheckMax(maxMessageBytes);
    }

    /// <summary>Writes one message and flushes.</summary>
    /// <exception cref="ArgumentException">The message is empty, contains a NUL byte, or is larger than the limit.</exception>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        if (json.IsEmpty)
        {
            throw new ArgumentException("A DevTools message is not empty.", nameof(json));
        }

        if (json.Length > _max)
        {
            throw new ArgumentException("The DevTools message is larger than the limit.", nameof(json));
        }

        if (json.Span.IndexOf((byte)0) >= 0)
        {
            throw new ArgumentException("A DevTools message may not contain a NUL byte.", nameof(json));
        }

        // The message and its NUL go out in one write, so a reader never sees the message without its terminator and a short
        // message is atomic in the pipe. The rented buffer is cleared on return because a message can carry typed text.
        var framed = ArrayPool<byte>.Shared.Rent(json.Length + 1);
        try
        {
            json.Span.CopyTo(framed);
            framed[json.Length] = 0;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(framed.AsMemory(0, json.Length + 1), ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(framed, clearArray: true);
        }
    }

    /// <summary>Releases the write lock object.</summary>
    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// Reads NUL-terminated DevTools messages. It never buffers more than the limit: a message that has not ended by then fails the
/// reader (<see cref="DevToolsFramingException"/>), and so does a stream that ends in the middle of a message. After a failure
/// every later read throws. A clean end of stream between messages returns null.
/// </summary>
public sealed class DevToolsMessageReader
{
    private readonly Stream _stream;
    private readonly int _max;
    private readonly byte[] _chunk = new byte[8192];
    private byte[] _message = new byte[1024];
    private int _length;
    private int _chunkStart;
    private int _chunkEnd;
    private DevToolsFramingException? _failure;

    /// <summary>Creates a reader over the stream. It does not take ownership of the stream.</summary>
    public DevToolsMessageReader(Stream stream, int maxMessageBytes = DevToolsFraming.DefaultMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _max = DevToolsFraming.CheckMax(maxMessageBytes);
    }

    /// <summary>Reads the next message (without its NUL), or null at a clean end of stream. Not safe for concurrent calls.</summary>
    /// <exception cref="DevToolsFramingException">The message is over the limit, or the stream ended inside a message.</exception>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct)
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        while (true)
        {
            if (_chunkStart == _chunkEnd)
            {
                _chunkStart = 0;
                _chunkEnd = await _stream.ReadAsync(_chunk, ct).ConfigureAwait(false);
                if (_chunkEnd == 0)
                {
                    if (_length == 0)
                    {
                        return null;
                    }

                    throw Fail("The DevTools pipe ended in the middle of a message.");
                }
            }

            var available = _chunk.AsSpan(_chunkStart, _chunkEnd - _chunkStart);
            var nul = available.IndexOf((byte)0);
            var take = nul < 0 ? available.Length : nul;
            if (_length + take > _max)
            {
                throw Fail("A DevTools message is larger than the limit.");
            }

            Append(available[..take]);
            if (nul < 0)
            {
                _chunkStart = _chunkEnd;
                continue;
            }

            _chunkStart += take + 1;
            var result = _message.AsSpan(0, _length).ToArray();
            _length = 0;
            if (_message.Length > 1024 * 1024)
            {
                // Do not keep a large buffer alive for one big message.
                _message = new byte[1024];
            }

            if (result.Length == 0)
            {
                // Two NULs in a row: an empty frame carries nothing, so skip it.
                continue;
            }

            return result;
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _message.Length)
        {
            Array.Resize(ref _message, (int)Math.Min(_max, Math.Max((long)_message.Length * 2, (long)_length + data.Length)));
        }

        data.CopyTo(_message.AsSpan(_length));
        _length += data.Length;
    }

    private DevToolsFramingException Fail(string message)
    {
        _failure = new DevToolsFramingException(message);
        _message = [];
        _length = 0;
        return _failure;
    }
}
