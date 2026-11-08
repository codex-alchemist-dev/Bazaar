using Bazaar.Server.Vault.Markdown;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Bazaar.Server.Knowledge.CompanyMemory;

/// <summary>
/// Reads a Vault document into a <see cref="DecisionRecord"/>. The typed fields come from <see cref="VaultFrontMatterReader"/>
/// (the Vault's own parser, so id, type and status rules apply). <see cref="VaultFrontMatter"/> does not carry the chain
/// keys, so this reads <c>supersedes</c>, <c>superseded_by</c> and <c>decidedIn</c> from the same YAML block.
/// </summary>
public static class DecisionRecordReader
{
    private const string _decisionType = "decision";
    private const string _supersedesKey = "supersedes";
    private const string _supersededByKey = "superseded_by";
    private const string _decidedInKey = "decidedIn";

    private static readonly IDeserializer _deserializer = new DeserializerBuilder().WithDuplicateKeyChecking().Build();

    /// <summary>
    /// Returns the decision record for a document at <paramref name="path"/>, or null when the file has no front matter or
    /// its type is not <c>decision</c>. Throws <see cref="VaultFormatException"/> when the front matter is invalid.
    /// </summary>
    public static DecisionRecord? TryRead(string path, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(source);

        var split = FrontMatterSplitter.Split(source);
        if (split.Yaml is null)
        {
            return null;
        }

        var frontMatter = VaultFrontMatterReader.Read(split.Yaml);
        if (frontMatter.Type != _decisionType)
        {
            return null;
        }

        var fields = Deserialize(split.Yaml);
        return new DecisionRecord(
            Path: path,
            Status: frontMatter.Status ?? throw new VaultFormatException("Front matter 'status' is required for a decision."),
            Supersedes: ReadReferences(fields, _supersedesKey),
            SupersededBy: ReadOptionalText(fields, _supersededByKey),
            DecidedIn: ReadOptionalText(fields, _decidedInKey));
    }

    private static Dictionary<string, object?> Deserialize(string yaml)
    {
        try
        {
            return _deserializer.Deserialize<Dictionary<string, object?>>(yaml) ?? [];
        }
        catch (YamlException exception)
        {
            throw new VaultFormatException($"Malformed front matter YAML: {exception.Message}");
        }
    }

    /// <summary>A list of references. Absent or null gives an empty list; a blank entry is refused.</summary>
    private static List<string> ReadReferences(Dictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? value) || value is null)
        {
            return [];
        }

        if (value is not List<object?> items)
        {
            throw new VaultFormatException($"Front matter '{key}' must be a list of decision references.");
        }

        var references = new List<string>(items.Count);
        foreach (object? item in items)
        {
            string text = item as string ?? throw new VaultFormatException($"Front matter '{key}' entries must be strings.");
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new VaultFormatException($"Front matter '{key}' must not contain a blank entry.");
            }

            references.Add(text);
        }

        return references;
    }

    /// <summary>An optional string: absent, null or blank gives null.</summary>
    private static string? ReadOptionalText(Dictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        string text = value as string ?? throw new VaultFormatException($"Front matter '{key}' must be a string.");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
