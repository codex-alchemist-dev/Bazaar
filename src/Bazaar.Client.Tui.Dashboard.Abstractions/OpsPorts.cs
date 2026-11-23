using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard;

// The ports the resident TUI needs beyond the web admin ports of doc 51 (doc 58 section 3). Status, Kodo, users, devices, audit,
// approvals and modules and rebuilds are read through the existing Bazaar.Server.WebAdmin ports. Logs, events, the crash journal and
// the owner's local gate had no port, so they are defined here, small and read-only, for the owning modules to implement. Doc 51
// says a missing port is added to the port set once for both front ends; until the web admin adopts them these stay here.
// None of these records carries a secret.

public enum OpsLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>Filter for the log tail (doc 58 section 3: tail, filter by level and module).</summary>
public sealed record OpsLogFilter(OpsLogLevel MinLevel = OpsLogLevel.Information, string? Module = null, int Limit = 200);

/// <summary>One log line. The text is untrusted: the TUI strips control characters before drawing it.</summary>
public sealed record OpsLogLine(DateTimeOffset At, OpsLogLevel Level, string Module, string Message);

/// <summary>Reads recent server log lines, newest last.</summary>
public interface IOpsLogSource
{
    Task<IReadOnlyList<OpsLogLine>> TailAsync(OpsLogFilter filter, CancellationToken ct = default);
}

/// <summary>What a health event is. Everything except <see cref="Fault"/> and <see cref="Info"/> is health good news (doc 58 section 3a).</summary>
public enum OpsEventKind
{
    /// <summary>Something failed or degraded.</summary>
    Fault,

    /// <summary>Kodo fixed something by itself.</summary>
    SelfHeal,

    /// <summary>A Kodo recovery event.</summary>
    Recovery,

    /// <summary>A restart that worked.</summary>
    RestartWorked,

    /// <summary>The server survived a fatal crash.</summary>
    CrashSurvived,

    /// <summary>An A/B slot rollback restored service.</summary>
    SlotRollback,

    /// <summary>A backup or restore that verified.</summary>
    BackupVerified,

    /// <summary>A neutral note.</summary>
    Info,
}

/// <summary>
/// One health event. <see cref="Major"/> is set by the source for the bigger wins (a fatal crash survived) that get the green banner
/// (owner card 2026-11-18); the TUI does not guess which wins are big.
/// </summary>
public sealed record OpsEvent(string Id, DateTimeOffset At, OpsEventKind Kind, string Summary, bool Major = false)
{
    public bool IsWin => Kind is not (OpsEventKind.Fault or OpsEventKind.Info);

    /// <summary>True for the wins that count toward "N things fixed themselves today": the system repaired itself. A verified backup is a win but fixed nothing.</summary>
    public bool FixedItself => Kind is OpsEventKind.SelfHeal or OpsEventKind.Recovery or OpsEventKind.RestartWorked
        or OpsEventKind.CrashSurvived or OpsEventKind.SlotRollback;
}

/// <summary>Recent health events, newest first.</summary>
public interface IOpsEventSource
{
    Task<IReadOnlyList<OpsEvent>> ReadRecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>A crash of the TUI itself, kept so the next run can show it as a line on the overview (doc 58 section 3a).</summary>
public sealed record OpsCrashNote(DateTimeOffset At, string Reason);

/// <summary>
/// Persists crashes of the resident TUI. The restart delay is derived from these times, so a crash that kills the process still
/// counts on the next start.
/// </summary>
public interface IOpsCrashJournal
{
    Task RecordAsync(OpsCrashNote note, CancellationToken ct = default);

    /// <summary>The newest crashes, newest first.</summary>
    Task<IReadOnlyList<OpsCrashNote>> ReadRecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// The owner's local gate (doc 45 section 1.9, the same as <c>bazaar-admin</c>): on the box, the owner proves presence locally.
/// Returns the owner's identity, or null when the gate is closed (not on the box, or refused).
/// </summary>
public interface IOwnerLocalGate
{
    Task<AdminIdentity?> EnterAsync(CancellationToken ct = default);
}

/// <summary>The roles check for a code-redeemed admin (doc 58 section 5, "under the same roles check"). True when the identity may use Ops.</summary>
public interface IOpsAuthorizer
{
    Task<bool> MayUseOpsAsync(AdminIdentity identity, CancellationToken ct = default);
}
