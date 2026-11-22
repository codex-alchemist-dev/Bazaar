using System.Text.Json;
using Bazaar.Server.Modules.Settings;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// Adapts the server's <see cref="SettingRegistry"/> to the narrow <see cref="ISetupSettingCatalog"/> that the setup library binds
/// to, so setup never references the server project. This is composition: the host and the admin tool, as executable roots, share this one
/// implementation and hand the registry across the seam.
/// </summary>
public sealed class RegistrySetupSettingCatalog : ISetupSettingCatalog
{
    private readonly SettingRegistry _registry;
    private readonly Dictionary<string, ISetupSettingDefinition> _byName;

    /// <summary>
    /// The catalog of the keys this build ships: the built-in registry plus the module manager's contribution (<c>update.channel</c>),
    /// the same composition the host's settings component builds, for roots that run without the host's service graph.
    /// </summary>
    public static RegistrySetupSettingCatalog Known { get; } = new(BuiltInSettingRegistries.WithContributions([new ModuleManagerSettingContribution()]));

    public RegistrySetupSettingCatalog(SettingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _byName = registry.Definitions.ToDictionary(d => d.Name, d => (ISetupSettingDefinition)new Definition(d), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ISetupSettingDefinition> Definitions => _byName.Values;

    /// <inheritdoc />
    public bool TryGet(string name, out ISetupSettingDefinition? definition) => _byName.TryGetValue(name, out definition);

    private sealed class Definition(SettingDefinition inner) : ISetupSettingDefinition
    {
        public string Name => inner.Name;

        public bool IsInstallScope => inner.Scope == SettingScope.Install;

        public Type ValueType => inner.ValueType;

        public string? CheckValue(JsonElement value) => inner.CheckValue(value);

        public SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed)
        {
            var evaluation = inner.Evaluate(new SettingActor(actorId, SettingRole.Operator), proposed);
            return new SetupSettingEvaluation(evaluation.Outcome == SettingWriteOutcome.Accepted, evaluation.Reason, evaluation.ValueJson);
        }
    }
}
