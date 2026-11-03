using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Secrets;

/// <summary>
/// Replaces every resolved provider key with <see cref="Marker"/> in text that leaves the gateway: failure and warning
/// messages, log lines and exception text. A value is matched in its raw form and in the encoded forms an upstream is
/// likely to echo back (percent-encoded in either hex case, and JSON-escaped). Values stay tracked for the life of the
/// process; nothing is removed silently. Safe for concurrent use.
/// </summary>
public sealed class SecretScrubber
{
    /// <summary>The text that replaces a resolved secret wherever it appears.</summary>
    public const string Marker = "[redacted]";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    // Immutable snapshot of every needle, longest first. Readers take it without the lock.
    private volatile string[] _needles = [];

    /// <summary>How many distinct secret values are tracked.</summary>
    public int TrackedCount
    {
        get
        {
            lock (_gate)
            {
                return _secrets.Count;
            }
        }
    }

    /// <summary>Starts replacing <paramref name="secret"/> in every text scrubbed from now on. Tracking the same value twice does nothing.</summary>
    /// <remarks>The value is never included in an exception message.</remarks>
    public void Track(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        lock (_gate)
        {
            if (!_secrets.Add(secret))
            {
                return;
            }

            _needles = BuildNeedles(_secrets);
        }
    }

    /// <summary>Returns <paramref name="text"/> with every tracked value replaced by <see cref="Marker"/>. Returns the same instance when nothing matches.</summary>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var needles = _needles;
        if (needles.Length == 0 || !ContainsAny(text, needles))
        {
            return text;
        }

        // One left-to-right pass, longest needle first at each position. A replacement is never scanned again, so the
        // marker cannot be split or extended by a shorter needle.
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var matched = 0;
            var rest = text.AsSpan(index);
            foreach (var needle in needles)
            {
                if (rest.StartsWith(needle.AsSpan(), StringComparison.Ordinal))
                {
                    matched = needle.Length;
                    break;
                }
            }

            if (matched > 0)
            {
                builder.Append(Marker);
                index += matched;
            }
            else
            {
                builder.Append(text[index]);
                index++;
            }
        }

        return builder.ToString();
    }

    /// <summary>Scrubs the text a model event carries: a failure's code and message, a warning's code and message. Other events pass through unchanged.</summary>
    public ModelEvent ScrubEvent(ModelEvent e) => e switch
    {
        ModelEvent.Failure failure => failure with { Code = Scrub(failure.Code), Message = Scrub(failure.Message) },
        ModelEvent.Warning warning => warning with { Code = Scrub(warning.Code), Message = Scrub(warning.Message) },
        _ => e,
    };

    private static bool ContainsAny(string text, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] BuildNeedles(IEnumerable<string> secrets)
    {
        var needles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            needles.Add(secret);

            var upperEscaped = Uri.EscapeDataString(secret);
            needles.Add(upperEscaped);
            needles.Add(LowerPercentHex(upperEscaped));

            needles.Add(JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value);
            needles.Add(JsonEncodedText.Encode(secret).Value);
        }

        return needles
            .OrderByDescending(n => n.Length)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Lower-cases the hex digits of each <c>%XX</c> escape. Some encoders write them that way.</summary>
    private static string LowerPercentHex(string escaped)
    {
        var chars = escaped.ToCharArray();
        for (var i = 0; i + 2 < chars.Length; i++)
        {
            if (chars[i] == '%')
            {
                chars[i + 1] = char.ToLowerInvariant(chars[i + 1]);
                chars[i + 2] = char.ToLowerInvariant(chars[i + 2]);
                i += 2;
            }
        }

        return new string(chars);
    }
}
