using Bazaar.Conduit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Conduit.Tests.Support;

/// <summary>Settings backed by a dictionary. Missing keys return the default.</summary>
internal sealed class DictionarySettings(IDictionary<string, object> values) : IConduitSettings
{
    public T Read<T>(string key, T defaultValue) =>
        values.TryGetValue(key, out var v) && v is T typed ? typed : defaultValue;
}

/// <summary>Builds a reachability context with a fixed hash and a fake clock.</summary>
internal static class Contexts
{
    public const string KeyHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    public static ConduitContext Create(TimeProvider? time = null, IDictionary<string, object>? settings = null, int port = 7443) =>
        new(port, KeyHash, new DictionarySettings(settings ?? new Dictionary<string, object>()), time ?? TimeProvider.System, NullLogger.Instance);
}

/// <summary>A module that publishes a fixed offer and can be told to fail. Used by registry tests.</summary>
internal sealed class FakeModule(string id, int priority, bool defaultEnabled = false, ConduitOffer? offer = null, bool failStart = false) : IConduitModule
{
    private ModuleStatus _status = new(id, ConduitModuleState.Disabled, null, []);

    public string Id { get; } = id;
    public string DisplayName => $"Fake {Id}";
    public ConduitScope Scope { get; } = ConduitScope.Lan;
    public int Priority { get; } = priority;
    public bool DefaultEnabled { get; } = defaultEnabled;
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public ModuleStatus Status => _status;
    public event EventHandler<ModuleStatus>? StatusChanged;

    public ValueTask StartAsync(ConduitContext context, CancellationToken cancellationToken)
    {
        Started = true;
        if (failStart)
        {
            throw new InvalidOperationException("boom");
        }

        var offers = offer is null ? (IReadOnlyList<ConduitOffer>)[] : [offer];
        _status = new ModuleStatus(Id, ConduitModuleState.Running, null, offers);
        StatusChanged?.Invoke(this, _status);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Stopped = true;
        _status = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
        return ValueTask.CompletedTask;
    }
}
