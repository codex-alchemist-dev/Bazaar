using Bazaar.Contracts.Gateway;

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

    /// <summary>
    /// Appends one zero-token row for a call policy refused before any provider was contacted (docs/design/05-model-gateway.md,
    /// Policy). <paramref name="providerId"/> is the provider of the first dropped candidate or the requested model, else
    /// <see cref="UsageSource.PolicyProviderId"/>. The deny reason is the error code.
    /// </summary>
    public ValueTask RecordDeniedAsync(
        string agentId, string? turnId, string route, string? providerId, string errorCode, DateTimeOffset at, CancellationToken ct)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? UsageSource.PolicyProviderId : providerId;
        return _store.AppendAsync(
            new GatewayUsageRecord(
                Id: Guid.CreateVersion7(at),
                Timestamp: at,
                AgentId: agentId,
                TurnId: turnId,
                ProviderId: provider,
                KeyRef: "none",
                ModelId: route,
                ServedModel: null,
                Identity: GatewayIdentity.Native,
                Tokens: new GatewayTokenUsage(0, 0, 0, 0, 0, 0),
                LatencyMs: 0,
                Status: UsageStatus.Denied,
                ErrorCode: errorCode,
                RetryAfterSeconds: null,
                Estimated: false,
                PriceVersion: null,
                Source: UsageSource.For(provider)),
            ct);
    }
}
