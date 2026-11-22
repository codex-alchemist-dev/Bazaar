using System.Runtime.CompilerServices;
using System.Text;

namespace Bazaar.Server.Gateway.Adapters.Common;

/// <summary>One server-sent event: the optional <c>event:</c> name and the joined <c>data:</c> lines.</summary>
public readonly record struct SseMessage(string? Event, string Data);

/// <summary>Minimal SSE parser shared by the adapters. Comment lines and unknown fields are ignored.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseMessage> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        var hasData = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseMessage(eventName, data.ToString());
                }

                eventName = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (field == "event")
            {
                eventName = value;
            }
            else if (field == "data")
            {
                if (hasData)
                {
                    data.Append('\n');
                }

                data.Append(value);
                hasData = true;
            }
        }

        if (hasData)
        {
            yield return new SseMessage(eventName, data.ToString());
        }
    }
}
