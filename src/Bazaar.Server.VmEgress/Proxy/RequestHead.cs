using System.Text;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>Why a request head could not be read.</summary>
internal enum HeadError
{
    None,
    Malformed,
    TooLarge,
    TooManyHeaders,
}

/// <summary>A parsed request head. Strict on purpose: anything a second parser might read differently is refused.</summary>
internal sealed class RequestHead
{
    private static readonly byte[] _terminator = "\r\n\r\n"u8.ToArray();

    public string Method { get; private init; } = "";

    public string Target { get; private init; } = "";

    public List<KeyValuePair<string, string>> Headers { get; } = [];

    /// <summary>Position in the buffer just after the blank line. Bytes beyond it belong to the body or tunnel.</summary>
    public int HeadLength { get; private init; }

    /// <summary>Finds the end of the head in <paramref name="buffer"/>, or -1 while it is incomplete.</summary>
    public static int FindEnd(ReadOnlySpan<byte> buffer)
    {
        var at = buffer.IndexOf(_terminator);
        return at < 0 ? -1 : at + _terminator.Length;
    }

    /// <summary>Parses a complete head. <paramref name="headLength"/> is the value from <see cref="FindEnd"/>.</summary>
    public static HeadError TryParse(ReadOnlySpan<byte> buffer, int headLength, int maxHeaders, out RequestHead head)
    {
        head = null!;
        var raw = buffer[..(headLength - 4)];
        foreach (var b in raw)
        {
            // NUL and bare CR or LF are how request smuggling hides. Every line break must be CRLF.
            if (b == 0)
            {
                return HeadError.Malformed;
            }
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(raw);
        }
        catch (ArgumentException)
        {
            return HeadError.Malformed;
        }

        var lines = text.Split("\r\n");
        if (lines.Any(l => l.Contains('\r', StringComparison.Ordinal) || l.Contains('\n', StringComparison.Ordinal)))
        {
            return HeadError.Malformed;
        }

        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || !parts[0].All(IsTokenChar) || parts[1].Length == 0
            || (parts[2] != "HTTP/1.1" && parts[2] != "HTTP/1.0"))
        {
            return HeadError.Malformed;
        }

        if (lines.Length - 1 > maxHeaders)
        {
            return HeadError.TooManyHeaders;
        }

        var result = new RequestHead { Method = parts[0], Target = parts[1], HeadLength = headLength };
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !line.AsSpan(0, colon).ToString().All(IsTokenChar))
            {
                return HeadError.Malformed; // empty name, space before the colon, or obsolete line folding
            }

            result.Headers.Add(new(line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
        }

        head = result;
        return HeadError.None;
    }

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
        || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);
}
