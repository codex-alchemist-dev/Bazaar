namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>The four trigger kinds in v1 (doc 38, Trigger types). The set is closed.</summary>
public enum RyuseiTriggerKind
{
    /// <summary>One shot at a fixed UTC instant.</summary>
    Timer,

    /// <summary>Recurring from an anchor. Missed runs coalesce into one fire.</summary>
    Interval,

    /// <summary>Repeats while a predicate holds, polled and debounced.</summary>
    Watcher,

    /// <summary>One shot, on the first event that matches the predicate.</summary>
    Event,
}

/// <summary>The spec stored in a trigger's <c>spec</c> column, one subtype per <see cref="RyuseiTriggerKind"/>.</summary>
public abstract record RyuseiTriggerSpec
{
    public abstract RyuseiTriggerKind Kind { get; }
}

/// <summary>A one-shot trigger. <see cref="FireAt"/> is a UTC instant.</summary>
public sealed record RyuseiTimerTriggerSpec(DateTimeOffset FireAt) : RyuseiTriggerSpec
{
    public override RyuseiTriggerKind Kind => RyuseiTriggerKind.Timer;
}

/// <summary>
/// A recurring trigger. Occurrences are <see cref="Anchor"/> plus whole multiples of <see cref="Every"/>. The anchor
/// itself is occurrence 0.
/// </summary>
public sealed record RyuseiIntervalTriggerSpec(TimeSpan Every, DateTimeOffset Anchor) : RyuseiTriggerSpec
{
    public override RyuseiTriggerKind Kind => RyuseiTriggerKind.Interval;
}

/// <summary>A repeating trigger that polls <see cref="Predicate"/> over the event stream.</summary>
public sealed record RyuseiWatcherTriggerSpec(
    RyuseiPredicate Predicate,
    int PollSeconds,
    int DebounceSeconds,
    DateTimeOffset? ExpiresAt,
    int? MaxFires) : RyuseiTriggerSpec
{
    public override RyuseiTriggerKind Kind => RyuseiTriggerKind.Watcher;
}

/// <summary>A one-shot subscription to <see cref="Predicate"/>. It reads the same predicate set as a watcher.</summary>
public sealed record RyuseiEventTriggerSpec(RyuseiPredicate Predicate) : RyuseiTriggerSpec
{
    public override RyuseiTriggerKind Kind => RyuseiTriggerKind.Event;
}
