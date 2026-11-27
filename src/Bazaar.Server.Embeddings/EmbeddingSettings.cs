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

    /// <summary>
    /// Share of embeddable blocks the new profile must cover before a planned switch activates it (doc 08, "Planned profile switch"
    /// [PROPOSED]). Default 1.0: every embeddable block.
    /// </summary>
    public static readonly SettingKey<decimal> SwitchThresholdKey = new(
        "memory.embedding_switch_threshold", 1.0m, SettingScope.Install, SettingRole.Operator,
        "Share of embeddable Vault blocks (0 to 1) a new embedding profile must cover before it replaces the active one. 1 means all of them.",
        validate: value => value > 0m && value <= 1m ? null : "Use a number above 0 and at most 1.");

    public static IReadOnlyList<SettingDefinition> All { get; } = [EmbeddingProfileKey, SwitchThresholdKey];

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

/// <summary>Used until the host adapts its setting store: threshold 1.0, so a switch activates only at full coverage.</summary>
internal sealed class DefaultEmbeddingSwitchSettingSource : IEmbeddingSwitchSettingSource
{
    public Task<decimal> GetSwitchThresholdAsync(CancellationToken cancellationToken) => Task.FromResult(EmbeddingSettings.SwitchThresholdKey.Default);
}

/// <summary>Used until the host adapts its setting store: the default <c>none</c>, so the indexer stays idle.</summary>
internal sealed class DefaultEmbeddingSettingSource : IEmbeddingSettingSource
{
    public Task<string> GetEmbeddingProfileSettingAsync(CancellationToken cancellationToken) => Task.FromResult(EmbeddingSettings.None);
}
