namespace Bazaar.Client.Settings;

/// <summary>The person's choice in the <c>client.density</c> setting.</summary>
public enum ClientDensity
{
    /// <summary>The default spacing.</summary>
    Comfortable,

    /// <summary>Tighter spacing.</summary>
    Compact,
}

/// <summary>The <c>client.density</c> setting: its key and wire values.</summary>
public static class ClientDensitySettings
{
    /// <summary>The setting key.</summary>
    public const string Key = "client.density";

    /// <summary>The shipped default.</summary>
    public const ClientDensity Default = ClientDensity.Comfortable;

    /// <summary>The wire values, default first.</summary>
    public static readonly IReadOnlyList<string> Values = ["comfortable", "compact"];

    /// <summary>Parses a value (case-insensitive). Unknown or empty values give the default.</summary>
    public static ClientDensity Parse(string? value) =>
        string.Equals(value?.Trim(), "compact", StringComparison.OrdinalIgnoreCase) ? ClientDensity.Compact : Default;

    /// <summary>The wire value of a density.</summary>
    public static string Format(ClientDensity density) => density == ClientDensity.Compact ? "compact" : "comfortable";
}

/// <summary>The <c>client.theme</c> setting key. The bundled theme names live with the themes (<c>BundledThemes</c>).</summary>
public static class ClientThemeSettings
{
    /// <summary>The setting key.</summary>
    public const string Key = "client.theme";

    /// <summary>The value meaning "use the imported theme file" (loaded by the theme import feature, not here).</summary>
    public const string Imported = "imported";
}
