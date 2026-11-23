namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>How a confined process is launched.</summary>
internal enum WindowsConfinementMode
{
    /// <summary>An AppContainer with no capabilities (no network, no file access outside the folders granted to its SID).</summary>
    AppContainer,

    /// <summary>A restricted primary token (<c>DISABLE_MAX_PRIVILEGE</c>). Weaker: it does not deny file or network access. Used only when AppContainer is unavailable, and never makes the provider tested.</summary>
    RestrictedToken,
}

/// <summary>Limits for one job object.</summary>
/// <param name="ProcessMemoryBytes">Per-process committed memory limit.</param>
/// <param name="JobMemoryBytes">Whole-job committed memory limit.</param>
/// <param name="CpuTime">Job-wide user-mode CPU time; the job's processes are terminated when it is used up.</param>
/// <param name="ActiveProcessLimit">Most live processes in the job.</param>
internal sealed record WindowsJobSpec(long ProcessMemoryBytes, long JobMemoryBytes, TimeSpan CpuTime, int ActiveProcessLimit);

/// <summary>The limits a job object reports when queried (what Windows actually holds, not what was asked for).</summary>
internal sealed record WindowsJobLimits(
    bool KillOnJobClose,
    bool BreakawayAllowed,
    bool DieOnUnhandledException,
    int ActiveProcessLimit,
    long ProcessMemoryBytes,
    long JobMemoryBytes,
    TimeSpan CpuTime,
    uint UiRestrictions);

/// <summary>An AppContainer profile created for one run.</summary>
/// <param name="Name">Profile name (pass to <see cref="IWindowsConfinementApi.DeleteAppContainerProfile"/>).</param>
/// <param name="Sid">The AppContainer SID in string form.</param>
internal sealed record WindowsAppContainerProfile(string Name, string Sid);

/// <summary>Everything needed to start one suspended process.</summary>
/// <param name="ApplicationPath">Absolute path of the executable.</param>
/// <param name="CommandLine">The full command line, already quoted.</param>
/// <param name="WorkingDirectory">The per-run work directory.</param>
/// <param name="EnvironmentBlock">The complete environment block (see <see cref="WindowsEnvironmentBlock"/>).</param>
/// <param name="Mode">AppContainer or restricted token.</param>
/// <param name="AppContainerSid">The profile SID in AppContainer mode, otherwise null.</param>
internal sealed record WindowsProcessRequest(
    string ApplicationPath,
    string CommandLine,
    string WorkingDirectory,
    string EnvironmentBlock,
    WindowsConfinementMode Mode,
    string? AppContainerSid);

/// <summary>A process started suspended. Standard input is closed (it is the NUL device); output and error are pipes.</summary>
internal interface IWindowsProcess : IDisposable
{
    /// <summary>The process id.</summary>
    int Id { get; }

    /// <summary>Standard output.</summary>
    Stream StandardOutput { get; }

    /// <summary>Standard error.</summary>
    Stream StandardError { get; }

    /// <summary>True once it has exited.</summary>
    bool HasExited { get; }

    /// <summary>Exit code; valid only after <see cref="HasExited"/>.</summary>
    int ExitCode { get; }

    /// <summary>Lets the main thread run. Only ever called after the process was assigned to its job.</summary>
    void Resume();

    /// <summary>Terminates this one process (used when the job cannot be relied on, for example a failure before assignment).</summary>
    void Terminate(uint exitCode);

    /// <summary>Waits up to <paramref name="timeout"/> for exit. Returns true when it exited.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>A job object. Closing it kills every process in it.</summary>
internal interface IWindowsJob : IDisposable
{
    /// <summary>Reads the limits back from Windows.</summary>
    WindowsJobLimits QueryLimits();

    /// <summary>Ids of the processes currently in the job.</summary>
    IReadOnlyList<int> ProcessIds();

    /// <summary>Puts a (suspended) process into the job.</summary>
    void Assign(IWindowsProcess process);

    /// <summary>Terminates every process in the job.</summary>
    void Terminate(uint exitCode);
}

/// <summary>
/// The seam over every Win32 call the provider makes (job object, AppContainer profile, ACL, process creation). The real
/// implementation is <c>Win32ConfinementApi</c>; tests use a fake so that the order of calls and the cleanup paths run on any host.
/// </summary>
internal interface IWindowsConfinementApi
{
    /// <summary>True when this implementation runs on Windows. A false answer makes the provider untested.</summary>
    bool IsWindows { get; }

    /// <summary>True when AppContainer profiles can be created on this host (Windows 8 or later).</summary>
    bool IsAppContainerSupported();

    /// <summary>True when a file exists (used for the executable and the self-test tools).</summary>
    bool FileExists(string path);

    /// <summary>Creates an unnamed job object with kill-on-close, the active-process, memory and CPU-time limits, and the UI restrictions.</summary>
    IWindowsJob CreateJob(WindowsJobSpec spec);

    /// <summary>Creates an AppContainer profile with no capabilities.</summary>
    WindowsAppContainerProfile CreateAppContainerProfile(string name);

    /// <summary>Deletes the profile. Must tolerate a profile that is already gone.</summary>
    void DeleteAppContainerProfile(string name);

    /// <summary>
    /// Replaces the directory's access list with a protected one: the current user, SYSTEM, and (when given) the AppContainer SID with
    /// modify access, inherited by everything created inside.
    /// </summary>
    void GrantWorkDirectoryAccess(string directory, string? appContainerSid);

    /// <summary>Starts the process suspended, with standard input closed and output and error piped.</summary>
    IWindowsProcess CreateProcessSuspended(WindowsProcessRequest request);
}
