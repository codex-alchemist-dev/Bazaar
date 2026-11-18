namespace Bazaar.Push;

/// <summary>
/// Turns a content-free wake into one fetch and, when the fetch found something, one local notification. The fetch is
/// idempotent because of the cursors, so a late, repeated or unknown wake is harmless and still runs the fetch.
/// </summary>
public sealed class BareWakeHandler : IBareWakeHandler
{
    private readonly IWakeFetcher _fetcher;
    private readonly ILocalNotifier _notifier;

    public BareWakeHandler(IWakeFetcher fetcher, ILocalNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(notifier);
        _fetcher = fetcher;
        _notifier = notifier;
    }

    public async Task<WakeOutcome> HandleAsync(string? rawKind, CancellationToken ct)
    {
        var kind = BareWakeParser.Parse(rawKind);
        try
        {
            var result = await _fetcher.FetchAsync(kind, ct).ConfigureAwait(false);
            if (result.NewEventCount <= 0)
            {
                return WakeOutcome.NothingNew;
            }

            await _notifier.ShowAsync(kind, ct).ConfigureAwait(false);
            return WakeOutcome.Notified;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed wake is not fatal: the cursor did not move, so the next wake or the next foreground start fetches again.
            return WakeOutcome.Failed;
        }
    }
}
