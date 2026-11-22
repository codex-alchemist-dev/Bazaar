namespace Bazaar.Server.Godot.Abstractions;

/// <summary>The outcome of one command.</summary>
public enum GodotRunStatus
{
    /// <summary>Exit code 0 and no error diagnostics.</summary>
    Succeeded,

    /// <summary>A non-zero exit code, or error diagnostics despite exit code 0.</summary>
    Failed,

    /// <summary>The process was killed for exceeding its timeout.</summary>
    TimedOut,

    /// <summary>The binary is missing or the canary failed to fail; the command did not run.</summary>
    Unavailable,
}

/// <summary>The result of one command.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="ExitCode">The exit code, or null when the process did not run to completion.</param>
/// <param name="Diagnostics">Parsed errors and warnings, bounded in count.</param>
/// <param name="Reason">Why the run is not <see cref="GodotRunStatus.Succeeded"/>. Null on success.</param>
/// <param name="OutputTruncated">True when output exceeded the cap and was cut.</param>
public sealed record GodotRunResult(
    GodotRunStatus Status,
    int? ExitCode,
    IReadOnlyList<GodotDiagnostic> Diagnostics,
    string? Reason,
    bool OutputTruncated);

/// <summary>Whether the adapter can be trusted to run commands: the binary starts and the startup canary fails as it must.</summary>
/// <param name="Available">True when commands may run.</param>
/// <param name="Reason">Why not, when <paramref name="Available"/> is false.</param>
public sealed record GodotAvailability(bool Available, string? Reason);
