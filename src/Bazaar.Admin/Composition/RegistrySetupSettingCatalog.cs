using System.Text.Json;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Composition;

/// <summary>
/// Adapts the server's <see cref="SettingRegistry"/> to the narrow <see cref="ISetupSettingCatalog"/> that the setup library binds
/// to, so setup never references the server project. This is composition: the admin tool, as an executable root, may reference the
/// server and hands the registry across the seam.
/// </summary>
public sealed class RegistrySetupSettingCatalog : ISetupSettingCatalog
{
    private readonly SettingRegistry _registry;
    private readonly Dictionary<string, ISetupSettingDefinition> _byName;

    /// <summary>The catalog of the registry this build ships (<see cref="SettingRegistry.Known"/>).</summary>
    public static RegistrySetupSettingCatalog Known { get; } = new(SettingRegistry.Known);

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

/// <summary>Adapts the server's <see cref="ISettingAuditSink"/> to <see cref="ISetupSettingAudit"/>: each plan change is one accepted install-scope row.</summary>
public sealed class SettingAuditSinkSetupAudit(ISettingAuditSink sink) : ISetupSettingAudit
{
    private readonly ISettingAuditSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <inheritdoc />
    public Task AppendInstallChangeAsync(string key, string actorId, string? oldValueJson, string newValueJson, CancellationToken cancellationToken = default) =>
        _sink.AppendAsync(
            new SettingChangeAudit(key, SettingScope.Install, string.Empty, actorId, SettingWriteOutcome.Accepted, oldValueJson, newValueJson, null),
            cancellationToken);
}
