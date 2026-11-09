using Bazaar.Server.Transports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Transports.Tests.Support;

/// <summary>Settings backed by a dictionary. Missing keys return the default.</summary>
internal sealed class DictionarySettings(IDictionary<string, object> values) : ITransportSettings
{
    public T Read<T>(string key, T defaultValue) =>
        values.TryGetValue(key, out var v) && v is T typed ? typed : defaultValue;
}

/// <summary>Builds a reachability context with a fixed hash and a fake clock.</summary>
internal static class Contexts
{
    public const string KeyHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    public static ReachabilityContext Create(TimeProvider? time = null, IDictionary<string, object>? settings = null, int port = 7443) =>
        new(port, KeyHash, new DictionarySettings(settings ?? new Dictionary<string, object>()), time ?? TimeProvider.System, NullLogger.Instance);
}

/// <summary>A module that publishes a fixed offer and can be told to fail. Used by registry tests.</summary>
internal sealed class FakeModule(string id, int priority, bool defaultEnabled = false, TransportOffer? offer = null, bool failStart = false) : ITransportModule
{
    private ModuleStatus _status = new(id, TransportModuleState.Disabled, null, []);

    public string Id { get; } = id;
    public string DisplayName => $"Fake {Id}";
    public TransportScope Scope { get; } = TransportScope.Lan;
    public int Priority { get; } = priority;
    public bool DefaultEnabled { get; } = defaultEnabled;
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public ModuleStatus Status => _status;
    public event EventHandler<ModuleStatus>? StatusChanged;

    public ValueTask StartAsync(ReachabilityContext context, CancellationToken cancellationToken)
    {
        Started = true;
        if (failStart)
        {
            throw new InvalidOperationException("boom");
        }

        var offers = offer is null ? (IReadOnlyList<TransportOffer>)[] : [offer];
        _status = new ModuleStatus(Id, TransportModuleState.Running, null, offers);
        StatusChanged?.Invoke(this, _status);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Stopped = true;
        _status = new ModuleStatus(Id, TransportModuleState.Disabled, null, []);
        return ValueTask.CompletedTask;
    }
}
