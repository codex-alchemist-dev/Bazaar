namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// Default settings and hard floors from doc 38 (Loop and runaway safeguards, Settings). The guards in
/// <see cref="RyuseiGuards"/> take the configured value as a parameter; these are the shipped defaults.
/// </summary>
public static class RyuseiDefaults
{
    /// <summary>Hard floor on an interval's <c>every</c>. Settings may raise it, never lower it.</summary>
    public static readonly TimeSpan IntervalFloor = TimeSpan.FromMinutes(1);

    /// <summary>Default <c>ryusei.min_interval</c>.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>Default <c>ryusei.max_triggers_per_agent</c>.</summary>
    public const int MaxTriggersPerAgent = 50;

    /// <summary>Default <c>ryusei.max_fires_per_day</c>.</summary>
    public const int MaxFiresPerDay = 96;

    /// <summary>Default <c>ryusei.max_chain_depth</c>. Valid range is 1 to 3.</summary>
    public const int MaxChainDepth = 3;

    /// <summary>Default <c>ryusei.cooldown_seconds</c>. Floor is 10 seconds.</summary>
    public const int CooldownSeconds = 60;

    /// <summary>Floor for <c>ryusei.cooldown_seconds</c>.</summary>
    public const int CooldownFloorSeconds = 10;

    /// <summary>Default <c>ryusei.auto_pause_after</c>. Valid range is 1 to 20.</summary>
    public const int AutoPauseAfter = 3;

    /// <summary>Default and maximum <c>ryusei.max_ping_fanout</c>. Valid range is 1 to 8.</summary>
    public const int MaxPingFanout = 8;

    /// <summary>Default <c>runtime.guard.wakes_per_hour</c>, the wake cap per agent shared with doc 06. Floor none.</summary>
    public const int MaxAgentWakesPerHour = 30;
}
