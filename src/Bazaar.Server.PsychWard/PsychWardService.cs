using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>
/// The Psych Ward (docs/design/26-fun-systems.md, section 1). Writes no GC entries itself: the host reacts to the stays it
/// returns. No score is attached to a stay, and the check-in text grants nothing. Transitions run one at a time so two
/// triggers for the same agent open one stay.
/// </summary>
public sealed class PsychWardService(
    IWardStayStore stays,
    IVentStore vents,
    PsychWardSettings settings,
    TimeProvider? time = null) : IPsychWardService, IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PsychWardSettings _settings = Validated(settings);

    private static PsychWardSettings Validated(PsychWardSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return value;
    }

    /// <inheritdoc />
    public async Task<WardOutcome> AdmitAsync(Guid agentId, WardTrigger trigger, string? detail, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await stays.GetLatestAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (current is { IsAdmitted: true })
            {
                return WardOutcome.Refuse("The agent is already admitted.", current);
            }

            if (trigger is not WardTrigger.OwnerReferral && _settings.AdmissionMode == WardAdmissionMode.OwnerOnly)
            {
                return WardOutcome.Refuse("Admission is owner-only; the owner must admit this agent.", current);
            }

            var stay = new WardStay(Guid.CreateVersion7(), agentId, trigger, detail, _time.GetUtcNow());
            await stays.SaveAsync(stay, cancellationToken).ConfigureAwait(false);
            return WardOutcome.Accept(stay);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WardOutcome> CheckInAsync(Guid agentId, WardCheckIn checkIn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkIn);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await stays.GetLatestAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (current is not { IsAdmitted: true })
            {
                return WardOutcome.Refuse("There is no active stay to check in to.", current);
            }

            var updated = current with { CheckIn = checkIn };
            await stays.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return WardOutcome.Accept(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WardOutcome> DischargeAsync(Guid agentId, DischargeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await stays.GetLatestAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (current is not { IsAdmitted: true })
            {
                return WardOutcome.Refuse("There is no active stay to discharge.", current);
            }

            if (!request.IsOwner)
            {
                if (_settings.DischargeBy == WardDischargeBy.Owner)
                {
                    return WardOutcome.Refuse("Only the owner may discharge this agent.", current);
                }

                if (request.ReviewerId is null || request.ReviewerId == agentId || !request.ReviewerIsClean)
                {
                    return WardOutcome.Refuse("A reviewer must be another agent with no tie to the failure and no taint.", current);
                }
            }

            var now = _time.GetUtcNow();
            var earliest = current.AdmittedAt.AddMinutes(_settings.MinStayMinutes);
            if (now < earliest)
            {
                return WardOutcome.Refuse($"The minimum stay has not passed (until {earliest:O}).", current);
            }

            var updated = current with { DischargedAt = now, ReviewerId = request.IsOwner ? null : request.ReviewerId };
            await stays.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return WardOutcome.Accept(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WardRestrictions> GetRestrictionsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var current = await stays.GetLatestAsync(agentId, cancellationToken).ConfigureAwait(false);
        return current is { IsAdmitted: true } ? WardRestrictions.Admitted : WardRestrictions.None;
    }

    /// <inheritdoc />
    public async Task<WardOverview> GetOverviewAsync(CancellationToken cancellationToken) =>
        new(
            await stays.ListActiveAsync(cancellationToken).ConfigureAwait(false),
            await vents.ListByStatusAsync(VentStatus.Held, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
