using Bazaar.Client.Linux.Display;
using Bazaar.Client.Materials;

namespace Bazaar.Client.Linux;

/// <summary>What the Linux head can learn about the session without talking to the compositor.</summary>
/// <param name="Backend">The windowing system the head runs on.</param>
/// <param name="LowPowerProfile">The firmware platform profile is low-power (the closest thing Linux has to battery saver).</param>
/// <param name="HighContrastTheme">The GTK theme names a high-contrast variant, read as the person asking for less transparency.</param>
public readonly record struct LinuxSessionFacts(DisplayBackend Backend, bool LowPowerProfile, bool HighContrastTheme)
{
    /// <summary>The kernel's ACPI platform profile; the file is absent on machines without one.</summary>
    public const string PlatformProfilePath = "/sys/firmware/acpi/platform_profile";

    /// <summary>Reads the session from the environment and the platform profile file. Never throws.</summary>
    public static LinuxSessionFacts Read(Func<string, string?> environment, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readFile);

        string? profile = readFile(PlatformProfilePath)?.Trim();
        string? theme = environment("GTK_THEME");
        return new LinuxSessionFacts(
            DisplayBackendSelector.Select(environment),
            string.Equals(profile, "low-power", StringComparison.OrdinalIgnoreCase),
            theme is not null && theme.Contains("HighContrast", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the live session.</summary>
    public static LinuxSessionFacts ReadLive() => Read(Environment.GetEnvironmentVariable, TryReadFile);

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The Linux head's <see cref="IMaterialEnvironmentSource"/> (doc 93, Materials), best effort. Linux has no system blur API a client
/// can rely on, so the head claims only a tinted translucent surface, and only on a session with a display: whether the compositor
/// really composites is learned after the window opens (the shell reads the window's actual transparency level and reports a refusal
/// through <see cref="ReportBackdropFailed"/>, which turns the chrome opaque). Reduce-transparency has no portable system setting, so a
/// high-contrast GTK theme stands in for it; the person's <c>client.materials</c> setting stays the reliable switch.
/// </summary>
public sealed class LinuxMaterialEnvironmentSource : IMaterialEnvironmentSource, IMaterialFeedback
{
    private readonly PolledMaterialEnvironmentSource _inner;

    public LinuxMaterialEnvironmentSource(Func<LinuxSessionFacts>? facts = null, TimeProvider? time = null)
    {
        Func<LinuxSessionFacts> read = facts ?? LinuxSessionFacts.ReadLive;
        _inner = new PolledMaterialEnvironmentSource(() => Map(read()), time);
    }

    /// <summary>The person's <c>client.materials</c> setting, the frame-budget flag and the cache control.</summary>
    public PolledMaterialEnvironmentSource Settings => _inner;

    /// <inheritdoc />
    public MaterialEnvironment Current => _inner.Current;

    /// <inheritdoc />
    public void ReportBackdropFailed() => _inner.ReportBackdropFailed();

    /// <summary>The platform half of the environment for a set of facts. Pure.</summary>
    public static MaterialEnvironment Map(LinuxSessionFacts facts) => new(
        MaterialPlatform.Linux,
        TransparencyAvailable: facts.Backend != DisplayBackend.None,
        ReduceTransparency: facts.HighContrastTheme,
        BatterySaver: facts.LowPowerProfile);
}
