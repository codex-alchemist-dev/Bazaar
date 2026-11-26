using System.ComponentModel;
using System.Diagnostics;
using Bazaar.Client.Motion;

namespace Bazaar.Client.Linux;

/// <summary>
/// The Linux reduced-motion flag (doc 93), best effort. Linux has no portable system setting, so the head asks GNOME's
/// <c>org.gnome.desktop.interface enable-animations</c> through <c>gsettings</c> (also honoured by the GTK-based desktops that
/// share it) and reads "not reduced" when that tool or key is absent. The person's <c>client.motion_level</c> stays the reliable
/// switch. The tool runs off the UI thread with a short timeout; the head refreshes at start and when the window is activated.
/// </summary>
public sealed class LinuxReducedMotion : IOsReducedMotionSource
{
    /// <summary>How long the head waits for <c>gsettings</c> before giving up.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Func<string?> _readGsettings;
    private readonly PolledOsReducedMotionSource _inner;
    private volatile bool _latest;

    /// <summary>Creates the source. <paramref name="readGsettings"/> returns the tool's standard output, or null when unavailable; the default runs <c>gsettings</c>.</summary>
    public LinuxReducedMotion(Func<string?>? readGsettings = null)
    {
        _readGsettings = readGsettings ?? RunGsettings;
        _inner = new PolledOsReducedMotionSource(() => _latest);
    }

    /// <inheritdoc />
    public bool Current => _inner.Current;

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    /// <summary>Reads the desktop setting (off the calling thread) and raises <see cref="Changed"/> if the answer moved.</summary>
    public Task RefreshAsync() => Task.Run(() =>
    {
        _latest = Parse(_readGsettings());
        _inner.Refresh();
    });

    /// <summary>
    /// Reads the output of <c>gsettings get org.gnome.desktop.interface enable-animations</c>: "false" means animations are off, so
    /// reduced motion. Anything else (true, an error text, nothing) is not reduced. Pure.
    /// </summary>
    public static bool Parse(string? gsettingsOutput) => string.Equals(gsettingsOutput?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static string? RunGsettings()
    {
        try
        {
            var start = new ProcessStartInfo("gsettings")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("get");
            start.ArgumentList.Add("org.gnome.desktop.interface");
            start.ArgumentList.Add("enable-animations");
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(ReadTimeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
