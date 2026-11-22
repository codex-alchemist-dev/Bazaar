using System.Text.Json;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Setup.FirstRun.Tests;

/// <summary>A permissive settings catalog: every key exists and accepts any value. The plans these tests write carry no overrides.</summary>
internal sealed class NoSettings : ISetupSettingCatalog
{
    public static readonly NoSettings Instance = new();

    public IReadOnlyCollection<ISetupSettingDefinition> Definitions => [];

    public bool TryGet(string name, out ISetupSettingDefinition? definition)
    {
        definition = new Open(name);
        return true;
    }

    private sealed class Open(string name) : ISetupSettingDefinition
    {
        public string Name => name;

        public bool IsInstallScope => true;

        public Type ValueType => typeof(object);

        public string? CheckValue(JsonElement value) => null;

        public SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed) => new(true, null, proposed.GetRawText());
    }
}
