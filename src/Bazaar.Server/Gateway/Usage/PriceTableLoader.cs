using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// Loads a dated price table from JSON. Unknown properties are rejected, so a typo in a rate cannot be silently ignored.
/// Dates are <c>yyyy-MM-dd</c> UTC. Repricing adds a new dated entry; it never edits an old one.
/// </summary>
public static class PriceTableLoader
{
    /// <summary>Parses the table. Throws <see cref="InvalidDataException"/> for malformed JSON, bad dates or invalid prices.</summary>
    public static PriceTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        PriceTableFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, PriceTableJsonContext.Default.PriceTableFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The price table is not valid JSON for this format.", ex);
        }

        if (file is null)
        {
            throw new InvalidDataException("The price table is empty.");
        }

        try
        {
            return new PriceTable(file.Version, file.Entries.Select(ToEntry).ToList());
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static PriceEntry ToEntry(PriceEntryFile row)
    {
        if (!DateOnly.TryParseExact(row.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveFrom))
        {
            throw new InvalidDataException($"Effective date '{row.EffectiveFrom}' for {row.Provider}:{row.Model} must be yyyy-MM-dd.");
        }

        try
        {
            return new PriceEntry(
                row.Provider,
                row.Model,
                effectiveFrom,
                row.InputPerMillion,
                row.OutputPerMillion,
                row.CacheReadPerMillion,
                row.CacheWrite5mPerMillion,
                row.CacheWrite1hPerMillion);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }
}

/// <summary>The top-level price file: a version label and its dated entries.</summary>
public sealed class PriceTableFile
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("entries")]
    public required List<PriceEntryFile> Entries { get; init; }
}

/// <summary>One dated price row, in USD per million tokens. Cache rates are optional and take the documented defaults when absent.</summary>
public sealed class PriceEntryFile
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("effective_from")]
    public required string EffectiveFrom { get; init; }

    [JsonPropertyName("input_per_mtok")]
    public required decimal InputPerMillion { get; init; }

    [JsonPropertyName("output_per_mtok")]
    public required decimal OutputPerMillion { get; init; }

    [JsonPropertyName("cache_read_per_mtok")]
    public decimal? CacheReadPerMillion { get; init; }

    [JsonPropertyName("cache_write_5m_per_mtok")]
    public decimal? CacheWrite5mPerMillion { get; init; }

    [JsonPropertyName("cache_write_1h_per_mtok")]
    public decimal? CacheWrite1hPerMillion { get; init; }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PriceTableFile))]
internal sealed partial class PriceTableJsonContext : JsonSerializerContext;
