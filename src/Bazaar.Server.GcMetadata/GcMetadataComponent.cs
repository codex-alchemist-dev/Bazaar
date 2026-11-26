using Bazaar.Core;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.GcMetadata;

/// <summary>
/// GC metadata and the DM owner-read flow (docs/design/01-gc-data-model.md) as a Bazaar Component. It needs the GC log (<c>gc</c>) for the
/// <see cref="IGcLogWriter"/> and the audit log (<c>audit</c>) for <see cref="IAuditLog"/>. The settings come from
/// <see cref="IDmAccessOptionsSource"/>: the host registers one backed by <c>gc.dm_access_approvers</c> before the components, and
/// without one the options bind from <c>Bazaar:GcMetadata</c> (<c>Approvers</c>: owner or owner-or-dm-member; <c>ShowOwnerReadMarker</c>;
/// <c>Statuses</c>).
/// </summary>
public sealed class GcMetadataComponent : IBazaarComponent
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Bazaar:GcMetadata";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "gc-metadata",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc", "audit"],
        Provides: ["feature:gc-metadata", "feature:dm-access"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        services.TryAddSingleton(ReadSchema(section));
        services.TryAddSingleton<GcMetadataCodec>(sp => new GcMetadataCodec(sp.GetRequiredService<GcMetadataSchema>()));

        var options = ReadOptions(section);
        services.TryAddSingleton<IDmAccessOptionsSource>(new StaticDmAccessOptionsSource(options));
        services.TryAddSingleton<IDmAccessLedger>(sp => new PgDmAccessLedger(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IGcHeaderSource>(sp => new PgGcHeaderSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IDmDirectory>(sp => new PgDmDirectory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IDmAccessEvents>(sp => new GcDmAccessEvents(sp.GetRequiredService<IGcLogWriter>()));
        services.TryAddSingleton(sp => new DmAccessService(
            sp.GetRequiredService<IDmDirectory>(),
            sp.GetRequiredService<IDmAccessLedger>(),
            sp.GetRequiredService<IDmAccessEvents>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<IDmAccessOptionsSource>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IDmAccessService>(sp => sp.GetRequiredService<DmAccessService>());
        services.TryAddSingleton<IDmReadAuthorizer>(sp => sp.GetRequiredService<DmAccessService>());
    }

    internal static GcMetadataSchema ReadSchema(IConfigurationSection section)
    {
        var statuses = section.GetSection("Statuses").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
        return statuses.Count == 0 ? GcMetadataSchema.Default : new GcMetadataSchema(statuses);
    }

    internal static DmAccessOptions ReadOptions(IConfigurationSection section)
    {
        var approvers = section["Approvers"] switch
        {
            null or "" or "owner" => DmApprovers.Owner,
            "owner-or-dm-member" => DmApprovers.OwnerOrDmMember,
            var other => throw new InvalidOperationException($"{SectionName}:Approvers holds '{other}'. Use owner or owner-or-dm-member."),
        };

        var marker = true;
        if (section["ShowOwnerReadMarker"] is { Length: > 0 } text && !bool.TryParse(text, out marker))
            throw new InvalidOperationException($"{SectionName}:ShowOwnerReadMarker must be true or false.");

        return new DmAccessOptions(approvers, marker);
    }
}
