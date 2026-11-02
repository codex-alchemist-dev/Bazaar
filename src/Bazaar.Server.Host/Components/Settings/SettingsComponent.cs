using System.Text.Json.Nodes;
using Bazaar.Server.Components;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Components.Settings;

/// <summary>
/// Settings as a Bazaar Component (docs/design/BAZAAR-COMPONENTS.md). Registers <see cref="SettingRegistry.Known"/>, the Postgres-backed
/// <see cref="ISettingStore"/>, and an <see cref="ISettingAuditSink"/> that writes each setting change into <see cref="IAuditLog"/>.
/// It depends on <c>audit</c> because the sink appends to the audit log, so the kernel starts audit first.
/// </summary>
public sealed class SettingsComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "settings",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["audit"],
        Provides: ["feature:settings"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(SettingRegistry.Known);
        services.TryAddSingleton<ISettingAuditSink, AuditLogSettingSink>();
        services.TryAddSingleton<ISettingStore, NpgsqlSettingStore>();
    }
}

/// <summary>
/// Writes each <see cref="SettingChangeAudit"/> as one <see cref="AuditEntry"/> with kind <c>setting.change</c>. The payload is a
/// JSON object whose fields carry every value of the change. Values stay as the strings the store produced, so a value that
/// is not valid JSON (the store keeps the raw text) still round-trips unchanged.
/// </summary>
internal sealed class AuditLogSettingSink(IAuditLog log, TimeProvider clock) : ISettingAuditSink
{
    /// <summary>The audit kind for a setting write attempt.</summary>
    public const string Kind = "setting.change";

    public Task AppendAsync(SettingChangeAudit change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var payload = new JsonObject
        {
            ["key"] = change.Key,
            ["scope"] = change.Scope.ToString().ToLowerInvariant(),
            ["scopeId"] = change.ScopeId,
            ["outcome"] = change.Outcome.ToString(),
            ["oldValueJson"] = change.OldValueJson,
            ["newValueJson"] = change.NewValueJson,
            ["reason"] = change.Reason,
        };
        return log.AppendAsync(new AuditEntry(clock.GetUtcNow(), change.ActorId, Kind, payload.ToJsonString()), cancellationToken);
    }
}
