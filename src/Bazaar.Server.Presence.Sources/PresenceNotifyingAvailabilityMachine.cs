using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Sources;

/// <summary>
/// Wraps one agent's <see cref="AvailabilityMachine"/> so each applied submit is reported to presence (doc 09, doc 23). The
/// work-life module cannot reference the presence ports (they reference it), and no shift scheduler exists yet to hold the call, so
/// whoever drives a machine submits through this wrapper instead. The sink is optional: with none, it is a plain submit. A sink
/// failure never fails the submit; it goes to <paramref name="onSinkFailure"/> (the host logs it at debug) and the caller carries on.
/// </summary>
public sealed class PresenceNotifyingAvailabilityMachine(
    AvailabilityMachine machine,
    Guid agentId,
    IAvailabilityTransitionSink? sink = null,
    TimeProvider? time = null,
    Action<Exception>? onSinkFailure = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The wrapped machine.</summary>
    public AvailabilityMachine Machine { get; } = machine ?? throw new ArgumentNullException(nameof(machine));

    /// <summary>Submits the event. An applied transition is reported to the sink with the current time.</summary>
    public async ValueTask<AvailabilityTransition> SubmitAsync(AvailabilityEvent evt, CancellationToken ct = default)
    {
        var transition = Machine.Submit(evt);
        if (transition.Applied && sink is not null)
        {
            try
            {
                await sink.OnTransitionAsync(agentId, transition, _time.GetUtcNow(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                onSinkFailure?.Invoke(ex);
            }
        }

        return transition;
    }
}
