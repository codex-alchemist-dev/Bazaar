using Bazaar.Server.Settings;

namespace Bazaar.Server.Modules.Settings;

/// <summary>
/// The module manager's contribution to the settings registry (doc 03). <see cref="ModuleManagerComponent"/> registers it, and the
/// settings component composes it with the built-in keys, so every key in <see cref="ModuleManagerSettings.All"/> stays known.
/// </summary>
public sealed class ModuleManagerSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => ModuleManagerSettings.All;
}
