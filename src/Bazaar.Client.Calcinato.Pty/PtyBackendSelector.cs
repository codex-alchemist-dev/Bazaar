using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>Chooses the PTY backend for the current OS.</summary>
public static class PtyBackendSelector
{
    /// <summary>
    /// Returns ConPTY on Windows 10 1809 or later, the Unix PTY on Linux, and an unsupported backend elsewhere.
    /// </summary>
    public static IPtyBackend CreateDefault()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return new ConPtyBackend();
        }

        if (OperatingSystem.IsLinux())
        {
            return new UnixPtyBackend();
        }

        return new UnsupportedPtyBackend();
    }
}

/// <summary>Placeholder for an OS without a PTY backend. <see cref="IsSupported"/> is false and opening throws.</summary>
internal sealed class UnsupportedPtyBackend : IPtyBackend
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public IPtySession Open(TerminalSpec spec) =>
        throw new PlatformNotSupportedException("No local PTY backend exists for this operating system.");
}
