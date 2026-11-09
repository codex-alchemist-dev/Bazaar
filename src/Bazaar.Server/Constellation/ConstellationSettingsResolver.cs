using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Constellation;

/// <summary>The Git Solar System choices read from the <c>constellation.orbit_radius</c> and <c>constellation.git_layout</c> settings.</summary>
/// <param name="OrbitRadius">Orbit radius rule for branches.</param>
/// <param name="GitLayout">Deterministic orbits or the force layout for the commit DAG.</param>
public sealed record ConstellationLayoutChoices(ConstellationOrbitRadius OrbitRadius, ConstellationGitLayout GitLayout);

/// <summary>
/// Maps stored <c>constellation.*</c> values to layout choices. Pure: it reads a snapshot of stored JSON and never touches
/// storage. An unset key takes its shipped default. A stored value that fails the key's type or validator check throws
/// <see cref="InvalidSettingValueException"/>, the same fail-closed read every setting uses.
/// </summary>
public static class ConstellationSettingsResolver
{
    /// <summary>Resolves the layout choices from stored values keyed by setting name. Keys that are not constellation settings are ignored.</summary>
    public static ConstellationLayoutChoices Resolve(IReadOnlyDictionary<string, JsonElement> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return new ConstellationLayoutChoices(
            OrbitRadius: ReadOrDefault(stored, ConstellationSettings.OrbitRadius),
            GitLayout: ReadOrDefault(stored, ConstellationSettings.GitLayout));
    }

    private static T ReadOrDefault<T>(IReadOnlyDictionary<string, JsonElement> stored, SettingKey<T> key) =>
        stored.TryGetValue(key.Name, out var json) ? key.Read(json) : key.Default;
}
