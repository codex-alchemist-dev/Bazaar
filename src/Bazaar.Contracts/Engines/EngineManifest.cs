namespace Bazaar.Contracts.Engines;

/// <summary>
/// What an engine adapter declares about itself when it starts: its id, version, capabilities and hosting needs
/// (docs/design/20-engine-integration-api.md, IEngineAdapter.Manifest).
/// <c>BuildVariant</c> is the required build variant (for example a Godot .NET build); <c>BinaryPath</c> is the engine binary.
/// </summary>
public sealed record EngineManifest(
    string EngineId,
    string Version,
    IReadOnlyList<EngineCapability> Capabilities,
    bool NeedsGuiHost,
    string? BuildVariant = null,
    string? BinaryPath = null);
