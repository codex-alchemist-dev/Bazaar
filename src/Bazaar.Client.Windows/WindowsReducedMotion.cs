using System.Runtime.InteropServices;
using Bazaar.Client.Motion;

namespace Bazaar.Client.Windows;

/// <summary>
/// The Windows reduced-motion flag (doc 93): Settings, Accessibility, Visual effects, Animation effects. Off means the person wants
/// less motion, which the client reads as <see cref="MotionLevel.Reduced"/>. The Windows-only call lives here, in the head. Windows
/// raises no simple change event for it from managed code, so the head calls <see cref="PolledOsReducedMotionSource.Refresh"/> when
/// the window is activated, which is when a person comes back from changing it in Settings.
/// </summary>
public static class WindowsReducedMotion
{
    /// <summary>SPI_GETCLIENTAREAANIMATION (winuser.h).</summary>
    public const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>Reads the live flag. Off Windows, or when the call fails, motion is not reduced.</summary>
    public static bool Read() => OperatingSystem.IsWindows() && Map(NativeMethods.SystemParametersInfo(SpiGetClientAreaAnimation, 0, out int enabled, 0), enabled);

    /// <summary>The flag for a call result: animations switched off (a successful read of 0) means reduced. Pure.</summary>
    public static bool Map(bool callSucceeded, int animationsEnabled) => callSucceeded && animationsEnabled == 0;

    /// <summary>A source reading the live flag.</summary>
    public static PolledOsReducedMotionSource Create() => new(Read);
}

internal static partial class NativeMethods
{
    // pvParam receives a BOOL for SPI_GETCLIENTAREAANIMATION.
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
