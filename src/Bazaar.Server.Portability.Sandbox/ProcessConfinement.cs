using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bazaar.Server.Portability.Sandbox;

/// <summary>Chooses the confinement provider for the running OS. Windows registers its job-object provider from Bazaar.Server.Providers.Windows.</summary>
public static class ParserConfinements
{
    private const string _windowsProviders = "Bazaar.Server.Providers.Windows.WindowsProviders, Bazaar.Server.Providers.Windows";

    /// <summary>
    /// Linux gets the rlimit and namespace provider. Windows gets the job-object provider from
    /// <c>WindowsProviders.ParserConfinement()</c> in Bazaar.Server.Providers.Windows, found by name because that assembly
    /// depends on the server and this one must not. If it is not deployed, the tree-kill-only fallback is used and every
    /// confinement item is reported as not applied (so <c>RequireFullConfinement</c> refuses to run).
    /// </summary>
    public static IParserConfinement CreateDefault()
    {
        if (OperatingSystem.IsLinux()) return new LinuxParserConfinement();
        if (OperatingSystem.IsWindows() && LoadWindowsProvider() is { } windows) return windows;
        return new TreeKillOnlyConfinement();
    }

    internal static IParserConfinement? LoadWindowsProvider()
    {
        try
        {
            var type = Type.GetType(_windowsProviders, throwOnError: false);
            return type?.GetMethod("ParserConfinement", Type.EmptyTypes)?.Invoke(null, null) as IParserConfinement;
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or FileLoadException or BadImageFormatException or MethodAccessException)
        {
            return null;
        }
    }
}

/// <summary>Kills the child and its descendants with the framework tree kill. Never throws.</summary>
internal sealed class ProcessTreeGuard : IProcessGuard
{
    private readonly Process _process;

    public ProcessTreeGuard(Process process)
    {
        _process = process;
    }

    public void KillTree()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            // Already gone, or the OS refused; the runner's wait then reports the real state.
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Fallback for an OS with no provider: only the wall-clock timeout and the tree kill apply. Every confinement item is
/// reported as not applied, so <see cref="ParserSandboxOptions.RequireFullConfinement"/> refuses to run.
/// </summary>
public sealed class TreeKillOnlyConfinement : IParserConfinement
{
    public string Name => "tree-kill-only";

    public ConfinementPlan Prepare(ConfinementLimits limits, LaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(command);
        return new ConfinementPlan(
            command,
            ["process-tree-kill"],
            [.. LinuxParserConfinement.AllItems(limits)]);
    }
}

/// <summary>
/// Linux provider: the child runs in its own session and process group (killed as a group), under <c>prlimit</c> CPU,
/// and core-dump limits, and in a new network namespace through <c>unshare</c> when the host allows it.
/// Tools are looked up on the usual paths; a missing tool means that item is reported as not applied, never silently
/// assumed. Filesystem read and write confinement and a child-process cap are not applied here (see the doc 44 status note).
/// </summary>
public sealed partial class LinuxParserConfinement : IParserConfinement
{
    public const string ItemNoNetwork = "no-network";
    public const string ItemCpuLimit = "cpu-limit";
    public const string ItemNoFileWrites = "no-file-writes";
    public const string ItemNoCoreDumps = "no-core-dumps";
    public const string ItemGroupKill = "process-group-kill";
    public const string ItemNoChildProcesses = "no-child-processes";
    public const string ItemFileReadScope = "file-read-scope";

    private const int _sigkill = 9;
    private static readonly string[] _binDirs = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];
    private static readonly Lazy<string[]?> _networkFlags = new(ProbeNetworkFlags);

    public string Name => "linux-rlimit-unshare";

