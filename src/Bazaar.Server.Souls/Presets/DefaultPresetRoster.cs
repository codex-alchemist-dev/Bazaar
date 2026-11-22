using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Presets;

// TODO(component): move to AddBazaarSouls() when the component model (PR #125) merges.

/// <summary>
/// The shipped default roster (docs/design/07-soul-system.md, "Default roster"). Presets are data: the roster is the embedded
/// <c>default-roster.json</c>, validated by <see cref="PresetLoader"/> on every load. Operators edit or replace it.
/// </summary>
public static class DefaultPresetRoster
{
    /// <summary>Logical name of the embedded roster resource.</summary>
    public const string ResourceName = "default-roster.json";

    /// <summary>Loads and validates the shipped roster.</summary>
    /// <exception cref="InvalidOperationException">The embedded resource is missing from the assembly.</exception>
    /// <exception cref="PresetValidationException">The shipped roster breaks a preset rule.</exception>
    public static IReadOnlyList<Preset> Load()
    {
        var assembly = typeof(DefaultPresetRoster).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing from assembly {assembly.GetName().Name}.");
        return PresetLoader.Parse(stream);
    }
}
