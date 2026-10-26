using System.Text.Json.Nodes;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Audit log and settings wiring. Needs <c>AddBazaarHostOptions</c>, <c>AddBazaarHostRuntime</c> and <c>AddBazaarSecrets</c>
/// registered first, because the shared <see cref="NpgsqlDataSource"/> comes from <see cref="DatabaseConnection"/>.
/// </summary>
public static class SettingsAuditCompositionExtensions
{
    /// <summary>Postgres audit log (doc 15) as <see cref="IAuditLog"/>, on the shared data source.</summary>
    public static IServiceCollection AddBazaarAudit(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => SharedDataSource(sp));
        services.TryAddSingleton<PostgresAuditLog>();
        services.TryAddSingleton<IAuditLog>(sp => sp.GetRequiredService<PostgresAuditLog>());
        return services;
    }

    /// <summary>
    /// Host settings: <see cref="SettingRegistry.Host"/>, the Postgres-backed <see cref="ISettingStore"/>, and an
    /// <see cref="ISettingAuditSink"/> that writes each setting change into <see cref="IAuditLog"/>. Calls
    /// <see cref="AddBazaarAudit"/> itself, so the sink always has its log.
    /// </summary>
    public static IServiceCollection AddBazaarSettings(this IServiceCollection services)
    {
        services.AddBazaarAudit();
        services.TryAddSingleton(SettingRegistry.Host);
        services.TryAddSingleton<ISettingAuditSink, AuditLogSettingSink>();
        services.TryAddSingleton<ISettingStore, NpgsqlSettingStore>();
        return services;
    }

    /// <summary>
    /// The one <see cref="NpgsqlDataSource"/>. <see cref="DatabaseConnection"/> resolves the secret once and caches the source,
    /// so this returns that same instance. The resolve is synchronous because the factory is; it runs once, at first use.
    /// </summary>
    private static NpgsqlDataSource SharedDataSource(IServiceProvider sp) =>
        sp.GetRequiredService<DatabaseConnection>().GetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
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
