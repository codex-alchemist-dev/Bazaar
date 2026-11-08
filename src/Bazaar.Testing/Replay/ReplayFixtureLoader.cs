using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Testing.Replay;

/// <summary>A fixture that is malformed, unsupported, or not sanitized. The message names the source and the reason.</summary>
public sealed class ReplayFixtureException(string source, string reason, Exception? inner = null)
    : Exception($"Replay fixture '{source}' rejected: {reason}", inner);

/// <summary>
/// Reads replay fixtures. Unknown properties are errors, the sanitizer runs before any record is bound, and the
/// structure is checked: format, name, strictly rising seq, and exactly the structural keys each kind requires.
/// </summary>
public static class ReplayFixtureLoader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ReplayFixture Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Parses fixture JSON. <paramref name="source"/> names the fixture in errors (a file path or a test name).</summary>
    public static ReplayFixture Parse(string json, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(source);

        try
        {
            using var document = JsonDocument.Parse(json);
            ReplaySanitizer.EnsureClean(document.RootElement, source);
        }
        catch (JsonException ex)
        {
            throw new ReplayFixtureException(source, "not well-formed JSON", ex);
        }

        ReplayFixture? fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<ReplayFixture>(json, _options);
        }
        catch (JsonException ex)
        {
            throw new ReplayFixtureException(source, $"does not match the fixture schema: {ex.Message}", ex);
        }

        if (fixture is null)
        {
            throw new ReplayFixtureException(source, "the document is empty");
        }

        Validate(fixture, source);
        return fixture;
    }

    private static void Validate(ReplayFixture fixture, string source)
    {
        if (fixture.Format != ReplayFixture.CurrentFormat)
        {
            throw new ReplayFixtureException(source, $"format {fixture.Format} is not supported (expected {ReplayFixture.CurrentFormat})");
        }

        if (string.IsNullOrWhiteSpace(fixture.Name))
        {
            throw new ReplayFixtureException(source, "name is empty");
        }

        if (fixture.Records is null)
        {
            throw new ReplayFixtureException(source, "records is missing");
        }

        long previous = 0;
        for (var i = 0; i < fixture.Records.Count; i++)
        {
            var record = fixture.Records[i];
            var at = $"records[{i}]";

            if (record.Seq <= previous)
            {
                throw new ReplayFixtureException(source, $"{at}.seq {record.Seq} must be greater than {previous}");
            }

            previous = record.Seq;

            if (record.Keys is null || record.Keys.Values.Any(v => v is null))
            {
                throw new ReplayFixtureException(source, $"{at}.keys is missing or has a null value");
            }

            var required = record.Kind.StructuralKeys();
            var missing = required.Where(k => !record.Keys.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                throw new ReplayFixtureException(source, $"{at} ({record.Kind.WireName()}) lacks structural key(s): {string.Join(", ", missing)}");
            }

            var extra = record.Keys.Keys.Where(k => !required.Contains(k)).ToList();
            if (extra.Count > 0)
            {
                throw new ReplayFixtureException(source, $"{at} ({record.Kind.WireName()}) has keys that are not structural: {string.Join(", ", extra)}");
            }
        }
    }
}
