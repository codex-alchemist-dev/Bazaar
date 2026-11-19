using System.Diagnostics;
using Bazaar.Client.Core.Preload;

namespace Bazaar.Client.Linux;

/// <summary>
/// Login autostart for the warm process: a systemd user unit under the XDG config home (doc 40).
/// Enabling runs <c>systemctl --user enable</c>; the unit is removed on disable.
/// </summary>
public sealed class LinuxPreloadAutostart(string unitDirectory) : IPreloadAutostart
{
    private readonly string _unitPath = Path.Combine(unitDirectory, SystemdUserUnit.FileName);

    public bool IsEnabled() => File.Exists(_unitPath);

    public void Enable(string executablePath)
    {
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(_unitPath, SystemdUserUnit.Render(executablePath));
        Systemctl("enable", SystemdUserUnit.FileName);
    }

    public void Disable()
    {
        if (!IsEnabled())
        {
            return;
        }

        Systemctl("disable", SystemdUserUnit.FileName);
        File.Delete(_unitPath);
    }

    /// <summary>Resolves the user unit directory: <c>$XDG_CONFIG_HOME/systemd/user</c>, else <c>~/.config/systemd/user</c>.</summary>
    public static string DefaultUnitDirectory(Func<string, string?> environment, string userHome)
    {
        var configHome = environment("XDG_CONFIG_HOME");
        var root = string.IsNullOrWhiteSpace(configHome) ? Path.Combine(userHome, ".config") : configHome;
        return Path.Combine(root, "systemd", "user");
    }

    private static void Systemctl(string verb, string unit)
    {
        using var process = Process.Start(new ProcessStartInfo("systemctl", ["--user", verb, unit]) { UseShellExecute = false })
            ?? throw new InvalidOperationException("systemctl could not be started.");
        process.WaitForExit();
    }
}
