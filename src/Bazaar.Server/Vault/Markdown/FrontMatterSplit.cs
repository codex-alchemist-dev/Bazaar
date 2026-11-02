namespace Bazaar.Server.Vault.Markdown;

/// <summary>
/// Where a document's front matter sits in its source. <see cref="Yaml"/> is null when the document has none.
/// <see cref="YamlStart"/> is the offset of the first YAML character; <see cref="BodyStart"/> is the offset of the first body character.
/// </summary>
public sealed record FrontMatterSplit(string? Yaml, int YamlStart, int BodyStart);
