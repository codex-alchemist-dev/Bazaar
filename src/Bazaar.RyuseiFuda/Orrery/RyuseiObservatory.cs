using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>One RyuseiFuda Observatory event (doc 38, Observability; doc 23). Fields carry ids, states, outcomes and hashes only.</summary>
/// <param name="Name">The event name, for example <c>ryusei.fire</c>.</param>
/// <param name="At">When the change was decided (UTC).</param>
/// <param name="Fields">The payload. It never carries a trigger's purpose text, spec or raw arguments.</param>
public sealed record RyuseiObservatoryEvent(string Name, DateTimeOffset At, JsonObject Fields);

/// <summary>
/// The hand-off from RyuseiFuda to the Observatory (doc 23). The Observatory projector implements this seam when it lands. Until
/// then the server registers <see cref="NullRyuseiObservatory"/>, so nothing is emitted anywhere.
/// </summary>
public interface IRyuseiObservatory
{
    /// <summary>Emits one event. Called only after the change that the event describes has committed.</summary>
    ValueTask EmitAsync(RyuseiObservatoryEvent evt, CancellationToken ct = default);
}

/// <summary>An Observatory sink that drops every event. It is the default until the Observatory projector is installed.</summary>
public sealed class NullRyuseiObservatory : IRyuseiObservatory
{
    public static NullRyuseiObservatory Instance { get; } = new();

    public ValueTask EmitAsync(RyuseiObservatoryEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;
}

/// <summary>
/// The RyuseiFuda Observatory events and the payloads they share with the audit rows (doc 38, Observability; doc 23). Pure: it
/// reads no clock and no database. Every trigger create, pause, resume, cancel, fire, skip and guard trip maps to one event here.
/// The state-change and create payloads are also the audit payloads, so the event and its audit row cannot disagree.
/// </summary>
public static class RyuseiObservatoryEvents
{
    /// <summary>A trigger was created (doc 23 <c>ryusei.trigger_created</c>).</summary>
    public const string TriggerCreated = "ryusei.trigger_created";

    /// <summary>A trigger was paused (doc 23 <c>ryusei.trigger_paused</c>).</summary>
    public const string TriggerPaused = "ryusei.trigger_paused";

    /// <summary>
    /// A trigger was resumed. Doc 23 lists no resume event; this name follows its pattern and is the safe default (see the PR
    /// notes).
    /// </summary>
    public const string TriggerResumed = "ryusei.trigger_resumed";

    /// <summary>A trigger was cancelled (doc 23 <c>ryusei.trigger_cancelled</c>).</summary>
    public const string TriggerCancelled = "ryusei.trigger_cancelled";

    /// <summary>A fire was decided: woke, refused, skipped or error. The outcome field says which (doc 23 <c>ryusei.fire</c>).</summary>
    public const string Fire = "ryusei.fire";

    /// <summary>A guard tripped and paused the trigger (doc 23 <c>ryusei.auto_paused</c>).</summary>
    public const string AutoPaused = "ryusei.auto_paused";

    /// <summary>Emits events in order to <paramref name="sink"/>. Call it only after the transaction that wrote them has committed.</summary>
    public static async ValueTask EmitAsync(IRyuseiObservatory sink, IEnumerable<RyuseiObservatoryEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(events);
        foreach (var evt in events)
        {
            await sink.EmitAsync(evt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The lowercase hex SHA-256 of a text. Purpose text goes into payloads only as this hash.</summary>
    public static string Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>The fields of a trigger-created event, which are also its audit payload.</summary>
    public static JsonObject CreatedFields(
        Guid triggerId,
        Guid projectId,
        RyuseiTriggerKind kind,
        IReadOnlyList<Guid> targetAgentIds,
        RyuseiPingMode pingMode,
        int chainDepth,
        string purpose) => new()
        {
            ["triggerId"] = triggerId.ToString("D"),
            ["projectId"] = projectId.ToString("D"),
            ["kind"] = RyuseiText.ToSql(kind),
            ["targets"] = IdArray(targetAgentIds),
            ["pingMode"] = RyuseiText.ToSql(pingMode),
            ["chainDepth"] = chainDepth,
            ["purposeHash"] = Hash(purpose),
        };

    /// <summary>The trigger-created event for a committed create.</summary>
    public static RyuseiObservatoryEvent Created(JsonObject createdFields, DateTimeOffset at) =>
        new(TriggerCreated, at, (JsonObject)createdFields.DeepClone());

    /// <summary>The fields of a trigger state change, which are also its audit payload.</summary>
    public static JsonObject StateChangedFields(Guid triggerId, Guid projectId, RyuseiTriggerState from, RyuseiTriggerState to) => new()
    {
        ["triggerId"] = triggerId.ToString("D"),
        ["projectId"] = projectId.ToString("D"),
        ["fromState"] = RyuseiText.ToSql(from),
        ["toState"] = RyuseiText.ToSql(to),
    };

    /// <summary>The event name for a committed pause, resume or cancel.</summary>
    public static string NameForStateChange(RyuseiTriggerState to) => to switch
    {
        RyuseiTriggerState.Paused => TriggerPaused,
        RyuseiTriggerState.Cancelled => TriggerCancelled,
        _ => TriggerResumed,
    };

    /// <summary>
    /// The events for one decided fire: always one <c>ryusei.fire</c>, and one <c>ryusei.auto_paused</c> when the fire moved an
    /// active trigger to paused (a guard trip or a malformed trigger).
    /// </summary>
    public static IReadOnlyList<RyuseiObservatoryEvent> ForFire(
        Guid fireId,
        RyuseiTriggerRecord trigger,
        RyuseiFireDecision decision,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(decision);

        var fire = new JsonObject
        {
            ["fireId"] = fireId.ToString("D"),
            ["triggerId"] = trigger.TriggerId.ToString("D"),
            ["projectId"] = trigger.ProjectId.ToString("D"),
            ["outcome"] = RyuseiTickRules.ToSql(decision.Outcome),
            ["scheduledFor"] = decision.ScheduledFor.ToUniversalTime().ToString("O"),
            ["wokenCount"] = decision.WakeAgentIds.Count,
        };
        var events = new List<RyuseiObservatoryEvent> { new(Fire, at, fire) };

        if (decision.NextState == RyuseiTriggerState.Paused && trigger.State != RyuseiTriggerState.Paused)
        {
            events.Add(new RyuseiObservatoryEvent(AutoPaused, at, new JsonObject
            {
                ["triggerId"] = trigger.TriggerId.ToString("D"),
                ["projectId"] = trigger.ProjectId.ToString("D"),
                ["fireId"] = fireId.ToString("D"),
                ["outcome"] = RyuseiTickRules.ToSql(decision.Outcome),
                ["consecutiveRefusals"] = decision.ConsecutiveRefusals,
            }));
        }

        return events;
    }

    private static JsonArray IdArray(IReadOnlyList<Guid> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add((JsonNode)id.ToString("D"));
        }

        return array;
    }
}
