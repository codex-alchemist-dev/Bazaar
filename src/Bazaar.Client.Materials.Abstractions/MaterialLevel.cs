namespace Bazaar.Client.Materials;

/// <summary>The person's choice in the <c>client.materials</c> setting.</summary>
public enum MaterialLevel
{
    /// <summary>Translucent where the platform allows it and nothing argues against it (the default).</summary>
    Auto,

    /// <summary>Force translucency past battery saver and a frame-budget miss. Reduce-transparency and a failed backdrop still win.</summary>
    Translucent,

    /// <summary>Always the opaque fallback.</summary>
    Opaque,
}

/// <summary>The <c>client.materials</c> setting: its key and its wire values.</summary>
public static class MaterialSettings
{
    /// <summary>The setting key.</summary>
    public const string Key = "client.materials";

    /// <summary>The shipped default.</summary>
    public const MaterialLevel Default = MaterialLevel.Auto;

    /// <summary>Parses a setting value (auto, translucent, opaque; case-insensitive). Unknown or empty values give the default.</summary>
    public static MaterialLevel Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "translucent" => MaterialLevel.Translucent,
        "opaque" => MaterialLevel.Opaque,
        _ => Default,
    };

    /// <summary>The setting value of a level.</summary>
    public static string Format(MaterialLevel level) => level switch
    {
        MaterialLevel.Translucent => "translucent",
        MaterialLevel.Opaque => "opaque",
        _ => "auto",
    };
}
