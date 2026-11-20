namespace Bazaar.Server.Settings;

/// <summary>
/// A module's contribution to the settings registry: the typed keys it registers. The registry aggregates every
/// contribution with the built-in keys and rejects a name registered twice. Keys are declared in code; the database
/// holds only values.
/// </summary>
public interface ISettingContribution
{
    /// <summary>The keys this module registers. Names must be unique across the whole registry.</summary>
    IReadOnlyList<SettingDefinition> Settings { get; }
}
