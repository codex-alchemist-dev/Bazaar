using System.Globalization;
using System.Text;

namespace Bazaar.Rubedo.LanguageServer;

/// <summary>A framing problem. <see cref="Fatal"/> means the stream cannot be resynchronised and the loop should stop.</summary>
public sealed class LspFramingException(string message, bool fatal) : Exception(message)
{
    public bool Fatal { get; } = fatal;
}

/// <summary>
/// Reads Content-Length framed messages (<c>Header: value\r\n ... \r\n\r\n body</c>) from a stream. Handles messages split over
/// several reads and several messages in one read. A bad header block throws a non-fatal <see cref="LspFramingException"/> after
/// the block is discarded, so the next call starts at the following bytes.
/// </summary>
public sealed class FrameReader(Stream input)
{
    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    /// <summary>Returns the next message body, or null at the end of the stream.</summary>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        int terminator;
        while ((terminator = IndexOfTerminator()) < 0)
        {
            if (_end - _start > MaxHeaderBytes)
            {
                _start = _end = 0;
                throw new LspFramingException("Header block too long.", fatal: false);
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                _start = _end = 0;
                return null;
            }
        }

        string header = Encoding.ASCII.GetString(_buffer, _start, terminator - _start);
        _start = terminator + 4;
        int length = ParseContentLength(header);
        while (_end - _start < length)
        {
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                _start = _end = 0;
                return null;
            }
        }

        byte[] body = new byte[length];
        Buffer.BlockCopy(_buffer, _start, body, 0, length);
        _start += length;
        return body;
    }

    private static int ParseContentLength(string header)
    {
        int? length = null;
        foreach (string line in header.Split("\r\n"))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                {
                    throw new LspFramingException("Content-Length is not a number.", fatal: false);
                }

                length = parsed;
            }
        }

        if (length is null)
        {
            throw new LspFramingException("Missing Content-Length header.", fatal: false);
        }

        // An oversized length cannot be skipped safely, so the stream ends here.
        return length.Value > MaxBodyBytes ? throw new LspFramingException("Content-Length over the limit.", fatal: true) : length.Value;
    }

    private int IndexOfTerminator()
    {
        ReadOnlySpan<byte> window = _buffer.AsSpan(_start, _end - _start);
        int at = window.IndexOf("\r\n\r\n"u8);
        return at < 0 ? -1 : _start + at;
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0 && _start == _end)
        {
            _start = _end = 0;
        }

        if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }
        }

        int read = await input.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        _end += read;
        return read > 0;
    }
}
