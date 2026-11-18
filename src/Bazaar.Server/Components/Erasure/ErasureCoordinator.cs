using Bazaar.Server.Erasure.Abstractions;

namespace Bazaar.Server.Components.Erasure;

/// <summary>
/// Runs one person's erasure: key destruction first, then each participant in registration order. A failing step is recorded
/// in the <see cref="ErasureReport"/> and does not stop the others or hide the key result. A repeat run for the same person
/// reports <see cref="PersonKeyOutcome.AlreadyDestroyed"/> and does not throw. A person under an active legal hold is not
/// touched: no key is destroyed and no participant runs; the report is <see cref="PersonKeyOutcome.HeldQueued"/>. The hold
/// check runs first, and if it throws the exception propagates, so nothing is destroyed (fail closed).
/// </summary>
/// <remarks>
/// The steps are mechanical. Whether key destruction plus tombstones is legally sufficient erasure is OPEN and needs counsel
/// (docs/design/32, section 9); this class does not decide it.
/// </remarks>
public sealed class ErasureCoordinator
{
    private readonly IPersonKeyEraser _keys;
    private readonly ILegalHoldReader _holds;
    private readonly List<IErasureParticipant> _participants;

    /// <param name="keys">The built-in key eraser. Always runs first.</param>
    /// <param name="holds">Legal hold reader, consulted before anything is destroyed.</param>
    /// <param name="participants">Participants in the order they run. Names must be unique.</param>
    public ErasureCoordinator(IPersonKeyEraser keys, ILegalHoldReader holds, IEnumerable<IErasureParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(participants);

        _keys = keys;
        _holds = holds;
        _participants = participants.ToList();

        var duplicate = _participants.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Erasure participant name '{duplicate.Key}' is registered more than once.", nameof(participants));
    }

    public async Task<ErasureReport> EraseAsync(Guid personId, CancellationToken ct = default)
    {
        if (personId == Guid.Empty)
            throw new ArgumentException("A person id is required.", nameof(personId));

        // Legal hold blocks removal (doc 48 section 9). Checked before any destruction; the request is queued, not refused.
        if (await _holds.IsPersonHeldAsync(personId, ct).ConfigureAwait(false))
            return new ErasureReport(personId, PersonKeyOutcome.HeldQueued, null, []);

        var (keyOutcome, keyError) = await DestroyKeyAsync(personId, ct).ConfigureAwait(false);

        // Participants run even when key destruction failed: their data is separate, and the report records both results.
        var outcomes = new List<ParticipantOutcome>(_participants.Count);
        foreach (var participant in _participants)
        {
            ErasureParticipantResult result;
            try
            {
                result = await participant.EraseAsync(personId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = ErasureParticipantResult.Failed(ex.GetType().Name);
            }

            outcomes.Add(new ParticipantOutcome(participant.Name, result));
        }

        // TODO(component): record erasure.completed through PrivacyAuditRecorder once the component model (PR #125) merges.
        return new ErasureReport(personId, keyOutcome, keyError, outcomes);
    }

    private async Task<(PersonKeyOutcome Outcome, string? Error)> DestroyKeyAsync(Guid personId, CancellationToken ct)
    {
        try
        {
            var result = await _keys.EraseAsync(personId, ct).ConfigureAwait(false);
            if (result.Destroyed > 0)
                return (PersonKeyOutcome.Destroyed, null);
            if (result.AlreadyDestroyed > 0)
                return (PersonKeyOutcome.AlreadyDestroyed, null);
            return (PersonKeyOutcome.NoKeyOnFile, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (PersonKeyOutcome.Failed, ex.GetType().Name);
        }
    }
}
