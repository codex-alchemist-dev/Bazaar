namespace Bazaar.Server.Settings;

/// <summary>
/// Orbit radius rule for branches (doc 19 <c>constellation.orbit_radius</c>). Lives in the Abstractions project so the
/// Server-free algorithms can name it; the namespace stays <c>Bazaar.Server.Settings</c> so consumers need no edits.
/// </summary>
public enum ConstellationOrbitRadius
{
    Age,
    Equal,
}
