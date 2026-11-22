using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Bazaar.Server.Garnet;

/// <summary>Checks a process id against the running operating system. Tests inject a fake.</summary>
public interface IGarnetProcessProbe
{
    /// <summary>True when the process exists and is named <paramref name="name"/> (for example <c>garnet-server.exe</c>).</summary>
    bool IsNamed(int pid, string name);

    /// <summary>Kills the process. Never throws when it is already gone.</summary>
    void Kill(int pid);
}

/// <summary>
/// Looks the process up by id and compares its image name (<c>Process.ProcessName</c>, the file name without <c>.exe</c> on Windows)
/// with the binary's, case-insensitively. A reused pid that now belongs to another program is therefore never killed.
/// </summary>
public sealed class ProcessGarnetProcessProbe : IGarnetProcessProbe
{
    /// <inheritdoc />
    public bool IsNamed(int pid, string name)
    {
        if (pid <= 1)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, ImageName(name), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The file name without a trailing <c>.exe</c> only: other dots belong to the name (<c>Bazaar.Server.exe</c>).</summary>
    internal static string ImageName(string path)
    {
        var file = Path.GetFileName(path);
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }

    /// <inheritdoc />
    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: false);
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
public sealed class GarnetStaleChildSweeper(IGarnetProcessProbe probe)
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
            GarnetPidFile.Delete(pidFilePath);
        }

        return killed;
    }
}

/// <summary>The pid file of the managed child.</summary>
public static class GarnetPidFile
{
    /// <summary>File name inside the runtime directory.</summary>
    public const string FileName = "garnet.pid";

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
