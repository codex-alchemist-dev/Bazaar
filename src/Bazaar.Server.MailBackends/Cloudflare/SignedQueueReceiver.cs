using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Mail;

namespace Bazaar.Server.MailBackends.Cloudflare;

/// <summary>One mail item as the Cloudflare Worker queues it. The Worker stores raw bytes; it never parses.</summary>
public sealed record QueuedMailItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("raw")] string RawBase64,
    [property: JsonPropertyName("received_at")] DateTimeOffset ReceivedAt);

/// <summary>Batch envelope from the queue endpoint.</summary>
public sealed record QueuedMailBatch([property: JsonPropertyName("items")] IReadOnlyList<QueuedMailItem> Items);

/// <summary>
/// Verifies and decodes a batch pulled from the Worker queue over outbound HTTPS (doc 22, C30: the host pulls).
/// The signature is HMAC-SHA256 over "{timestamp}.{body}". A batch outside the window or seen before is refused.
/// </summary>
public sealed class SignedQueueReceiver(byte[] sharedKey, TimeProvider clock, TimeSpan window)
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SignedQueueReceiver(byte[] sharedKey, TimeProvider clock)
        : this(sharedKey, clock, TimeSpan.FromMinutes(5))
    {
    }

    /// <summary>Verifies the headers and body, then returns the decoded raw mail. Throws on any failure.</summary>
    public IReadOnlyList<RawMail> Receive(string timestamp, string signature, ReadOnlySpan<byte> body, MailboxRef? onlyFor = null)
    {
        if (sharedKey.Length < 32) throw new InvalidOperationException("Shared key must be at least 32 bytes.");
        if (!long.TryParse(timestamp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var unix))
        {
            throw new UnauthorizedAccessException("Bad timestamp.");
        }

        // FromUnixTimeSeconds throws outside year 1 to 9999; a header outside that range is just a bad timestamp.
        if (unix is < -62135596800L or > 253402300799L) throw new UnauthorizedAccessException("Bad timestamp.");

        var sent = DateTimeOffset.FromUnixTimeSeconds(unix);
        var skew = (clock.GetUtcNow() - sent).Duration();
        if (skew > window) throw new UnauthorizedAccessException("Batch outside the accepted window.");

        if (signature is null) throw new UnauthorizedAccessException("Bad signature.");
        var expected = Sign(timestamp, body);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
        {
            throw new UnauthorizedAccessException("Bad signature.");
        }

        lock (_gate)
        {
            Prune(clock.GetUtcNow());
            if (!_seen.TryAdd(signature, sent)) throw new UnauthorizedAccessException("Replayed batch.");
        }

        QueuedMailBatch? batch;
        try
        {
            batch = JsonSerializer.Deserialize<QueuedMailBatch>(body);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The batch is not valid JSON.");
        }

        if (batch?.Items is null) throw new InvalidDataException("Empty batch.");
        var result = new List<RawMail>(batch.Items.Count);
        foreach (var item in batch.Items)
        {
            if (item?.Id is null || item.Mailbox is null || item.RawBase64 is null) throw new InvalidDataException("Incomplete item in batch.");

            // The last '@' splits the address, so a quoted local part that holds an '@' stays one mailbox.
            var at = item.Mailbox.LastIndexOf('@');
            if (at <= 0 || at == item.Mailbox.Length - 1) throw new InvalidDataException("Bad mailbox address in batch.");
            var box = new MailboxRef(item.Mailbox[(at + 1)..], item.Mailbox[..at]);

            // Domains are case-insensitive and mail systems treat local parts alike, so differently cased mail still arrives.
            if (onlyFor is { } want
                && !(string.Equals(want.Domain, box.Domain, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(want.LocalPart, box.LocalPart, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(item.RawBase64);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("Bad base64 in batch.");
            }

            result.Add(new RawMail($"cf:{item.Id}", box, raw, item.ReceivedAt));
        }

        return result;
    }

    /// <summary>Computes the expected signature for a timestamp and body. Lowercase hex.</summary>
    public string Sign(string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return Convert.ToHexString(HMACSHA256.HashData(sharedKey, message)).ToLowerInvariant();
    }

    // Keeps the replay set bounded to the acceptance window.
    private void Prune(DateTimeOffset now)
    {
        foreach (var (sig, sent) in _seen.Where(p => (now - p.Value).Duration() > window).ToList())
        {
            _seen.Remove(sig);
        }
    }
}
