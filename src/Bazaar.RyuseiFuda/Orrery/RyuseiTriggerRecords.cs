namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>How a fire is announced (doc 38, Ping, attach and new-GC semantics). Stored in <c>ryusei_trigger.ping_mode</c>.</summary>
public enum RyuseiPingMode
{
    None,
    Attach,
    NewGc,
}

/// <summary>Stored in <c>ryusei_trigger.state</c>. Only <see cref="Active"/> triggers are due.</summary>
public enum RyuseiTriggerState
{
    Active,
    Paused,
    Fired,
    Expired,
    Cancelled,
}

/// <summary>Values for one new <c>ryusei_trigger</c> row. <see cref="SpecJson"/> is the stored <c>spec</c> document.</summary>
public sealed record NewRyuseiTrigger(
    Guid TriggerId,
    Guid ProjectId,
    Guid CreatedBy,
    RyuseiTriggerKind Kind,
    IReadOnlyList<Guid> TargetAgentIds,
    string Purpose,
    string SpecJson,
    Guid? TaskId,
    Guid? AttachGcId,
    RyuseiPingMode PingMode,
    DateTimeOffset? NextFireAt,
    long? MaxFires,
    DateTimeOffset? ExpiresAt,
    int ChainDepth);

/// <summary>One <c>ryusei_trigger</c> row, every column in its stored form.</summary>
public sealed record RyuseiTriggerRecord(
    Guid TriggerId,
    Guid ProjectId,
    Guid CreatedBy,
    RyuseiTriggerKind Kind,
    Guid[] TargetAgentIds,
    string Purpose,
    string SpecJson,
    Guid? TaskId,
    Guid? AttachGcId,
    RyuseiPingMode PingMode,
    RyuseiTriggerState State,
    DateTimeOffset? NextFireAt,
    long FireCount,
    int ConsecutiveRefusals,
    long? MaxFires,
    DateTimeOffset? ExpiresAt,
    int ChainDepth,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Maps the trigger enums to the text the 0001 CHECK constraints accept. Unknown text is an error, never a default.</summary>
public static class RyuseiText
{
    public static string ToSql(RyuseiTriggerKind kind) => kind switch
    {
        RyuseiTriggerKind.Timer => "timer",
        RyuseiTriggerKind.Interval => "interval",
        RyuseiTriggerKind.Watcher => "watcher",
        RyuseiTriggerKind.Event => "event",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a trigger kind."),
    };

    public static RyuseiTriggerKind ParseKind(string text) => text switch
    {
        "timer" => RyuseiTriggerKind.Timer,
        "interval" => RyuseiTriggerKind.Interval,
        "watcher" => RyuseiTriggerKind.Watcher,
        "event" => RyuseiTriggerKind.Event,
        _ => throw new InvalidOperationException($"Unknown trigger kind '{text}'."),
    };

    public static string ToSql(RyuseiPingMode mode) => mode switch
    {
        RyuseiPingMode.None => "none",
        RyuseiPingMode.Attach => "attach",
        RyuseiPingMode.NewGc => "new_gc",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a ping mode."),
    };

    public static RyuseiPingMode ParsePingMode(string text) => text switch
    {
        "none" => RyuseiPingMode.None,
        "attach" => RyuseiPingMode.Attach,
        "new_gc" => RyuseiPingMode.NewGc,
        _ => throw new InvalidOperationException($"Unknown ping mode '{text}'."),
    };

    public static string ToSql(RyuseiTriggerState state) => state switch
    {
        RyuseiTriggerState.Active => "active",
        RyuseiTriggerState.Paused => "paused",
        RyuseiTriggerState.Fired => "fired",
        RyuseiTriggerState.Expired => "expired",
        RyuseiTriggerState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a trigger state."),
    };

    public static RyuseiTriggerState ParseState(string text) => text switch
    {
        "active" => RyuseiTriggerState.Active,
        "paused" => RyuseiTriggerState.Paused,
        "fired" => RyuseiTriggerState.Fired,
        "expired" => RyuseiTriggerState.Expired,
        "cancelled" => RyuseiTriggerState.Cancelled,
        _ => throw new InvalidOperationException($"Unknown trigger state '{text}'."),
    };
}
