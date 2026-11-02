namespace Bazaar.Contracts.Soul;

/// <summary>
/// Schema constants for Soul presets (docs/design/07-soul-system.md, "Default roster"). A preset is a behavioural template:
/// a Soul with placeholder identity, no lessons, no memory and no project context.
/// </summary>
public static class PresetSchema
{
    /// <summary>The only preset schema version this build reads.</summary>
    public const int Version = 1;
}

/// <summary>
/// A behavioural Soul template. Presets hold no personal data and no project context; the validator in
/// <c>Bazaar.Server.Souls.Presets</c> enforces that.
/// </summary>
/// <param name="Id">Stable slug, unique within a preset set.</param>
/// <param name="SchemaVersion">Must equal <see cref="PresetSchema.Version"/>.</param>
/// <param name="Name">Placeholder display name. Operators replace it when they instantiate the preset.</param>
/// <param name="Role">Short role description.</param>
/// <param name="Description">What the preset is for.</param>
/// <param name="Specialties">Starting specialty tags. Tags are self-declared and change only through Council votes.</param>
/// <param name="ModelPolicy">Suggested model route and whitelist. A suggestion, never a grant.</param>
/// <param name="PreferencesJson">Behavioural preferences as canonical JSON (sorted keys, no insignificant whitespace).</param>
public sealed record Preset(
    string Id,
    int SchemaVersion,
    string Name,
    string Role,
    string Description,
    IReadOnlyList<string> Specialties,
    PresetModelPolicy ModelPolicy,
    string PreferencesJson);

/// <summary>A preset's suggested model policy.</summary>
/// <param name="Route">A <c>custom/&lt;name&gt;</c> delegated route, or a provider-qualified model (<c>provider:model</c>) that is listed in <paramref name="Whitelist"/>.</param>
/// <param name="Whitelist">Provider-qualified models the route may use. Never a global <c>auto</c> family or a wildcard.</param>
public sealed record PresetModelPolicy(string Route, IReadOnlyList<string> Whitelist);
