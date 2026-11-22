using System.Runtime.InteropServices;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// Console mode helpers for the text-mode head on Windows. Lives here so Windows APIs stay inside this project
/// (doc 52, section 3).
/// </summary>
public static partial class WindowsConsoleMode
{
    private const int _stdOutputHandle = -11;
    private const uint _enableVirtualTerminalProcessing = 0x0004;
    private const uint _disableNewlineAutoReturn = 0x0008;

    /// <summary>
    /// Turns on ANSI escape processing for the console output, so Calcinato's cursor and colour sequences are drawn
    /// instead of printed. Does nothing off Windows or when output is not a console (redirected).
    /// </summary>
    /// <returns>True when escape processing is on afterwards, or when the OS needs no change.</returns>
    public static bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return EnableOnWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static unsafe bool EnableOnWindows()
    {
        var handle = GetStdHandle(_stdOutputHandle);
        if (handle == nint.Zero || handle == -1)
        {
            return false;
        }

        uint mode;
        if (GetConsoleMode(handle, &mode) == 0)
        {
            // Not a console (redirected output): nothing to switch on.
            return false;
        }

        if ((mode & _enableVirtualTerminalProcessing) != 0)
        {
            return true;
        }

        return SetConsoleMode(handle, mode | _enableVirtualTerminalProcessing | _disableNewlineAutoReturn) != 0
            || SetConsoleMode(handle, mode | _enableVirtualTerminalProcessing) != 0;
    }

    [LibraryImport("kernel32.dll")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static unsafe partial int GetConsoleMode(nint handle, uint* mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static partial int SetConsoleMode(nint handle, uint mode);
}
