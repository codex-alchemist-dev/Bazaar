using Bazaar.Contracts.Engines;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot;

/// <summary>
/// The manifest the Godot adapter declares (docs/design/20-engine-integration-api.md, IEngineAdapter.Manifest). It runs headless,
/// so <c>NeedsGuiHost</c> is false.
/// </summary>
public static class GodotAdapterManifest
{
    /// <summary>The engine id this adapter serves.</summary>
    public const string EngineId = "godot";

    /// <summary>Builds the manifest. The version is this assembly's SemVer prefix, with no build metadata.</summary>
    public static EngineManifest Create(GodotAdapterOptions? options = null)
    {
        GodotAdapterOptions resolved = options ?? new GodotAdapterOptions();
        Version? assembly = typeof(GodotAdapterManifest).Assembly.GetName().Version;
        string version = assembly is null ? "0.0.0" : assembly.ToString(3);
        return new EngineManifest(
            EngineId,
            version,
            [EngineCapability.Import, EngineCapability.Export, EngineCapability.Test, EngineCapability.ScriptCheck],
            NeedsGuiHost: false,
            BuildVariant: resolved.BuildVariant == GodotBuildVariant.Dotnet ? "dotnet" : "gdscript",
            BinaryPath: resolved.BinaryPath);
    }
}
