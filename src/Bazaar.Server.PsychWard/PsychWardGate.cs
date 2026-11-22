using Bazaar.LoopGuard.Abstractions;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>
/// The real <see cref="IPsychWardGate"/> (doc 26, C34): any loop-guard trip admits every agent involved. An agent that is
/// already admitted keeps its one stay, and owner-only admission mode refuses the automatic admission (the trip is still
/// recorded by the loop-guard module).
/// </summary>
public sealed class PsychWardGate(IPsychWardService ward) : IPsychWardGate
{
    /// <inheritdoc />
    public async Task AdmitAsync(LoopTrip trip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trip);

        var detail = $"{trip.Kind}: {trip.Reason}";
        foreach (var agentId in trip.AgentIds.Distinct())
        {
            await ward.AdmitAsync(agentId, WardTrigger.LoopGuard, detail, cancellationToken).ConfigureAwait(false);
        }
    }
}
