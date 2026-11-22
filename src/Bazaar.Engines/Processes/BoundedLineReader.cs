using System.Text;

namespace Bazaar.Engines.Processes;

/// <summary>One line read by <see cref="BoundedLineReader"/>. <see cref="Text"/> is null at end of stream.</summary>
/// <param name="Text">The line without its terminator, cut at the limit when <paramref name="Truncated"/> is true.</param>
/// <param name="Truncated">True when the line was longer than the limit.</param>
internal readonly record struct BoundedLine(string? Text, bool Truncated);

/// <summary>
/// Reads newline-terminated lines with a hard length cap, so a child that never sends a newline cannot grow the buffer
/// without bound (<see cref="TextReader.ReadLineAsync()"/> has no limit).
/// </summary>
/// <param name="reader">The source.</param>
/// <param name="maxChars">The longest line kept, in chars.</param>
/// <param name="stopOnOverflow">When true, returns as soon as the cap is passed. When false, the rest of the line is read and dropped.</param>
internal sealed class BoundedLineReader(TextReader reader, int maxChars, bool stopOnOverflow)
{
    private readonly char[] _buffer = new char[4096];
    private readonly StringBuilder _line = new();
    private int _position;
    private int _length;

    public async Task<BoundedLine> ReadLineAsync()
    {
        _line.Clear();
        bool truncated = false;
        bool any = false;
        while (true)
        {
            if (_position >= _length)
            {
                _length = await reader.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    return any ? new BoundedLine(Finish(), truncated) : new BoundedLine(null, false);
                }
            }

            any = true;
            while (_position < _length)
            {
                char c = _buffer[_position++];
                if (c == '\n')
                {
                    return new BoundedLine(Finish(), truncated);
                }

                if (_line.Length < maxChars)
                {
                    _line.Append(c);
                }
                else
                {
                    truncated = true;
                    if (stopOnOverflow)
                    {
                        return new BoundedLine(Finish(), true);
                    }
                }
            }
        }
    }

    private string Finish()
    {
        if (_line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
        }

        return _line.ToString();
    }
}
