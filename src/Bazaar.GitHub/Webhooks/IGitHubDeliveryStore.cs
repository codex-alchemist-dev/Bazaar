namespace Bazaar.GitHub.Webhooks;

/// <summary>Durable record of GitHub deliveries (the <c>github_delivery</c> table, doc 21). Webhooks and the poller share it.</summary>
public interface IGitHubDeliveryStore
{
    /// <summary>
    /// Records one delivery. Returns true when it is new and false when <c>(source, deliveryId)</c> was already recorded,
    /// so a replay is acknowledged without being queued again.
    /// </summary>
    Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Records one delivery with its raw JSON body and event type (doc 21, Data and interfaces), for the PR service and
    /// the event mapper. Dedupe is unchanged: a replay returns false and keeps the first stored payload. The default
    /// implementation drops the payload and calls the original overload, so existing stores keep working.
    /// </summary>
    /// <param name="payloadJson">The raw body when it is valid JSON, otherwise null.</param>
    Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, string? payloadJson, CancellationToken cancellationToken) =>
        TryRecordAsync(source, deliveryId, eventName, receivedAt, cancellationToken);
}
