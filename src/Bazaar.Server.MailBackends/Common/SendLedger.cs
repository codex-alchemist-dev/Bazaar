using System.Collections.Concurrent;
using Bazaar.Server.Mail;

namespace Bazaar.Server.MailBackends.Common;

/// <summary>
/// Remembers receipts by idempotency key so a repeated key returns the first receipt without sending again.
/// In-memory only; the durable copy is mail_outbound (doc 22). Back ends take this seam so the store can replace it.
/// </summary>
public sealed class SendLedger
{
    private readonly ConcurrentDictionary<string, SendReceipt> _receipts = new(StringComparer.Ordinal);

    /// <summary>Returns the prior receipt for <paramref name="key"/>, or null when the key is new.</summary>
    public SendReceipt? Find(string key) =>
        _receipts.TryGetValue(key, out var receipt) ? receipt with { AlreadySent = true } : null;

    /// <summary>Records a successful send. The first record wins; a later duplicate keeps the original.</summary>
    public SendReceipt Record(SendReceipt receipt) => _receipts.GetOrAdd(receipt.IdempotencyKey, receipt);
}
