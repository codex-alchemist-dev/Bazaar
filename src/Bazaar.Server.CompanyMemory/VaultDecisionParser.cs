using Bazaar.Server.Knowledge.CompanyMemory;
using Bazaar.Server.Vault.Markdown;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Bazaar.Server.CompanyMemory;

/// <summary>
/// Reads a Vault file into a <see cref="DecisionRecord"/>. Typed fields come from <see cref="VaultFrontMatterReader"/> so the
/// Vault's own rules apply; the chain keys (<c>supersedes</c>, <c>superseded_by</c>, <c>decidedIn</c>) are read from the same
/// YAML block, because the Vault's typed front matter does not carry them.
/// </summary>
internal static class VaultDecisionParser
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder().WithDuplicateKeyChecking().Build();

    /// <summary>Null when the file has no front matter or is not a decision. Throws <see cref="VaultFormatException"/> when invalid.</summary>
    public static DecisionRecord? TryParse(string path, string source)
    {
        var split = FrontMatterSplitter.Split(source);
        if (split.Yaml is null)
        {
            return null;
        }

        var typed = VaultFrontMatterReader.Read(split.Yaml);
        if (typed.Type != "decision")
        {
            return null;
        }

        Dictionary<string, object?> fields;
        try
        {
            fields = _deserializer.Deserialize<Dictionary<string, object?>>(split.Yaml) ?? [];
        }
        catch (YamlException exception)
        {
            throw new VaultFormatException($"Malformed front matter YAML in {path}: {exception.Message}");
        }

        return new DecisionRecord(
            path,
            typed.Status ?? throw new VaultFormatException($"Decision {path} has no status."),
            ReadList(fields, "supersedes", path),
            ReadText(fields, "superseded_by", path),
            ReadText(fields, "decidedIn", path));
    }

    private static List<string> ReadList(Dictionary<string, object?> fields, string key, string path)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is not List<object?> items)
        {
            throw new VaultFormatException($"'{key}' in {path} must be a list.");
        }

        return [.. items.Select(item => item is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new VaultFormatException($"'{key}' in {path} must hold non-blank strings."))];
    }

    private static string? ReadText(Dictionary<string, object?> fields, string key, string path)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var text = value as string ?? throw new VaultFormatException($"'{key}' in {path} must be a string.");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
