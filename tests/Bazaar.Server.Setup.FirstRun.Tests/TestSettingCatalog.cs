using System.Text.Json;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Setup.FirstRun.Tests;

/// <summary>
/// A settings catalog with only the keys the loader insists on (the host status bind, port and shutdown timeout). The plans these
/// tests load set none of them, so every value is accepted.
/// </summary>
internal sealed class TestSettingCatalog : ISetupSettingCatalog
{
    private static readonly string[] _names = ["host.status.bind", "host.status.port", "host.shutdown_timeout_seconds"];

    private readonly Dictionary<string, ISetupSettingDefinition> _byName =
        _names.ToDictionary(name => name, name => (ISetupSettingDefinition)new Definition(name), StringComparer.Ordinal);

    public static TestSettingCatalog Instance { get; } = new();

    public IReadOnlyCollection<ISetupSettingDefinition> Definitions => _byName.Values;

    public bool TryGet(string name, out ISetupSettingDefinition? definition) => _byName.TryGetValue(name, out definition);

    private sealed class Definition(string name) : ISetupSettingDefinition
    {
        public string Name => name;

        public bool IsInstallScope => true;

        public Type ValueType => typeof(object);

        public string? CheckValue(JsonElement value) => null;

        public SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed) => new(true, null, proposed.GetRawText());
    }
}
