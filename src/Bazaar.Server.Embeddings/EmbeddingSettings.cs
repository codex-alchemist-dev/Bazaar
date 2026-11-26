using System.Text.RegularExpressions;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Embeddings;

/// <summary>
/// The <c>memory.embedding_profile</c> setting (doc 08, settings table). <c>none</c> means lexical only; any other value is the
/// id of a configured provider and must match the active <c>embedding_profile</c> row. Changing it after data exists is a
/// planned re-embed.
/// </summary>
public static partial class EmbeddingSettings
{
    public const string None = "none";

    public static readonly SettingKey<string> EmbeddingProfileKey = new(
        "memory.embedding_profile", None, SettingScope.Install, SettingRole.Operator,
        "Embedding provider for Vault and memory search: none (lexical only) or the id of a configured provider. One per install.",
        validate: value => value == None || IdPattern().IsMatch(value) ? null : "Use none or a provider id of lowercase letters, digits, dots, underscores or hyphens.");

    public static IReadOnlyList<SettingDefinition> All { get; } = [EmbeddingProfileKey];

    /// <summary>True when the stored value means lexical only.</summary>
    public static bool IsNone(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value, None, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}

/// <summary>Registers <see cref="EmbeddingSettings.All"/> with the settings registry. The host composes it with the built-in keys.</summary>
public sealed class EmbeddingSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => EmbeddingSettings.All;
}

/// <summary>Used until the host adapts its setting store: the default <c>none</c>, so the indexer stays idle.</summary>
internal sealed class DefaultEmbeddingSettingSource : IEmbeddingSettingSource
{
    public Task<string> GetEmbeddingProfileSettingAsync(CancellationToken cancellationToken) => Task.FromResult(EmbeddingSettings.None);
}
