using Bazaar.Server.Settings;

namespace Bazaar.Server.MailPolicy;

/// <summary>The mail policy module's contribution to the settings registry (doc 03). The host composes it with the built-in keys.</summary>
public sealed class MailPolicySettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => MailPolicySettings.All;
}
