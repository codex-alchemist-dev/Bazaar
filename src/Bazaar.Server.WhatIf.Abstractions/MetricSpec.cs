using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.WhatIf;

/// <summary>Keys of the four metrics a What If run is scored on (docs/design/26-fun-systems.md section 3).</summary>
public static class MetricKeys
{
    public const string CiResult = "ci_result";
    public const string DiffSize = "diff_size";
    public const string ReviewBand = "review_band";
    public const string Tokens = "tokens";

    public static IReadOnlyList<string> All { get; } = [CiResult, DiffSize, ReviewBand, Tokens];
}

/// <summary>
/// The metrics a What If run is scored on, fixed before any branch runs. The spec is hashed first (<see cref="Hash"/>); a hash
/// that no longer matches aborts the run, so branches can never be scored on moving goalposts.
/// </summary>
/// <param name="Weights">Relative weight per metric key. All four keys of <see cref="MetricKeys"/> are required.</param>
/// <param name="DiffSizeCapLines">A diff at or above this many changed lines scores 0 on diff size.</param>
/// <param name="TokenCap">Token use at or above this scores 0 on tokens.</param>
public sealed record MetricSpec(IReadOnlyDictionary<string, decimal> Weights, int DiffSizeCapLines, long TokenCap)
{
    /// <summary>Bumped only if the canonical form changes; part of the hashed text.</summary>
    public const int CanonicalVersion = 1;

    /// <summary>A neutral default spec. Weights are a documented starting point, not a decided value (doc 26 does not set them).</summary>
    public static MetricSpec Default { get; } = new(
        new Dictionary<string, decimal>
        {
            [MetricKeys.CiResult] = 0.4m,
            [MetricKeys.DiffSize] = 0.2m,
            [MetricKeys.ReviewBand] = 0.3m,
            [MetricKeys.Tokens] = 0.1m,
        },
        DiffSizeCapLines: 1000,
        TokenCap: 1_000_000);

    /// <summary>Throws when the spec cannot score anything: a missing or unknown key, a negative weight, a zero total or a non-positive cap.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Weights);
        if (Weights.Count != MetricKeys.All.Count || MetricKeys.All.Any(k => !Weights.ContainsKey(k)))
            throw new ArgumentException($"A metric spec needs exactly the weights {string.Join(", ", MetricKeys.All)}.", nameof(Weights));
        if (Weights.Values.Any(w => w < 0))
            throw new ArgumentException("A metric weight cannot be negative.", nameof(Weights));
        if (Weights.Values.Sum() <= 0)
            throw new ArgumentException("At least one metric weight must be above zero.", nameof(Weights));
        if (DiffSizeCapLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(DiffSizeCapLines), "The diff size cap must be above zero.");
        if (TokenCap <= 0)
            throw new ArgumentOutOfRangeException(nameof(TokenCap), "The token cap must be above zero.");
    }

    /// <summary>
    /// Canonical JSON: fixed property order, weight keys sorted ordinally, decimals without trailing zeros, no whitespace. The same
    /// spec yields the same text whatever order its weights were supplied in.
    /// </summary>
    public string ToCanonicalJson()
    {
        Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("diff_size_cap_lines", DiffSizeCapLines);
            writer.WriteNumber("token_cap", TokenCap);
            writer.WriteNumber("version", CanonicalVersion);
            writer.WriteStartObject("weights");
            foreach (var key in Weights.Keys.Order(StringComparer.Ordinal))
                writer.WriteNumber(key, Normalise(Weights[key]));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Lowercase hex SHA-256 of <see cref="ToCanonicalJson"/>.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson())));

    /// <summary>Weight text for reports, without trailing zeros.</summary>
    public static string Format(decimal value) => Normalise(value).ToString(CultureInfo.InvariantCulture);

    // Dividing by 1.000...0m drops trailing zeros, so 0.40m and 0.4m serialise alike.
    private static decimal Normalise(decimal value) => value / 1.0000000000000000000000000000m;
}
