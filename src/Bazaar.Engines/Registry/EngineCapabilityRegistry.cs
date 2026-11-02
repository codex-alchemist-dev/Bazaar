using Bazaar.Contracts.Engines;

namespace Bazaar.Engines.Registry;

/// <summary>
/// In-memory index of the engine adapters that have registered a manifest, keyed by adapter id (the manifest's
/// <see cref="EngineManifest.EngineId"/>). Answers "which adapters support capability X" so the UI and agents query by
/// capability, never by engine id (docs/design/20-engine-integration-api.md, Capabilities).
/// </summary>
public sealed class EngineCapabilityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EngineManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<EngineCapability>> _capabilities = new(StringComparer.Ordinal);

    /// <summary>Registers one adapter's manifest. Throws when that adapter id is already registered; a manifest is never replaced silently.</summary>
    /// <exception cref="ArgumentException">The manifest has no engine id.</exception>
    /// <exception cref="InvalidOperationException">An adapter with the same id is already registered.</exception>
    public void Register(EngineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.EngineId);
        ArgumentNullException.ThrowIfNull(manifest.Capabilities);

        lock (_gate)
        {
            if (_manifests.ContainsKey(manifest.EngineId))
            {
                throw new InvalidOperationException($"Engine adapter '{manifest.EngineId}' is already registered.");
            }

            _manifests[manifest.EngineId] = manifest;
            // Copy the capabilities so a later change to the caller's list cannot alter what the registry answers.
            _capabilities[manifest.EngineId] = [.. manifest.Capabilities];
        }
    }

    /// <summary>Adapter ids whose manifest declares <paramref name="capability"/>, in ordinal order. Empty when none do.</summary>
    public IReadOnlyList<string> AdaptersWithCapability(EngineCapability capability)
    {
        lock (_gate)
        {
            return [.. _capabilities
                .Where(pair => pair.Value.Contains(capability))
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The registered manifest for an adapter id, or null when none is registered.</summary>
    public EngineManifest? Find(string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        lock (_gate)
        {
            return _manifests.GetValueOrDefault(adapterId);
        }
    }
}
