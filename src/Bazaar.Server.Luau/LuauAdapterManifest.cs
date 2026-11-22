using Bazaar.Contracts.Engines;

namespace Bazaar.Server.Luau;

/// <summary>
/// The manifest the Luau static-check adapter declares (docs/design/20-engine-integration-api.md, IEngineAdapter.Manifest).
/// It declares <see cref="EngineCapability.ScriptCheck"/> only. It runs headless, with no Studio, so <c>NeedsGuiHost</c> is false.
/// </summary>
public static class LuauAdapterManifest
{
    /// <summary>The engine id this adapter serves: the Roblox adapter's headless path (doc 20, Roblox adapter).</summary>
    public const string EngineId = "roblox";

    /// <summary>Builds the manifest. The version is this assembly's SemVer prefix, with no build metadata.</summary>
    public static EngineManifest Create()
    {
        Version? assembly = typeof(LuauAdapterManifest).Assembly.GetName().Version;
        string version = assembly is null ? "0.0.0" : assembly.ToString(3);
        return new EngineManifest(
            EngineId,
            version,
            [EngineCapability.ScriptCheck],
            NeedsGuiHost: false);
    }
}
