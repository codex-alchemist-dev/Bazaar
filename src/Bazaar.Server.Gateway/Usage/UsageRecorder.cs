namespace Bazaar.Server.Gateway.Usage;

/// <summary>Builds an accumulator's usage row and appends it to the store. Each accumulator records exactly once.</summary>
public sealed class UsageRecorder
{
    private readonly IUsageStore _store;

    public UsageRecorder(IUsageStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Builds and appends the row. A second call on the same accumulator throws. If the append fails, the claim is
    /// released so the caller may retry.
    /// </summary>
    public async ValueTask RecordAsync(UsageAccumulator accumulator, PriceTable? prices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accumulator);
        if (!accumulator.TryClaimRecording())
        {
            throw new InvalidOperationException("This usage accumulator has already been recorded.");
        }

        try
        {
            await _store.AppendAsync(accumulator.Build(prices), ct).ConfigureAwait(false);
        }
        catch
        {
            accumulator.ReleaseRecording();
            throw;
        }
    }
}
