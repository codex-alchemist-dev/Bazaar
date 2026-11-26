using System.Diagnostics;

namespace Bazaar.Client.Linux.Updates;

/// <summary>The process to start after the running one has shut down: an absolute executable and its arguments, never a shell line.</summary>
/// <param name="FileName">Absolute path of the executable.</param>
/// <param name="Arguments">Arguments passed as an argument list.</param>
public sealed record RestartRequest(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// "Restart to apply" (doc 30 section 7). Only the user's press calls <see cref="TryRestart"/>; it refuses unless an update is
/// staged and the process path is absolute and not the <c>dotnet</c> host. It never starts the new process itself: it hands the
/// request to <c>schedule</c> and asks the application to shut down (which closes the chat connection). The new process starts
/// from <see cref="PendingRestart.StartIfScheduled(Action{string})"/> once <c>Main</c> has released the single-instance pipe, so
/// it does not forward its launch to the instance that is exiting.
/// </summary>
public sealed class UpdateRestart
{
    /// <summary>Switches that mean "do this once" and are dropped from the restarted command line, plus the warm-start flag.</summary>
    private static readonly string[] _oneShotFlags = ["--sign-in", "--create-company", "--first-run", "--preload"];

    private readonly Func<bool> _isStaged;
    private readonly Func<string?> _processPath;
    private readonly IReadOnlyList<string> _args;
    private readonly Action _shutdown;
    private readonly Action<RestartRequest> _schedule;
    private int _requested;

    /// <summary>Creates the action. Nothing runs until <see cref="TryRestart"/>.</summary>
    /// <param name="isStaged">True when an update is staged.</param>
    /// <param name="processPath">The running executable, normally <c>Environment.ProcessPath</c>.</param>
    /// <param name="args">The original command line.</param>
    /// <param name="shutdown">Asks the application lifetime to shut down cleanly.</param>
    /// <param name="schedule">Records the request to start after shutdown.</param>
    public UpdateRestart(Func<bool> isStaged, Func<string?> processPath, IReadOnlyList<string> args, Action shutdown, Action<RestartRequest> schedule)
    {
        ArgumentNullException.ThrowIfNull(isStaged);
        ArgumentNullException.ThrowIfNull(processPath);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(schedule);
        _isStaged = isStaged;
        _processPath = processPath;
        _args = args;
        _shutdown = shutdown;
        _schedule = schedule;
    }

    /// <summary>True when a restart would apply a staged update.</summary>
    public bool CanRestart => _isStaged();

    /// <summary>The request a restart would start, or the reason there is none. Pure.</summary>
    public static (RestartRequest? Request, string? Refusal) Plan(bool staged, string? processPath, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!staged)
        {
            return (null, "No update is staged, so there is nothing to restart for.");
        }

        if (string.IsNullOrEmpty(processPath) || processPath[0] != '/')
        {
            return (null, "Cannot restart: the process path is not an absolute path. Start Bazaar again by hand.");
        }

        if (string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.Ordinal))
        {
            return (null, "Cannot restart: Bazaar is running under the dotnet host, not as its own binary. Start Bazaar again by hand.");
        }

        return (new RestartRequest(processPath, [.. args.Where(a => !_oneShotFlags.Contains(a, StringComparer.Ordinal))]), null);
    }

    /// <summary>
    /// Validates, schedules the new process and asks for shutdown. Returns null when the restart began, else the refusal text.
    /// A second call after success does nothing and returns null.
    /// </summary>
    public string? TryRestart()
    {
        var (request, refusal) = Plan(_isStaged(), _processPath(), _args);
        if (request is null)
        {
            return refusal;
        }

        if (Interlocked.Exchange(ref _requested, 1) == 0)
        {
            _schedule(request);
            _shutdown();
        }

        return null;
    }
}

/// <summary>Holds the one restart request between shutdown and the end of <c>Main</c>.</summary>
public static class PendingRestart
{
    private static RestartRequest? _request;

    /// <summary>Records the request to start once the process is out of the way.</summary>
    public static void Set(RestartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _request, request);
    }

    /// <summary>Starts the scheduled process, if any, with <c>UseShellExecute</c> off. Never throws; failure goes to <paramref name="log"/>.</summary>
    public static void StartIfScheduled(Action<string> log) => StartIfScheduled(Start, log);

    /// <summary>Starts the scheduled request through <paramref name="start"/>; it is taken once.</summary>
    public static void StartIfScheduled(Action<RestartRequest> start, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(log);
        var request = Interlocked.Exchange(ref _request, null);
        if (request is null)
        {
            return;
        }

        try
        {
            start(request);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log($"Bazaar: restart failed: {ex.Message}");
        }
    }

    private static void Start(RestartRequest request)
    {
        var info = new ProcessStartInfo(request.FileName) { UseShellExecute = false };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
    }
}
