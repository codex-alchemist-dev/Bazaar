using Bazaar.Server.Settings;

namespace Bazaar.Server.Gateway;

/// <summary>
/// The gateway's contribution to the settings registry (doc 05). The host registers it, and the settings component composes it with
/// the built-in keys, so every key in <see cref="GatewaySettings.All"/> stays known. Bazaar.Server cannot reference this project.
/// </summary>
public sealed class GatewaySettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => GatewaySettings.All;
}