    /// <summary>Every item a provider is asked to cover, for the fallback provider's report.</summary>
    internal static IEnumerable<string> AllItems(ConfinementLimits limits)
    {
        if (limits.NoNetwork) yield return ItemNoNetwork;
        yield return ItemCpuLimit;
        if (limits.NoFileWrites) yield return ItemNoFileWrites;
        yield return ItemNoCoreDumps;
        yield return ItemGroupKill;
        if (limits.NoChildProcesses) yield return ItemNoChildProcesses;
        yield return ItemFileReadScope;
    }

    public ConfinementPlan Prepare(ConfinementLimits limits, LaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(command);

        var applied = new List<string>();
        var notApplied = new List<string>();
        var prefix = new List<string>();
        var ownsGroup = false;

        if (Find("setsid") is { } setsid)
        {
            // setsid execs without forking when the caller is not a group leader, so the pid the runner holds is the child's.
            prefix.AddRange([setsid, "-w", "--"]);
            applied.Add(ItemGroupKill);
            ownsGroup = true;
        }
        else
        {
            notApplied.Add(ItemGroupKill);
        }

        if (limits.NoNetwork)
        {
            if (Find("unshare") is { } unshare && _networkFlags.Value is { } flags)
            {
                prefix.Add(unshare);
                prefix.AddRange(flags);
                prefix.Add("--");
                applied.Add(ItemNoNetwork);
            }
            else
            {
                notApplied.Add(ItemNoNetwork);
            }
        }

        if (Find("prlimit") is { } prlimit)
        {
            prefix.Add(prlimit);
            prefix.Add("--cpu=" + limits.CpuSeconds);
            prefix.Add("--core=0");
            applied.Add(ItemCpuLimit);
            applied.Add(ItemNoCoreDumps);
            prefix.Add("--");
        }
        else
        {
            notApplied.Add(ItemCpuLimit);
            notApplied.Add(ItemNoCoreDumps);
        }

        // A zero file-size limit would stop file writes, but it kills the .NET runtime at startup (its W^X memory
        // allocator grows a memfd), so it is not used. File-write confinement stays unapplied on Linux.
        if (limits.NoFileWrites) notApplied.Add(ItemNoFileWrites);

        if (limits.NoChildProcesses) notApplied.Add(ItemNoChildProcesses);
        notApplied.Add(ItemFileReadScope);

        var launch = prefix.Count == 0
            ? command
            : new LaunchCommand(prefix[0], [.. prefix.Skip(1), command.FileName, .. command.Arguments]);
        return new ConfinementPlan(launch, applied, notApplied)
        {
            AttachAfterStart = process => new LinuxProcessGuard(process, ownsGroup),
        };
    }

    private static string? Find(string tool)
    {
        foreach (var dir in _binDirs)
        {
            var path = Path.Combine(dir, tool);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Finds the unshare flags that work on this host (plain, then with a user namespace), or null when neither does.</summary>
    private static string[]? ProbeNetworkFlags()
    {
        if (Find("unshare") is not { } unshare) return null;
        foreach (var flags in new[] { new[] { "--net" }, new[] { "--user", "--map-root-user", "--net" } })
        {
            try
            {
                var info = new ProcessStartInfo(unshare) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
                foreach (var flag in flags) info.ArgumentList.Add(flag);
                info.ArgumentList.Add("true");
                using var probe = Process.Start(info);
                if (probe is null) continue;
                if (!probe.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    probe.Kill(entireProcessTree: true);
                    continue;
                }
                if (probe.ExitCode == 0) return flags;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Try the next flag set.
            }
        }
        return null;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);

    private sealed class LinuxProcessGuard : IProcessGuard
    {
        private readonly Process _process;
        private readonly bool _ownsGroup;

        public LinuxProcessGuard(Process process, bool ownsGroup)
        {
            _process = process;
            _ownsGroup = ownsGroup;
        }

        public void KillTree()
        {
            try
            {
                // A negative pid signals the whole process group; ESRCH (group already empty) is harmless.
                if (_ownsGroup) _ = SendSignal(-_process.Id, _sigkill);
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
            {
                // Already gone, or the OS refused.
            }
        }

        public void Dispose()
        {
        }
    }
}
