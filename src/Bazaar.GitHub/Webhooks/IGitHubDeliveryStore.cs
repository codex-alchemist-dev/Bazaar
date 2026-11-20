namespace Bazaar.GitHub.Webhooks;

/// <summary>Durable record of GitHub deliveries (the <c>github_delivery</c> table, doc 21). Webhooks and the poller share it.</summary>
public interface IGitHubDeliveryStore
{
    /// <summary>
    /// Records one delivery. Returns true when it is new and false when <c>(source, deliveryId)</c> was already recorded,
    /// so a replay is acknowledged without being queued again.
    /// </summary>
    Task<bool> TryRecordAsync(string source, string deliveryId, string eventName, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}
