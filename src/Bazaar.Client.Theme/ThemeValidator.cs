using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Client.Theme;

/// <summary>
/// Parses and validates a theme file. Pure: no Avalonia types and no I/O.
/// Rules: schemaVersion 1 (migrated to 2 by filling defaults) or 2; known top-level fields only; every required token present with the right kind and
/// no unknown tokens; contrast pairs (<see cref="ThemeTokens.ContrastPairs"/>) opaque and at least 4.5:1;
/// no URL, markup, script or remote-font content in any string (names and keys included).
/// </summary>
public static partial class ThemeValidator
{
    private const int _maxInputLength = 256 * 1024;

    private static readonly string[] _forbiddenFragments =
    [
        "//", "www.", "url(", "@import", "javascript:", "vbscript:", "data:", "expression(", "<", ">", "{", "}", "\\",
    ];

    private static readonly HashSet<string> _topLevelFields = new(StringComparer.Ordinal) { "schemaVersion", "name", "base", "tokens" };

    /// <summary>Loads and validates a theme file. Never throws on bad input; problems come back as issues.</summary>
    public static ThemeLoadResult Validate(string json)
    {
        var issues = new List<ThemeIssue>();
        if (json.Length > _maxInputLength)
        {
            issues.Add(new ThemeIssue("", "too-large", $"Theme file is larger than {_maxInputLength} characters."));
            return Reject(issues);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 8,
            });
        }
        catch (JsonException ex)
        {
            issues.Add(new ThemeIssue("", "json-syntax", ex.Message));
            return Reject(issues);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ThemeIssue("", "not-object", "Theme file must be a JSON object."));
                return Reject(issues);
            }

            ScanText(root, "", issues);
            CheckDuplicateKeys(root, "", issues);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!_topLevelFields.Contains(property.Name))
                {
                    issues.Add(new ThemeIssue("/" + property.Name, "unknown-field", "Unknown top-level field."));
                }
            }

            int? version = CheckSchemaVersion(root, issues);
            string? name = CheckName(root, issues);
            ThemeBase? polarity = CheckBase(root, issues);
            Dictionary<string, string> values = CheckTokens(root, version ?? ThemeTokens.SchemaVersion, issues);
            if (version == 1 && polarity is not null && issues.Count == 0)
            {
                ThemeDefaults.Fill(values, polarity.Value); // migration: a version 1 file gains the version 2 tokens
            }

            CheckContrast(values, issues);
            CheckHues(values, issues);
            CheckMaterial(values, issues);
            CheckMotionFloor(values, issues);

            if (issues.Count > 0 || name is null || polarity is null)
            {
                return Reject(issues);
            }

            var ordered = ThemeTokens.Required
                .Where(spec => values.ContainsKey(spec.Name))
                .ToDictionary(spec => spec.Name, spec => values[spec.Name], StringComparer.Ordinal);
            return new ThemeLoadResult(
                new ThemeDefinition(ThemeTokens.SchemaVersion, name, polarity.Value, ordered),
                []);
        }
    }

    /// <summary>WCAG 2.x contrast ratio between two opaque colours given as #RRGGBB (alpha, if present, is ignored).</summary>
    public static double ContrastRatio(string first, string second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>WCAG relative luminance of a #RRGGBB or #RRGGBBAA colour.</summary>
    public static double RelativeLuminance(string hex)
    {
        int r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int b = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return 0.2126 * Linearise(r) + 0.7152 * Linearise(g) + 0.0722 * Linearise(b);
    }

    [GeneratedRegex("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex ColourPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9 _-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex FontFamilyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.()-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private static ThemeLoadResult Reject(List<ThemeIssue> issues) => new(null, issues);

    private static double Linearise(int channel)
    {
        double s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static int? CheckSchemaVersion(JsonElement root, List<ThemeIssue> issues)
    {
        if (!root.TryGetProperty("schemaVersion", out JsonElement value))
        {
            issues.Add(new ThemeIssue("/schemaVersion", "missing-field", "schemaVersion is required."));
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int version)
            || version < ThemeTokens.MinSchemaVersion || version > ThemeTokens.SchemaVersion)
        {
            issues.Add(new ThemeIssue(
                "/schemaVersion",
                "unsupported-schema-version",
                $"schemaVersion must be {ThemeTokens.MinSchemaVersion} to {ThemeTokens.SchemaVersion}."));
            return null;
        }

        return version;
    }

    private static string? CheckName(JsonElement root, List<ThemeIssue> issues)
    {
        if (!TryReadString(root, "name", issues, out string? name))
        {
            return null;
        }

        if (!NamePattern().IsMatch(name))
        {
            issues.Add(new ThemeIssue("/name", "bad-name", "Name must be 1 to 64 characters: a letter or digit, then letters, digits, spaces and _ . ( ) -."));
            return null;
        }

        return name;
    }

    private static ThemeBase? CheckBase(JsonElement root, List<ThemeIssue> issues)
    {
        if (!TryReadString(root, "base", issues, out string? value))
        {
            return null;
        }

        switch (value)
        {
            case "light":
                return ThemeBase.Light;
            case "dark":
                return ThemeBase.Dark;
            default:
                issues.Add(new ThemeIssue("/base", "bad-base", "base must be \"light\" or \"dark\"."));
                return null;
        }
    }

    private static Dictionary<string, string> CheckTokens(JsonElement root, int version, List<ThemeIssue> issues)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("tokens", out JsonElement tokens))
        {
            issues.Add(new ThemeIssue("/tokens", "missing-field", "tokens is required."));
            return values;
        }

        if (tokens.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ThemeIssue("/tokens", "wrong-type", "tokens must be an object."));
            return values;
        }

        CheckDuplicateKeys(tokens, "/tokens", issues);
        TokenSpec[] expected = [.. ThemeTokens.For(version)];
        var known = expected.ToDictionary(spec => spec.Name, StringComparer.Ordinal);
        foreach (JsonProperty property in tokens.EnumerateObject())
        {
            string path = "/tokens/" + property.Name;
            if (!known.TryGetValue(property.Name, out TokenSpec? spec))
            {
                issues.Add(new ThemeIssue(path, "unknown-token", "Unknown token; only the token list of this schemaVersion is accepted."));
                continue;
            }

            string? canonical = CanonicalValue(spec, property.Value, path, issues);
            if (canonical is not null)
            {
                values[spec.Name] = canonical;
            }
        }

        foreach (TokenSpec spec in expected)
        {
            if (!tokens.TryGetProperty(spec.Name, out _))
            {
                issues.Add(new ThemeIssue("/tokens/" + spec.Name, "missing-token", "Required token is missing."));
            }
        }

        return values;
    }

    private static string? CanonicalValue(TokenSpec spec, JsonElement value, string path, List<ThemeIssue> issues)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string? canonical = spec.Kind switch
        {
            TokenKind.Colour => text is not null && ColourPattern().IsMatch(text) ? text.ToUpperInvariant() : null,
            TokenKind.FontFamily => text is not null && FontFamilyPattern().IsMatch(text) ? text : null,
            TokenKind.Length => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n is >= 0 and <= ThemeTokens.MaxLength
                ? n.ToString(CultureInfo.InvariantCulture)
                : null,
            TokenKind.Flag => value.ValueKind == JsonValueKind.True ? "true" : value.ValueKind == JsonValueKind.False ? "false" : null,
            TokenKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d)
                && double.IsFinite(d) && d >= spec.Min && d <= spec.Max
                    ? d.ToString("0.###", CultureInfo.InvariantCulture)
                    : null,
            TokenKind.Duration => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int ms) && ms is >= 0 and <= ThemeTokens.MaxDurationMs
                ? ms.ToString(CultureInfo.InvariantCulture)
                : null,
            TokenKind.Choice => text is not null && spec.Choices is not null && spec.Choices.Contains(text, StringComparer.Ordinal) ? text : null,
            _ => null,
        };

        if (canonical is null)
        {
            issues.Add(new ThemeIssue(path, "bad-value", Expected(spec)));
        }

        return canonical;
    }

    private static string Expected(TokenSpec spec) => spec.Kind switch
    {
        TokenKind.Number => $"Expected a number from {spec.Min.ToString(CultureInfo.InvariantCulture)} to {spec.Max.ToString(CultureInfo.InvariantCulture)}.",
        TokenKind.Duration => $"Expected a whole number of milliseconds from 0 to {ThemeTokens.MaxDurationMs}.",
        TokenKind.Choice => $"Expected one of: {string.Join(", ", spec.Choices ?? [])}.",
        TokenKind.Colour => "Expected #RRGGBB or #RRGGBBAA.",
        TokenKind.FontFamily => "Expected a font family name: letters, digits, spaces, underscores and hyphens; no URLs.",
        TokenKind.Length => $"Expected an integer from 0 to {ThemeTokens.MaxLength} (pixels).",
        TokenKind.Flag => "Expected true or false.",
        _ => "Unexpected value.",
    };

    private static void CheckContrast(Dictionary<string, string> colours, List<ThemeIssue> issues)
    {
        foreach (ContrastPair pair in ThemeTokens.ContrastPairs)
        {
            if (!colours.TryGetValue(pair.Foreground, out string? foreground) || !colours.TryGetValue(pair.Background, out string? background))
            {
                continue; // a missing or invalid token is already reported
            }

            if (foreground.Length != 7 || background.Length != 7)
            {
                string opaque = foreground.Length != 7 ? pair.Foreground : pair.Background;
                issues.Add(new ThemeIssue("/tokens/" + opaque, "contrast-alpha", "Contrast-checked colours must be opaque #RRGGBB."));
                continue;
            }

            double ratio = ContrastRatio(foreground, background);
            if (ratio < ThemeTokens.MinimumContrastRatio)
            {
                issues.Add(new ThemeIssue(
                    "/tokens/" + pair.Foreground,
                    "contrast",
                    $"Contrast {ratio.ToString("F2", CultureInfo.InvariantCulture)}:1 against {pair.Background} is below {ThemeTokens.MinimumContrastRatio.ToString("F1", CultureInfo.InvariantCulture)}:1."));
            }
        }
    }

    /// <summary>Each hue must pass 4.5:1 as text (and so 3:1 as a graphic) on the base, surface and editor backgrounds.</summary>
    private static void CheckHues(Dictionary<string, string> values, List<ThemeIssue> issues)
    {
        for (int i = 0; i < ThemeTokens.HueCount; i++)
        {
            string token = ThemeTokens.HueToken(i);
            if (!values.TryGetValue(token, out string? hue))
            {
                continue;
            }

            if (hue.Length != 7)
            {
                issues.Add(new ThemeIssue("/tokens/" + token, "contrast-alpha", "Contrast-checked colours must be opaque #RRGGBB."));
                continue;
            }

            foreach (string background in ThemeTokens.HueBackgrounds)
            {
                if (!values.TryGetValue(background, out string? surface) || surface.Length != 7)
                {
                    continue; // reported elsewhere
                }

                double ratio = ContrastRatio(hue, surface);
                if (ratio < ThemeTokens.MinimumContrastRatio)
                {
                    issues.Add(new ThemeIssue(
                        "/tokens/" + token,
                        "contrast",
                        $"Contrast {ratio.ToString("F2", CultureInfo.InvariantCulture)}:1 against {background} is below {ThemeTokens.MinimumContrastRatio.ToString("F1", CultureInfo.InvariantCulture)}:1."));
                }
            }
        }
    }

    /// <summary>
    /// Text on a translucent material is checked against the worst-case backdrop: the tint at the material opacity composited
    /// over pure black and over pure white (the darkest and brightest wallpaper). The opaque fallback colours are the floor:
    /// they are what shows when translucency is off, so they must carry every text colour too.
    /// </summary>
    private static void CheckMaterial(Dictionary<string, string> values, List<ThemeIssue> issues)
    {
        foreach (string fallback in new[] { "material.fallback.chrome", "material.fallback.sheet" })
        {
            if (!values.TryGetValue(fallback, out string? colour))
            {
                continue;
            }

            if (colour.Length != 7)
            {
                issues.Add(new ThemeIssue("/tokens/" + fallback, "contrast-alpha", "The opaque fallback must be #RRGGBB."));
                continue;
            }

            foreach (string text in ThemeTokens.MaterialTextTokens)
            {
                if (values.TryGetValue(text, out string? foreground) && foreground.Length == 7)
                {
                    AddIfLow(issues, "/tokens/" + fallback, "fallback-contrast", text, foreground, colour, null);
                }
            }
        }

        if (!values.TryGetValue("material.mode", out string? mode) || mode == "opaque"
            || !values.TryGetValue("material.tint", out string? tint)
            || !values.TryGetValue("material.opacity", out string? opacityText))
        {
            return;
        }

        if (tint.Length != 7)
        {
            issues.Add(new ThemeIssue("/tokens/material.tint", "contrast-alpha", "The material tint must be #RRGGBB; its opacity is material.opacity."));
            return;
        }

        double opacity = double.Parse(opacityText, CultureInfo.InvariantCulture);
        foreach (string backdrop in new[] { "#000000", "#FFFFFF" })
        {
            string composite = Composite(tint, opacity, backdrop);
            foreach (string text in ThemeTokens.MaterialTextTokens)
            {
                if (values.TryGetValue(text, out string? foreground) && foreground.Length == 7)
                {
                    AddIfLow(issues, "/tokens/material.tint", "material-contrast", text, foreground, composite, backdrop);
                }
            }
        }
    }

    private static void AddIfLow(List<ThemeIssue> issues, string path, string code, string textToken, string foreground, string background, string? backdrop)
    {
        double ratio = ContrastRatio(foreground, background);
        if (ratio < ThemeTokens.MinimumContrastRatio)
        {
            string where = backdrop is null ? "the opaque fallback" : $"the material over a {backdrop} backdrop";
            issues.Add(new ThemeIssue(
                path,
                code,
                $"{textToken} has contrast {ratio.ToString("F2", CultureInfo.InvariantCulture)}:1 on {where}, below {ThemeTokens.MinimumContrastRatio.ToString("F1", CultureInfo.InvariantCulture)}:1."));
        }
    }

    /// <summary>The tint at the given opacity over an opaque backdrop, as #RRGGBB.</summary>
    public static string Composite(string tint, double opacity, string backdrop)
    {
        int Channel(int offset)
        {
            double t = int.Parse(tint.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            double b = int.Parse(backdrop.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (int)Math.Round((t * opacity) + (b * (1.0 - opacity)), MidpointRounding.AwayFromZero);
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{Channel(1):X2}{Channel(3):X2}{Channel(5):X2}");
    }

    /// <summary>Safety floor (doc 93): the reduced-motion duration is never longer than 90 ms.</summary>
    private static void CheckMotionFloor(Dictionary<string, string> values, List<ThemeIssue> issues)
    {
        if (values.TryGetValue("motion.duration.reduced", out string? text)
            && int.Parse(text, CultureInfo.InvariantCulture) > ThemeTokens.ReducedDurationFloorMs)
        {
            issues.Add(new ThemeIssue(
                "/tokens/motion.duration.reduced",
                "motion-floor",
                $"The reduced-motion duration may not exceed {ThemeTokens.ReducedDurationFloorMs} ms."));
        }
    }

    private static bool TryReadString(JsonElement root, string field, List<ThemeIssue> issues, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!root.TryGetProperty(field, out JsonElement element))
        {
            issues.Add(new ThemeIssue("/" + field, "missing-field", $"{field} is required."));
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(new ThemeIssue("/" + field, "wrong-type", $"{field} must be a string."));
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    private static void CheckDuplicateKeys(JsonElement obj, string path, List<ThemeIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                issues.Add(new ThemeIssue(path + "/" + property.Name, "duplicate-key", "Key appears more than once."));
            }
        }
    }

    /// <summary>Scans every property name and string value, recursively, for URL, markup, script or remote-font fragments.</summary>
    private static void ScanText(JsonElement element, string path, List<ThemeIssue> issues)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                CheckText(element.GetString() ?? "", path, issues);
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string childPath = path + "/" + property.Name;
                    CheckText(property.Name, childPath, issues);
                    ScanText(property.Value, childPath, issues);
                }

                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ScanText(item, path + "/" + index.ToString(CultureInfo.InvariantCulture), issues);
                    index++;
                }

                break;
        }
    }

    private static void CheckText(string text, string path, List<ThemeIssue> issues)
    {
        string? fragment = _forbiddenFragments.FirstOrDefault(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (fragment is not null)
        {
            issues.Add(new ThemeIssue(path, "unsafe-content", $"Text contains \"{fragment}\". URLs, markup, scripts and remote fonts are not allowed."));
        }
    }
}
