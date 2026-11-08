namespace Bazaar.Server.Components.Erasure;

/// <summary>
/// Runs one person's erasure: key destruction first, then each participant in registration order. A failing step is recorded
/// in the <see cref="ErasureReport"/> and does not stop the others or hide the key result. A repeat run for the same person
/// reports <see cref="PersonKeyOutcome.AlreadyDestroyed"/> and does not throw.
/// </summary>
/// <remarks>
/// The steps are mechanical. Whether key destruction plus tombstones is legally sufficient erasure is OPEN and needs counsel
/// (docs/design/32, section 9); this class does not decide it.
/// </remarks>
public sealed class ErasureCoordinator
{
    private readonly IPersonKeyEraser _keys;
    private readonly List<IErasureParticipant> _participants;

    /// <param name="keys">The built-in key eraser. Always runs first.</param>
    /// <param name="participants">Participants in the order they run. Names must be unique.</param>
    public ErasureCoordinator(IPersonKeyEraser keys, IEnumerable<IErasureParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(participants);

        _keys = keys;
        _participants = participants.ToList();

        var duplicate = _participants.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Erasure participant name '{duplicate.Key}' is registered more than once.", nameof(participants));
    }

    public async Task<ErasureReport> EraseAsync(Guid personId, CancellationToken ct = default)
    {
        if (personId == Guid.Empty)
            throw new ArgumentException("A person id is required.", nameof(personId));

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
