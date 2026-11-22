using Bazaar.Server.Security.Audit;
using Bazaar.Contracts.Engines;
using Bazaar.Core;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.EngineRelay;

/// <summary>
/// Host composition of the engine relay [PROPOSED] (docs/design/02 stream registry, 20, 40 section 4b item 4). Registers the wire
/// adapters, the <c>engine.relay</c> stream gate, the dispatcher and the relay broker. Every seam the host has not supplied yet
/// (grant reader, taint reader, GC recorder, audit log) gets a fail-closed default, so until the Engines module and the GC recorder
/// are composed no relayed call is allowed. Setting: <c>Engine:Relay:TimeoutSeconds</c> (<c>engine.relay.timeout_seconds</c>).
/// </summary>
public sealed class EngineRelayComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "engine-relay",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:engine-relay"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddBazaarEngineRelay();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStreamGate, EngineRelayStreamGate>());
        services.TryAddSingleton<IEngineGrantReader, DenyingEngineGrantReader>();
        services.TryAddSingleton<ISessionTaintReader, TaintedEngineSessionReader>();
        services.TryAddSingleton<IEngineGcRecorder, FailingEngineGcRecorder>();
        services.TryAddSingleton(sp => new EngineToolDispatcher(
            sp.GetRequiredService<IEngineGrantReader>(),
            sp.GetRequiredService<ISessionTaintReader>(),
            sp.GetRequiredService<IEngineGcRecorder>(),
            sp.GetService<IAuditLog>() ?? new FailingAuditLog(),
            time: sp.GetService<TimeProvider>()));
        services.TryAddSingleton(new EngineRelayOptions
        {
            Timeout = TimeSpan.FromSeconds(int.TryParse(configuration["Engine:Relay:TimeoutSeconds"], out var seconds) ? seconds : EngineRelayWire.DefaultTimeoutSeconds),
        });
        services.TryAddSingleton(sp => new EngineRelayBroker(
            sp.GetRequiredService<EngineToolDispatcher>(),
            sp.GetRequiredService<IRelayTargetDirectory>(),
            sp.GetRequiredService<IRelayPublisher>(),
            sp.GetRequiredService<EngineRelayOptions>(),
            sp.GetService<TimeProvider>()));
    }
}

internal sealed class DenyingEngineGrantReader : IEngineGrantReader
{
    public ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct) =>
        ValueTask.FromResult(false);
}

internal sealed class TaintedEngineSessionReader : ISessionTaintReader
{
    public ValueTask<bool> IsHardTaintedAsync(Guid agentId, Guid? sessionId, CancellationToken ct) => ValueTask.FromResult(true);
}

internal sealed class FailingEngineGcRecorder : IEngineGcRecorder
{
    public Task RecordAsync(EngineToolCallRecord record, CancellationToken ct) =>
        throw new InvalidOperationException("No engine GC recorder is composed.");
}

internal sealed class FailingAuditLog : IAuditLog
{
    public Task AppendAsync(AuditEntry e, CancellationToken ct = default) =>
        throw new InvalidOperationException("No audit log is composed.");
}
