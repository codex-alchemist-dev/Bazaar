namespace Bazaar.Engines.Roblox.Rojo;

/// <summary>
/// Configurable inputs for the Rojo binary. Nothing is pinned by default: no Rojo version has been verified
/// (docs/design/20-engine-integration-api.md, Q8). The operator sets <see cref="BinaryPath"/> and, once a
/// version is chosen, <see cref="RequiredVersion"/>.
/// </summary>
public sealed record RojoOptions
{
    /// <summary>Path to the rojo executable, or a bare name resolved through PATH.</summary>
    public string BinaryPath { get; init; } = "rojo";

    /// <summary>Optional version the probe must report. Null means no pin is enforced.</summary>
    public string? RequiredVersion { get; init; }

    /// <summary>Wall-clock limit for each rojo invocation.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
