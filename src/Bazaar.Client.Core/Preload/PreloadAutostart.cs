namespace Bazaar.Client.Core.Preload;

/// <summary>
/// The warm hidden process (doc 40). The head started with <c>--preload</c> builds its window but does not show it;
/// a later launch from the icon is forwarded to it and shows the window at once. Off by default.
/// </summary>
public static class PreloadArguments
{
    /// <summary>The launch flag for the warm hidden process.</summary>
    public const string Flag = "--preload";

    /// <summary>True when the command line asks for a hidden, warm start.</summary>
    public static bool IsPreload(IEnumerable<string> args) => args.Contains(Flag, StringComparer.Ordinal);

    /// <summary>The command line that starts the warm process, with the executable path quoted.</summary>
    public static string CommandLine(string executablePath) => $"\"{ValidatePath(executablePath)}\" {Flag}";

    /// <summary>Rejects paths that would break a quoted command line or a unit file.</summary>
    internal static string ValidatePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("The executable path cannot contain quotes or line breaks.", nameof(executablePath));
        }

        return executablePath;
    }
}

/// <summary>Renders the systemd user unit that starts the warm process at login (Linux).</summary>
public static class SystemdUserUnit
{
    /// <summary>The unit file name.</summary>
    public const string FileName = "bazaar-preloader.service";

    /// <summary>The unit text for an executable.</summary>
    public static string Render(string executablePath) =>
        string.Join('\n',
            "[Unit]",
            "Description=Bazaar warm client (preloader)",
            "After=graphical-session.target",
            "",
            "[Service]",
            $"ExecStart={PreloadArguments.CommandLine(executablePath)}",
            "Restart=on-failure",
            "",
            "[Install]",
            "WantedBy=default.target",
            "");
}

/// <summary>The per-user autostart entry (Windows Run key value, and the XDG fallback) for the warm process.</summary>
public static class PreloadRunEntry
{
    /// <summary>The value name under the per-user Run key.</summary>
    public const string ValueName = "Bazaar Preloader";

    /// <summary>The per-user Run key path, relative to HKEY_CURRENT_USER.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The command written to the Run key.</summary>
    public static string Command(string executablePath) => PreloadArguments.CommandLine(executablePath);
}

/// <summary>Enables or disables the warm process at login. Implemented per head (Windows Run key, Linux systemd user unit).</summary>
public interface IPreloadAutostart
{
    /// <summary>True when the login entry exists.</summary>
    bool IsEnabled();

    /// <summary>Writes the login entry for the executable. Opt-in only; the caller asks the user first.</summary>
    void Enable(string executablePath);

    /// <summary>Removes the login entry if it exists.</summary>
    void Disable();
}
