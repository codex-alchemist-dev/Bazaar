using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Bazaar.Server.Valkey;

/// <summary>Checks a process id against the running operating system. Tests inject a fake.</summary>
public interface IValkeyProcessProbe
{
    /// <summary>True when the process exists and is named <paramref name="name"/> (for example <c>valkey-server</c>).</summary>
    bool IsNamed(int pid, string name);

    /// <summary>Kills the process. Never throws when it is already gone.</summary>
    void Kill(int pid);
}

/// <summary>Reads <c>/proc</c> on Linux. On other systems nothing is named, so nothing is killed.</summary>
public sealed class LinuxValkeyProcessProbe : IValkeyProcessProbe
{
    /// <summary>Linux truncates a process name to 15 characters in <c>/proc/&lt;pid&gt;/comm</c>.</summary>
    private const int _commLength = 15;

    /// <inheritdoc />
    public bool IsNamed(int pid, string name)
    {
        if (!OperatingSystem.IsLinux() || pid <= 1)
        {
            return false;
        }

        var expected = name.Length > _commLength ? name[.._commLength] : name;
        try
        {
            return string.Equals(File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/comm").Trim(), expected, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Kill(int pid)
    {
        try
        {
            Process.GetProcessById(pid).Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone, or not ours to stop. Either way nothing is left to clean up.
        }
    }
}

/// <summary>
/// Kills a stale managed child that an earlier host left behind. The host records the child's process id in a pid file; at startup the
/// file is read and the process is killed only when it is still named as the server. The file is removed in every case.
/// </summary>
public sealed class ValkeyStaleChildSweeper(IValkeyProcessProbe probe)
{
    /// <summary>Sweeps once. Returns true when a stale child was killed.</summary>
    public bool Sweep(string pidFilePath, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pidFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        if (!File.Exists(pidFilePath))
        {
            return false;
        }

        var killed = false;
        try
        {
            if (int.TryParse(File.ReadAllText(pidFilePath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                && probe.IsNamed(pid, executableName))
            {
                probe.Kill(pid);
                killed = true;
            }
        }
        catch (IOException)
        {
            // An unreadable pid file proves nothing, so nothing is killed. The file is still removed below.
        }
        finally
        {
            ValkeyPidFile.Delete(pidFilePath);
        }

        return killed;
    }
}

/// <summary>The pid file of the managed child.</summary>
public static class ValkeyPidFile
{
    /// <summary>File name inside the runtime directory.</summary>
    public const string FileName = "valkey-server.pid";

    /// <summary>Removes the file when present. Never throws.</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the next sweep to remove; a missing file is not an error.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: nothing else can be done from here.
        }
    }
}
