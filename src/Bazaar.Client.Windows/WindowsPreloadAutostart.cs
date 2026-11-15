using System.Runtime.Versioning;
using Bazaar.Client.Core.Preload;
using Microsoft.Win32;

namespace Bazaar.Client.Windows;

/// <summary>
/// Login autostart for the warm process: a value under the per-user Run key (doc 40, decided 2026-11-11).
/// Only the current user's key is written; no admin rights are needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPreloadAutostart : IPreloadAutostart
{
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PreloadRunEntry.RunKeyPath, writable: false);
        return key?.GetValue(PreloadRunEntry.ValueName) is string;
    }

    public void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PreloadRunEntry.RunKeyPath, writable: true);
        key.SetValue(PreloadRunEntry.ValueName, PreloadRunEntry.Command(executablePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PreloadRunEntry.RunKeyPath, writable: true);
        key?.DeleteValue(PreloadRunEntry.ValueName, throwOnMissingValue: false);
    }
}
