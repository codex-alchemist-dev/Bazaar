using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Vms.Images;

/// <summary>Source-generated JSON metadata for golden image recipes (no reflection at runtime).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(GoldenImageRecipe))]
public sealed partial class GoldenImageJsonContext : JsonSerializerContext
{
}

/// <summary>Reads and writes golden image recipes as JSON.</summary>
public static class GoldenImageRecipeJson
{
    /// <summary>Parses a recipe. Malformed JSON or unknown fields throw <see cref="JsonException"/>; invalid values throw <see cref="ArgumentException"/>.</summary>
    public static GoldenImageRecipe Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var recipe = JsonSerializer.Deserialize(json, GoldenImageJsonContext.Default.GoldenImageRecipe)
            ?? throw new JsonException("The recipe JSON is empty.");
        return recipe;
    }

    /// <summary>Writes a recipe as indented JSON.</summary>
    public static string Save(GoldenImageRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        return JsonSerializer.Serialize(recipe, GoldenImageJsonContext.Default.GoldenImageRecipe);
    }
}
