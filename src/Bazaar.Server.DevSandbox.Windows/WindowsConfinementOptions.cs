namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>The command a confined run executes inside the AppContainer.</summary>
/// <param name="Executable">Absolute drive-letter path of the executable (for example <c>C:\Windows\System32\curl.exe</c>). It and everything it loads must be readable by the AppContainer: system directories and Program Files qualify; the per-run work directory does too. A <c>.bat</c> or <c>.cmd</c> file and <c>cmd.exe</c> itself are refused, because a shell would reparse the arguments.</param>
/// <param name="Arguments">Its arguments, passed as an argument vector and quoted with the Windows rules, never through a shell string.</param>
public sealed record WindowsLaunchCommand(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Supplied by the engine: what to run for a given spec, with the per-run working directory the provider created.</summary>
public interface IWindowsSandboxLaunchPlan
{
    /// <summary>
    /// Builds the command for <paramref name="spec"/>. <paramref name="workDirectory"/> is the empty per-run working directory; the
    /// AppContainer already has modify access there (and only there), so the plan may copy the module's files into it.
    /// </summary>
    WindowsLaunchCommand Create(SandboxRunSpec spec, string workDirectory);
}

/// <summary>Locations and fixed limits for the Windows provider. The defaults suit a standard Windows install.</summary>
public sealed class WindowsConfinementOptions
{
    /// <summary>Directory under which per-run working directories are created. Null means the system temp directory.</summary>
    public string? WorkRoot { get; init; }

    /// <summary>The Windows system directory (<c>System32</c>), where the self-test finds <c>cmd.exe</c> and <c>curl.exe</c>. Null means <see cref="Environment.SystemDirectory"/>.</summary>
    public string? SystemDirectory { get; init; }

    /// <summary>The <c>SystemRoot</c> value given to the confined process. Null means the host's value, or <c>C:\Windows</c> when it has none.</summary>
    public string? SystemRoot { get; init; }

    /// <summary>Job-wide limit on live processes (<c>ActiveProcessLimit</c>).</summary>
    public int MaxProcesses { get; init; } = 32;

    /// <summary>How long each self-test probe may run.</summary>
    public TimeSpan SelfTestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    internal string ResolvedSystemDirectory => string.IsNullOrEmpty(SystemDirectory) ? Environment.SystemDirectory : SystemDirectory;

    internal string ResolvedSystemRoot =>
        !string.IsNullOrEmpty(SystemRoot)
            ? SystemRoot
            : Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } root ? root : @"C:\Windows";

    internal string ResolvedWorkRoot => WorkRoot ?? Path.GetTempPath();

    /// <summary>Joins with a backslash regardless of the host, because these paths go to Windows.</summary>
    internal string SystemFile(string name) => ResolvedSystemDirectory.TrimEnd('\\', '/') + "\\" + name;
}
