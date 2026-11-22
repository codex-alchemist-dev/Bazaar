namespace Bazaar.Client.Transport.AndroidHttp3.Android.Head;

/// <summary>
/// Header rules at the Cronet boundary. Request headers are checked before they reach <c>addHeader</c> (Cronet throws on a
/// bad name and would pass a CR or LF through); response headers, which Cronet reports as an ordered list that may repeat a
/// name, are folded into the lower-case map the transport expects.
/// </summary>
internal static class CronetHeaderMapper
{
    /// <summary>Request headers Cronet sets itself or rejects; the head never forwards them.</summary>
    private static readonly HashSet<string> _forbidden = new(StringComparer.Ordinal)
    {
        "content-length", "host", "connection", "transfer-encoding", "te", "upgrade",
    };

    /// <summary>
    /// Returns the headers to add, lower-cased, dropping the ones Cronet owns. Throws <see cref="ArgumentException"/> on a
    /// pseudo-header, an empty or non-token name, or a value holding CR, LF or NUL.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ForRequest(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var result = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || name[0] == ':' || !IsToken(name))
            {
                throw new ArgumentException($"Invalid request header name '{name}'.", nameof(headers));
            }

            if (value is null || value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                throw new ArgumentException($"Invalid value for request header '{name}'.", nameof(headers));
            }

            var lower = name.ToLowerInvariant();
            if (!_forbidden.Contains(lower)) result.Add(new KeyValuePair<string, string>(lower, value));
        }

        return result;
    }

    /// <summary>Folds Cronet's ordered response header list into a lower-case map; repeated names join with ", " in order.</summary>
    public static IReadOnlyDictionary<string, string> ForResponse(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || name[0] == ':') continue;
            var lower = name.ToLowerInvariant();
            map[lower] = map.TryGetValue(lower, out var existing) ? existing + ", " + value : value;
        }

        return map;
    }

    private static bool IsToken(string name)
    {
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal))) return false;
        }

        return true;
    }
}
