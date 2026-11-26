using System.Runtime.InteropServices;
using Bazaar.Client.Materials;
using Microsoft.Win32;

namespace Bazaar.Client.Windows;

/// <summary>What the Windows head reads from the OS for the material policy.</summary>
/// <param name="BuildNumber">The Windows build number (for example 22631 for Windows 11 23H2).</param>
/// <param name="TransparencyEnabled">The "Transparency effects" switch in Settings, Personalization, Colors. Off means reduce transparency.</param>
/// <param name="BatterySaver">Battery saver is on.</param>
public readonly record struct WindowsPlatformFacts(int BuildNumber, bool TransparencyEnabled, bool BatterySaver)
{
    /// <summary>Reads the live OS. Off Windows it reports a platform with nothing to offer, so the policy falls back to opaque.</summary>
    public static WindowsPlatformFacts Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsPlatformFacts(0, TransparencyEnabled: false, BatterySaver: false);
        }

        return new WindowsPlatformFacts(Environment.OSVersion.Version.Build, ReadTransparencyEnabled(), ReadBatterySaver());
    }

    // Personalize\EnableTransparency is a DWORD; the value is absent until the person changes the switch, and absent means on.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ReadTransparencyEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    // SYSTEM_POWER_STATUS.SystemStatusFlag is 1 while battery saver is on.
    private static bool ReadBatterySaver() => NativeMethods.GetSystemPowerStatus(out SystemPowerStatus status) && status.SystemStatusFlag == 1;
}

// SYSTEM_POWER_STATUS (winbase.h).
[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte ACLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>
/// The Windows head's <see cref="IMaterialEnvironmentSource"/> (doc 93, Materials). Mica needs Windows 11 (build 22000), acrylic
/// needs the Windows 10 April 2018 build (17134), and a plain transparent window needs Windows 10. The Transparency effects switch
/// is the system's reduce-transparency setting, and battery saver is read from the power status. The Windows-only reads live in
/// <see cref="WindowsPlatformFacts"/>, behind this head; the rest of the client sees only the interface.
/// </summary>
public sealed class WindowsMaterialEnvironmentSource : IMaterialEnvironmentSource, IMaterialFeedback
{
    /// <summary>Windows 11 21H2, the first build with Mica.</summary>
    public const int MicaMinBuild = 22000;

    /// <summary>Windows 10 1803, the first build with acrylic blur-behind.</summary>
    public const int AcrylicMinBuild = 17134;

    /// <summary>Windows 10 1507, the first build of the supported Windows 10 line.</summary>
    public const int TransparencyMinBuild = 10240;

    private readonly PolledMaterialEnvironmentSource _inner;

    public WindowsMaterialEnvironmentSource(Func<WindowsPlatformFacts>? facts = null, TimeProvider? time = null)
    {
        Func<WindowsPlatformFacts> read = facts ?? WindowsPlatformFacts.Read;
        _inner = new PolledMaterialEnvironmentSource(() => Map(read()), time);
    }

    /// <summary>The person's <c>client.materials</c> setting, the frame-budget flag and the cache control.</summary>
    public PolledMaterialEnvironmentSource Settings => _inner;

    /// <inheritdoc />
    public MaterialEnvironment Current => _inner.Current;

    /// <inheritdoc />
    public void ReportBackdropFailed() => _inner.ReportBackdropFailed();

    /// <summary>The platform half of the environment for a set of facts. Pure.</summary>
    public static MaterialEnvironment Map(WindowsPlatformFacts facts) => new(
        MaterialPlatform.Windows,
        MicaAvailable: facts.BuildNumber >= MicaMinBuild,
        AcrylicAvailable: facts.BuildNumber >= AcrylicMinBuild,
        TransparencyAvailable: facts.BuildNumber >= TransparencyMinBuild,
        ReduceTransparency: !facts.TransparencyEnabled,
        BatterySaver: facts.BatterySaver);
}
