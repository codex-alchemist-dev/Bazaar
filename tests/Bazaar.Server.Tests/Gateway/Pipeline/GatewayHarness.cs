using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Tests.Gateway.Policy;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.Pipeline;

/// <summary>Wraps a <see cref="FakeProvider"/> as an adapter and remembers the endpoints it was called with.</summary>
internal sealed class ScriptedAdapter(FakeProvider provider) : IModelAdapter
{
    public FakeProvider Provider { get; } = provider;

    public List<ProviderEndpoint> Endpoints { get; } = [];

    public AdapterKind Kind => AdapterKind.OpenAiChat;

    public IAsyncEnumerable<ModelEvent> StreamAsync(ProviderEndpoint endpoint, ModelRequest request, CancellationToken ct)
    {
        Endpoints.Add(endpoint);
        return Provider.StreamAsync(request, ct);
    }
}

/// <summary>An adapter that throws, with a message that carries a value the gateway must not leak.</summary>
internal sealed class ThrowingAdapter(string message) : IModelAdapter
{
    public AdapterKind Kind => AdapterKind.OpenAiChat;

    public IAsyncEnumerable<ModelEvent> StreamAsync(ProviderEndpoint endpoint, ModelRequest request, CancellationToken ct) =>
        throw new InvalidOperationException(message);
}

internal sealed class FakeQuota : IQuotaTracker
{
    public Dictionary<(ProviderKey Key, string Class), (QuotaState State, DateTimeOffset? Until)> Blocked { get; } = [];

    public List<(ProviderKey Key, string Class, ModelEvent.Failure Failure)> Reports { get; } = [];

    public QuotaState? BlockedState(ProviderKey key, string modelClass) =>
        Blocked.TryGetValue((key, modelClass), out var b) ? b.State : null;

    public DateTimeOffset? BlockedUntil(ProviderKey key, string modelClass) =>
        Blocked.TryGetValue((key, modelClass), out var b) ? b.Until : null;

    public void Report(ProviderKey key, string modelClass, ModelEvent.Failure failure) => Reports.Add((key, modelClass, failure));
}

internal sealed class RecordingSink : IGatewayEventSink
{
    public List<GatewayQuotaEvent> Events { get; } = [];

    public void Publish(GatewayQuotaEvent e) => Events.Add(e);
}

internal sealed class RecordingSuspender : IRouteSuspender
{
    public List<(string RouteId, IReadOnlyList<string> Violations)> Calls { get; } = [];

    public ValueTask SuspendAsync(string routeId, IReadOnlyList<string> violations, CancellationToken ct)
    {
        Calls.Add((routeId, violations));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSecrets : IProviderSecretSource
{
    public const string Sentinel = "PLANTED-SENTINEL-VALUE";

    public Dictionary<string, string?> Values { get; } = [];

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct) =>
        ValueTask.FromResult(Values.TryGetValue(secretRef, out var v) ? v : Sentinel);
}

/// <summary>The directory the gateway sees. The resolver reads the world directly, so a test can change a row after resolution.</summary>
internal sealed class MutableDirectory(InMemoryPolicyWorld world) : IGatewayDirectory
{
    public Func<GwProvider, GwProvider>? ProviderOverride { get; set; }

    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(world.Providers.GetValueOrDefault(providerId) is { } p ? ProviderOverride?.Invoke(p) ?? p : null);

    public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) => world.GetModelAsync(model, cancellationToken);

    public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) => world.GetRouteAsync(routeId, cancellationToken);
}

/// <summary>
/// Providers <c>a</c>, <c>b</c> (and <c>c</c>, outside the route) with one model <c>m</c> each, route <c>r</c> = a, b,
/// and agent <c>charles</c> allowed a:m, b:m and r. Each provider has its own scripted adapter, named after it.
/// </summary>
internal sealed class GatewayHarness
{
    public const string Agent = "charles";

    public GatewayHarness(int breakerThreshold = 5)
    {
        Clock = new FakeClock();
        World = new InMemoryPolicyWorld();
        Directory = new MutableDirectory(World);
        Usage = new InMemoryUsageStore();
        Health = new HealthTracker(new BreakerOptions { FailureThreshold = breakerThreshold }, Clock);
        Quota = new FakeQuota();
        Sink = new RecordingSink();
        Suspender = new RecordingSuspender();
        Secrets = new FakeSecrets();

        foreach (var id in new[] { "a", "b", "c" })
        {
            AddProvider(id);
        }

        World.AddRoute("r", ["a:m", "b:m"]);
        World.AddPolicy(Agent, ["a:m", "b:m", "r"], "r");
    }

    public FakeClock Clock { get; }

    public InMemoryPolicyWorld World { get; }

    public MutableDirectory Directory { get; }

    public InMemoryUsageStore Usage { get; }

    public HealthTracker Health { get; }

    public FakeQuota Quota { get; }

    public RecordingSink Sink { get; }

    public RecordingSuspender Suspender { get; }

    public FakeSecrets Secrets { get; }

    public Dictionary<string, IModelAdapter> Adapters { get; } = [];

    public List<Exception> InternalErrors { get; } = [];

    public ScriptedAdapter Adapter(string id) => (ScriptedAdapter)Adapters[id];

    public static ProviderKey KeyOf(string id) => new(id, SecretRef(id));

    public static string SecretRef(string id) => $"secret://bazaar/providers/{id}/key";

    /// <summary>Adds an enabled provider with a scripted adapter and one available model <c>m</c> of family <c>claude</c>.</summary>
    public GatewayHarness AddProvider(string id, string authKind = "api_key")
    {
        var adapter = new ScriptedAdapter(new FakeProvider(Clock));
        Adapters[id] = adapter;
        World.AddProvider(id, authKind, secretRef: SecretRef(id));
        World.Providers[id] = World.Providers[id] with { Adapter = id };
        World.AddModel(id, "m");
        return this;
    }

    public ModelGateway Gateway(PriceTable? prices = null) => new(
        World.Resolver(),
        Directory,
        new DictionaryAdapterRegistry(Adapters),
        Secrets,
        Health,
        new UsageRecorder(Usage),
        prices,
        Quota,
        Sink,
        Suspender,
        Clock,
        InternalErrors.Add);

    public static ModelRequest Request(string route = "r") => InMemoryPolicyWorld.Request(Agent, route);

    public static async Task<List<ModelEvent>> CollectAsync(IAsyncEnumerable<ModelEvent> stream, CancellationToken ct = default)
    {
        var events = new List<ModelEvent>();
        await foreach (var e in stream.WithCancellation(ct))
        {
            events.Add(e);
        }

        return events;
    }

    public static ScriptStep[] Reply(string text = "hello", string served = "m") =>
    [
        new ScriptStep.Emit(new ModelEvent.TextDelta(text)),
        new ScriptStep.Emit(new ModelEvent.UsageReported(new GatewayTokenUsage(10, 0, 0, 0, 5, 0))),
        new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, served)),
    ];
}
