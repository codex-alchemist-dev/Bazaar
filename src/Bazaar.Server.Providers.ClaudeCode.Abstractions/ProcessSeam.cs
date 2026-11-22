namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>One child process to start. Arguments go through an argument list, never a shell string.</summary>
public sealed record ClaudeCodeInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string StdinText,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>A running child. Disposing kills the whole process tree if it is still alive.</summary>
public interface IClaudeCodeRun : IAsyncDisposable
{
    /// <summary>Standard output lines as they arrive.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    /// <summary>The exit code once the process ended, else null.</summary>
    int? ExitCode { get; }

    /// <summary>The last bytes of standard error, already truncated. For diagnostics only.</summary>
    string StderrTail { get; }

    /// <summary>Kills the process and its children now.</summary>
    void KillTree();
}

/// <summary>
/// Optional OS-level guard for child processes (docs/design/91 section 6). On Windows a Job Object with kill-on-close makes a
/// crashed server take its children with it. A platform with no container simply registers none.
/// </summary>
public interface IChildProcessContainer
{
    /// <summary>Puts the just-started process, and everything it later starts, under the container's control.</summary>
    void Adopt(System.Diagnostics.Process process);
}

/// <summary>The seam the fake CLI replaces in tests.</summary>
public interface IClaudeCodeProcessRunner
{
    Task<IClaudeCodeRun> StartAsync(ClaudeCodeInvocation invocation, CancellationToken ct);
}

/// <summary>Lane health facts for Kodo and the admin panel. Facts only.</summary>
public sealed record ClaudeCodeStatus(
    bool Up,
    string? DownReason,
    DateTimeOffset? RateLimitedUntil,
    DateTimeOffset? LastSuccessAt,
    string? LastErrorClass,
    long IgnoredEvents);

public interface IClaudeCodeLaneState
{
    ClaudeCodeStatus Current { get; }
}
