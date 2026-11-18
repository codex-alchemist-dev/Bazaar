namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>The command a confined run executes inside the namespaces.</summary>
/// <param name="Executable">Absolute path of the executable (the host file system is visible to it; see <see cref="LinuxConfinementProvider"/>).</param>
/// <param name="Arguments">Its arguments, passed as an argument vector, never through a shell string.</param>
public sealed record LinuxLaunchCommand(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Supplied by the engine: what to run for a given spec, with the per-run working directory the provider created.</summary>
public interface ILinuxSandboxLaunchPlan
{
    /// <summary>Builds the command for <paramref name="spec"/>. <paramref name="workDirectory"/> is the empty, private working directory.</summary>
    LinuxLaunchCommand Create(SandboxRunSpec spec, string workDirectory);
}

/// <summary>Tool locations and fixed limits for the Linux provider. The defaults suit a standard util-linux host.</summary>
public sealed class LinuxConfinementOptions
{
    /// <summary>Path of util-linux <c>unshare</c>. Null means look in <c>/usr/bin</c>, <c>/bin</c>, <c>/usr/sbin</c>, <c>/sbin</c>.</summary>
    public string? UnsharePath { get; init; }

    /// <summary>Path of util-linux <c>prlimit</c>. Null means look in the standard directories.</summary>
    public string? PrlimitPath { get; init; }

    /// <summary>Path of the POSIX shell used by the self-test probe.</summary>
    public string ShellPath { get; init; } = "/bin/sh";

    /// <summary>The only environment variable the confined process gets.</summary>
    public string Path { get; init; } = "/usr/bin:/bin";

    /// <summary>Process-count limit (<c>RLIMIT_NPROC</c>, counted per real user on the host).</summary>
    public int MaxProcesses { get; init; } = 512;

    /// <summary>Open-file limit (<c>RLIMIT_NOFILE</c>).</summary>
    public int MaxOpenFiles { get; init; } = 256;

    /// <summary>Directory under which per-run working directories are created. Null means the system temp directory.</summary>
    public string? WorkRoot { get; init; }

    /// <summary>How long the self-test probe may run.</summary>
    public TimeSpan SelfTestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal string? ResolveUnshare() => Resolve(UnsharePath, "unshare");

    internal string? ResolvePrlimit() => Resolve(PrlimitPath, "prlimit");

    private static string? Resolve(string? configured, string tool)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" })
        {
            var candidate = System.IO.Path.Combine(dir, tool);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
