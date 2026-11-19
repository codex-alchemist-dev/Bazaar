namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>The command a confined run executes inside the namespaces.</summary>
/// <param name="Executable">Absolute path of the executable (the host file system is visible to it; see <see cref="LinuxConfinementProvider"/>).</param>
/// <param name="Arguments">Its arguments, passed as an argument vector, never through a shell string.</param>
/// <param name="AdditionalReadPaths">Extra absolute paths the process may read and execute when hardening is on (Landlock). The executable's own directory and the system library directories are always allowed; ignored without a helper.</param>
public sealed record LinuxLaunchCommand(string Executable, IReadOnlyList<string> Arguments, IReadOnlyList<string>? AdditionalReadPaths = null);

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

    /// <summary>
    /// Path of the hardening helper executable (<c>Bazaar.Server.DevSandbox.Linux.Helper</c>). Null (the default) means namespaces and rlimits
    /// only. When set, every launch goes through the helper, which applies no_new_privs, Landlock and a seccomp allow list before exec, and the
    /// self-test must prove them; if it cannot (helper missing, no Landlock), <see cref="LinuxConfinementProvider.Tested"/> is false and nothing
    /// starts. There is no silent fall back to the weaker mode.
    /// </summary>
    public string? HelperPath { get; init; }

    /// <summary>
    /// Environment variables given to the helper's own start-up (for a framework-dependent helper, <c>DOTNET_ROOT</c>), never to the confined
    /// target: the helper hands the target an environment holding only PATH. Ignored without <see cref="HelperPath"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? HelperEnvironment { get; init; }

    /// <summary>Path of the POSIX shell used by the self-test probe.</summary>
    public string ShellPath { get; init; } = "/bin/sh";

    /// <summary>The only environment variable the confined process gets.</summary>
    public string Path { get; init; } = "/usr/bin:/bin";

    /// <summary>Process-count limit (<c>RLIMIT_NPROC</c>, counted per real user on the host).</summary>
    public int MaxProcesses { get; init; } = 512;

    /// <summary>Open-file limit (<c>RLIMIT_NOFILE</c>).</summary>
    public int MaxOpenFiles { get; init; } = 256;

    /// <summary>Largest file the module may create or grow (<c>RLIMIT_FSIZE</c>, in bytes). Default 64 MiB. Must be positive.</summary>
    public long MaxFileSizeBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Directory under which per-run working directories are created. Null means the system temp directory.</summary>
    public string? WorkRoot { get; init; }

    /// <summary>How long the self-test probe may run.</summary>
    public TimeSpan SelfTestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Directories every hardened process may read and execute from, besides its own executable directory.</summary>
    internal static readonly string[] SystemReadPaths = ["/lib", "/lib64", "/usr/lib", "/usr/lib64", "/proc/self"];

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
