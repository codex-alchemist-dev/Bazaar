using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Sliding-window token quotas backed by an <see cref="ITokenBudgetStore"/>. On each call it prunes spends older than the
/// widest window (one week), then asks the store to check all windows and record the spend as one atomic operation. It
/// keeps no cache, so a new instance over the same store recovers every window. Time comes from the injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// Calls are also serialised in this process, which only keeps pruning orderly: the limit itself is enforced by the store's
/// atomic <see cref="ITokenBudgetStore.TryConsume"/>, which is safe across processes. Platform-neutral: no OS-specific APIs.
/// Each call prunes the store first, so a failure while pruning records nothing.
/// </remarks>
public sealed class TokenBudgetService : ITokenBudgetService, IDisposable
{
    private readonly TokenBudgetOptions _options;
    private readonly ITokenBudgetStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IWardTriggerIntake? _ward;

    /// <exception cref="ArgumentNullException"><paramref name="options"/>, <paramref name="store"/> or <paramref name="time"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> has a window limit below 1.</exception>
    public TokenBudgetService(TokenBudgetOptions options, ITokenBudgetStore store, TimeProvider time, IWardTriggerIntake? ward = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _ward = ward;
    }

    public async Task<TokenBudgetDecision> TryConsume(string agentId, long tokens, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        ct.ThrowIfCancellationRequested();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();

            // Prune first: if this call fails here, nothing has been recorded yet.
            await _store.PruneOlderThan(TokenBudgetEvaluator.WidestStart(now), ct).ConfigureAwait(false);

            // One atomic check-and-record in the store: every window is checked and the spend is written as a unit, so a
            // denial leaves no trace and no other process can slip a spend between the check and the write.
            var decision = await _store.TryConsume(agentId, now, tokens, _options, ct).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                // The denial stands first. Then the Psych Ward is told (doc 26: a token hard stop is a trigger); a failing report
                // never changes the decision.
                await ReportHardStopAsync(agentId, decision, ct).ConfigureAwait(false);
            }

            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReportHardStopAsync(string agentId, TokenBudgetDecision decision, CancellationToken ct)
    {
        if (_ward is null || !Guid.TryParse(agentId, out var id))
        {
            return;
        }

        try
        {
            await _ward.ReportTokenHardStopAsync(id, $"{decision.Window?.ToString() ?? "unknown"} window", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Reporting is best effort.
        }
    }

    /// <summary>Releases the call gate. The service must not be used afterwards.</summary>
    public void Dispose() => _gate.Dispose();
}
