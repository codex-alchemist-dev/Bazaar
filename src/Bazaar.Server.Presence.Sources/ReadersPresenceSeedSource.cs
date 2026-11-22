using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Sources;

/// <summary>
/// The restart seed (docs/design/23, "Restart"), built from the modules that own the truth rather than the presence table. An agent's
/// availability is its saved machine state when one exists, otherwise the schedule calculator's answer at the current time, otherwise
/// (only when it has an open turn, which implies it is working) on shift. An agent with neither availability nor an open turn gets no
/// seed: there is nothing to rebuild, and it shows the projector's default until a transition arrives. The projector marks a seeded
/// row <c>unknown</c> when no turn event arrives for ten minutes, so a stale open turn does not show as live. Overlays: THROTTLED
/// while a saved throttle has not expired, COVERED while a saved cover is set. Each reader is optional. Seeds are ordered by agent id.
/// </summary>
public sealed class ReadersPresenceSeedSource(
    TimeProvider time,
    IAvailabilityStateReader? states = null,
    IAgentScheduleReader? schedules = null,
    IOpenTurnReader? turns = null) : IPresenceSeedSource
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PresenceSeed>> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var saved = states is null ? [] : await states.ListAsync(ct).ConfigureAwait(false);
        var scheduled = schedules is null ? [] : await schedules.ListAsync(ct).ConfigureAwait(false);
        var open = turns is null ? [] : await turns.ListOpenAsync(ct).ConfigureAwait(false);

        var savedById = saved.GroupBy(s => s.AgentId).ToDictionary(g => g.Key, g => g.Last());
        var scheduleById = scheduled.GroupBy(s => s.AgentId).ToDictionary(g => g.Key, g => g.Last());
        // Several open turns for one agent (a crash can leave them): the latest start is the one it is on.
        var openById = open.GroupBy(t => t.AgentId).ToDictionary(g => g.Key, g => g.OrderBy(t => t.StartedAt).ThenBy(t => t.TurnId).Last());

        var seeds = new List<PresenceSeed>();
        foreach (var id in savedById.Keys.Union(scheduleById.Keys).Union(openById.Keys).Order())
        {
            openById.TryGetValue(id, out var turn);
            var openEvent = turn is null ? null : ToEvent(turn);
            if (savedById.TryGetValue(id, out var state))
            {
                var overlays = state.Snapshot.Overlays;
                var throttled = overlays.OfType<AvailabilityOverlay.Throttled>().Any(t => t.Until > now);
                var covered = overlays.OfType<AvailabilityOverlay.Covered>().Any();
                seeds.Add(new PresenceSeed(id, AvailabilityPresenceSource.Map(state.Snapshot.State), throttled, covered, openEvent));
            }
            else if (scheduleById.TryGetValue(id, out var inputs))
            {
                var window = ScheduleEvaluator.Evaluate(inputs.Schedule, inputs.Events, now, inputs.Options);
                seeds.Add(new PresenceSeed(id, Map(window.State), OpenTurn: openEvent));
            }
            else if (openEvent is not null)
            {
                seeds.Add(new PresenceSeed(id, PresenceAvailability.OnShift, OpenTurn: openEvent));
            }
        }

        return seeds;
    }

    private static PresenceAvailability Map(AvailabilityState state) => state switch
    {
        AvailabilityState.OnShift => PresenceAvailability.OnShift,
        AvailabilityState.Off => PresenceAvailability.Off,
        AvailabilityState.Vacation => PresenceAvailability.Vacation,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    // The turn's exact step is not stored in the seed's source, so an open turn shows as a model call, as a turn start does.
    private static ActivityEvent ToEvent(OpenTurn t)
    {
        var gc = TurnPresenceSource.MapGc(new TurnActivity(
            t.AgentId, t.StartedAt, TurnActivityKind.TurnStarted, t.TurnId, t.GcId, t.GcKind, t.GcProjectId, t.AgentProjectId));
        return ActivityEvent.TurnChanged(t.AgentId, t.StartedAt, PresenceActivityKind.ModelCall, PresenceToolClass.None, gc, t.GcId, t.TurnId);
    }
}
