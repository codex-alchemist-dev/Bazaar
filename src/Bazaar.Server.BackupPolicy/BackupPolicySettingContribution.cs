using Bazaar.Server.Settings;

namespace Bazaar.Server.BackupPolicy;

/// <summary>The backup policy module's contribution to the settings registry (doc 03). The host composes it with the built-in keys.</summary>
public sealed class BackupPolicySettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => BackupPolicySettings.All;
}
