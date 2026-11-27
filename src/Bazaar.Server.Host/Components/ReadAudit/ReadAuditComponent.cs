using Cdax.Core;
using Bazaar.Server.ReadAudit.Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Components.ReadAudit;

/// <summary>
/// Read audit coverage (doc 48 section 5.1) as a Bazaar Component. Registers <see cref="AuditedDbDataSource"/> over the shared
/// <see cref="Npgsql.NpgsqlDataSource"/> so modules can migrate to it one at a time. It is a distinct type, never an
/// <see cref="Npgsql.NpgsqlDataSource"/>, so the audit log (which asks for the raw source) can never resolve it by accident: audit
/// writes stay off the audited path. On by default, because reads of personal data must be audited; the operator turns it off with
/// <c>Bazaar:ReadAudit:Enabled=false</c> (or disables the component). <c>Bazaar:ReadAudit:RecordUntaggedReads</c> and
/// <c>Bazaar:ReadAudit:UntaggedObjectType</c> tune <see cref="AuditedDataSourceOptions"/>. It depends on <c>audit</c> for <c>IAuditLog</c>.
/// </summary>
public sealed class ReadAuditComponent : ICdaxModule
{
    public const string SectionName = "Bazaar:ReadAudit";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "read-audit",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["audit"],
        Provides: ["feature:read-audit"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.GetValue("Enabled", true))
            return;
        services.AddBazaarAuditedDataSource(options =>
        {
            options.RecordUntaggedReads = section.GetValue("RecordUntaggedReads", options.RecordUntaggedReads);
            options.UntaggedObjectType = section["UntaggedObjectType"] is { Length: > 0 } label ? label : options.UntaggedObjectType;
        });
    }
}
