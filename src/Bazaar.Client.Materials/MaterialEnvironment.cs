namespace Bazaar.Client.Materials;

/// <summary>The platform families the policy distinguishes.</summary>
public enum MaterialPlatform
{
    Windows,
    Linux,
    Android,
    Other,
}

/// <summary>
/// A snapshot of what the platform can do and what conditions apply now. The client heads fill it from the OS (Windows build,
/// reduce-transparency, power state) and from the frame-budget monitor; the policy never reads the OS itself.
/// </summary>
public sealed record MaterialEnvironment(
    MaterialPlatform Platform,
    MaterialLevel Level = MaterialLevel.Auto,
    bool MicaAvailable = false,
    bool AcrylicAvailable = false,
    bool TransparencyAvailable = false,
    bool BlurMeasuredWithinBudget = false,
    bool ReduceTransparency = false,
    bool BatterySaver = false,
    bool BackdropRequestFailed = false,
    bool FrameBudgetMissed = false);

/// <summary>Supplies the current <see cref="MaterialEnvironment"/>. A head implements it over the OS settings and the frame monitor.</summary>
public interface IMaterialEnvironmentSource
{
    /// <summary>The environment now.</summary>
    MaterialEnvironment Current { get; }
}
