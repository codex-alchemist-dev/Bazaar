using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Mail.Quarantine;
using MimeKit;

namespace Bazaar.Server.MailInbound;

/// <summary>Reads the SPF, DKIM and DMARC verdicts out of the headers the receiving provider recorded.</summary>
internal static class InboundAuthReader
{
    public static (MailAuthResults Results, string Summary) Read(IReadOnlyDictionary<string, string> headers)
    {
        headers.TryGetValue("Authentication-Results", out var authResults);
        var spf = Token(authResults, "spf") ?? FirstWord(headers.GetValueOrDefault("Received-SPF"));
        var dkim = Token(authResults, "dkim");
        var dmarc = Token(authResults, "dmarc");
        var summary = $"spf={spf ?? "none"} dkim={dkim ?? "none"} dmarc={dmarc ?? "none"}";
        return (new MailAuthResults(spf, dkim, dmarc), summary);
    }

    /// <summary>The value after the first <c>name=</c> that starts a method result, for example the <c>pass</c> in <c>spf=pass (note) smtp.mailfrom=x</c>.</summary>
    private static string? Token(string? header, string name)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        var search = name + "=";
        var from = 0;
        while (from < header.Length)
        {
            var at = header.IndexOf(search, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            // The method name must start a token, so "x-dkim=" or "header.dkim=" does not count.
            if (at == 0 || header[at - 1] is ' ' or ';' or '\t' or '\r' or '\n')
            {
                var start = at + search.Length;
                var end = start;
                while (end < header.Length && char.IsAsciiLetter(header[end]))
                {
                    end++;
                }

                return end > start ? header[start..end].ToLowerInvariant() : null;
            }

            from = at + search.Length;
        }

        return null;
    }

    private static string? FirstWord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var word = value.AsSpan().TrimStart();
        var end = 0;
        while (end < word.Length && char.IsAsciiLetter(word[end]))
        {
            end++;
        }

        return end == 0 ? null : word[..end].ToString().ToLowerInvariant();
    }
}

/// <summary>
/// Gives the screening service an attachment's decoded bytes by its index, decoding lazily and only the ones asked for. The index order
/// is the parser's: attachment parts in body-part order.
/// </summary>
internal sealed class MimeAttachmentBytes(ReadOnlyMemory<byte> raw)
{
    private List<MimePart>? _parts;
    private bool _failed;

    public ReadOnlyMemory<byte>? Read(int index)
    {
        if (_failed)
        {
            return null;
        }

        try
        {
            if (_parts is null)
            {
                using var stream = new MemoryStream(raw.ToArray(), writable: false);
                var message = MimeMessage.Load(stream);
                _parts = [.. message.BodyParts.OfType<MimePart>().Where(p => p.IsAttachment)];
            }

            if (index < 0 || index >= _parts.Count || _parts[index].Content is not { } content)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            content.DecodeTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An attachment that cannot be decoded is not inspected; the parser has already accepted the message, so it still faces every other screen.
            _failed = true;
            return null;
        }
    }
}

/// <summary>Identifies a delivery in results and logs without naming it: a short hash of the back end id.</summary>
internal static class InboundRef
{
    public static string Of(string backendId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(backendId)))[..12].ToLowerInvariant();
}
