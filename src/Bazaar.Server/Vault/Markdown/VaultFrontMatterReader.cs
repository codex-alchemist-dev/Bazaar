using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Bazaar.Server.Vault.Markdown;

/// <summary>Parses the YAML of a front matter block into <see cref="VaultFrontMatter"/>. Owned by VLT1 (front-matter reader).</summary>
public static class VaultFrontMatterReader
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder().WithDuplicateKeyChecking().Build();

    private static readonly string[] _forbiddenTitleFragments = ["#", "|", "^", ":", "%%", "[[", "]]"];
    private static readonly string[] _decisionStatuses = ["proposed", "accepted", "superseded"];
    private static readonly string[] _tiers = ["T0", "T1", "T2", "T3"];

    /// <summary>Reads the front matter YAML. Unknown keys are ignored. Throws <see cref="VaultFormatException"/> on any rule violation.</summary>
    public static VaultFrontMatter Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        Dictionary<string, object?>? fields;
        try
        {
            fields = _deserializer.Deserialize<Dictionary<string, object?>>(yaml);
        }
        catch (YamlException exception)
        {
            throw new VaultFormatException($"Malformed front matter YAML: {exception.Message}");
        }

        fields ??= [];

        string id = RequireText(fields, "id");
        if (!IsUlid(id))
        {
            throw new VaultFormatException($"Front matter 'id' must be a 26-character ULID (Crockford base32), got '{id}'.");
        }

        string type = RequireText(fields, "type");
        if (!VaultFrontMatter.KnownTypes.Contains(type))
        {
            throw new VaultFormatException($"Front matter 'type' '{type}' is not a known Vault document type.");
        }

        string title = RequireText(fields, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new VaultFormatException("Front matter 'title' must not be empty.");
        }

        foreach (string fragment in _forbiddenTitleFragments)
        {
            if (title.Contains(fragment, StringComparison.Ordinal))
            {
                throw new VaultFormatException($"Front matter 'title' must not contain '{fragment}'.");
            }
        }

        string? status = OptionalText(fields, "status");
        if (type == "decision")
        {
            if (status is null)
            {
                throw new VaultFormatException("Front matter 'status' is required for a decision and must be proposed, accepted or superseded.");
            }

            if (!_decisionStatuses.Contains(status, StringComparer.Ordinal))
            {
                throw new VaultFormatException($"Front matter 'status' '{status}' for a decision must be proposed, accepted or superseded.");
            }
        }

        string? tier = OptionalText(fields, "tier");
        if (tier is not null && !_tiers.Contains(tier, StringComparer.Ordinal))
        {
            throw new VaultFormatException($"Front matter 'tier' '{tier}' must be T0, T1, T2 or T3.");
        }

        return new VaultFrontMatter(
            Id: id,
            Type: type,
            Title: title,
            Project: OptionalText(fields, "project"),
            Status: status,
            Aliases: ReadAliases(fields),
            Owner: OptionalText(fields, "owner"),
            SourceTrust: OptionalText(fields, "source_trust"),
            Tier: tier);
    }

    /// <summary>A Crockford base32 ULID: 26 characters from 0-9 and A-Z without I, L, O, U, case-insensitive.</summary>
    private static bool IsUlid(string value)
    {
        if (value.Length != 26)
        {
            return false;
        }

        foreach (char character in value)
        {
            char upper = char.ToUpperInvariant(character);
            bool valid = upper is >= '0' and <= '9'
                || upper is >= 'A' and <= 'Z' and not ('I' or 'L' or 'O' or 'U');
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A required string field. Throws when it is absent, null or not a scalar string.</summary>
    private static string RequireText(Dictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? value) || value is null)
        {
            throw new VaultFormatException($"Front matter '{key}' is missing.");
        }

        return value as string ?? throw new VaultFormatException($"Front matter '{key}' must be a string.");
    }

    /// <summary>An optional string field: null when absent, null, empty or whitespace.</summary>
    private static string? OptionalText(Dictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        string text = value as string ?? throw new VaultFormatException($"Front matter '{key}' must be a string.");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Aliases: absent or null gives an empty list; otherwise a sequence of strings.</summary>
    private static List<string> ReadAliases(Dictionary<string, object?> fields)
    {
        if (!fields.TryGetValue("aliases", out object? value) || value is null)
        {
            return [];
        }

        if (value is not List<object?> items)
        {
            throw new VaultFormatException("Front matter 'aliases' must be a sequence of strings.");
        }

        var aliases = new List<string>(items.Count);
        foreach (object? item in items)
        {
            aliases.Add(item as string ?? throw new VaultFormatException("Front matter 'aliases' must contain only strings."));
        }

        return aliases;
    }
}
