using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bazaar.Guest.Agent;

/// <summary>
/// Starts child processes and kills their trees. On Linux the child is started through <c>setsid -w</c>, which makes
/// it the leader of a new session and process group, so a timeout or cancel can SIGKILL the whole group, including
/// background grandchildren. setsid execs the program without forking, so the pid the agent holds is the program's
/// own. Elsewhere, or on Linux without setsid, the tree is killed with <see cref="Process.Kill(bool)"/> and
/// entireProcessTree set.
/// </summary>
internal static partial class ChildProcess
{
    private const int _sigkill = 9;
    private static readonly string? _setsid = FindSetsid();

    /// <summary>
    /// Starts <paramref name="argv"/> with redirected stdin, stdout and stderr, and exactly the given environment.
    /// Returns whether the child owns its process group, which decides how it is killed.
    /// </summary>
    public static (Process Process, bool OwnsGroup) Start(
        IReadOnlyList<string> argv,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            info.WorkingDirectory = workingDirectory;
        }

        info.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        if (_setsid is not null)
        {
            info.FileName = _setsid;
            info.ArgumentList.Add("-w");
            info.ArgumentList.Add("--");
            foreach (var arg in argv)
            {
                info.ArgumentList.Add(arg);
            }
        }
        else
        {
            info.FileName = argv[0];
            for (var i = 1; i < argv.Count; i++)
            {
                info.ArgumentList.Add(argv[i]);
            }
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        return (process, _setsid is not null);
    }

    /// <summary>Kills the child and, when it owns its group, every process left in that group. Never throws.</summary>
    public static void KillTree(Process process, bool ownsGroup)
    {
        try
        {
            if (ownsGroup)
            {
                // A negative pid signals the process group. ESRCH (group already empty) is harmless.
                _ = SendSignal(-process.Id, _sigkill);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited or disposed.
        }
        catch (AggregateException)
        {
            // Process.Kill(entireProcessTree) reports members it could not reach, and a tree that exited meanwhile
            // yields "no process is associated". Either way there is nothing left to kill.
        }
        catch (Win32Exception)
        {
            // The OS refused the signal; the process is then left to exit on its own.
        }
    }

    private static string? FindSetsid()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
