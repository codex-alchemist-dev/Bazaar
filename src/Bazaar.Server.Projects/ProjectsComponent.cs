using Bazaar.Core;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Projects;

/// <summary>
/// Projects and membership (docs/design/93-projects-and-membership.md) as a Bazaar Component. Registers the Postgres
/// <see cref="IProjectMembership"/> that Vault access and search read, and the audited <see cref="IProjectStore"/> write API, both on the shared
/// <see cref="NpgsqlDataSource"/>. Its SQL is the <c>projects</c> migration namespace, embedded under <c>Sql/components/projects</c>. Options bind
/// from <c>Bazaar:Projects</c>.
/// </summary>
public sealed class ProjectsComponent : IBazaarComponent
{
    /// <summary>Migration namespace and history key for this component's SQL. Equals <see cref="ComponentManifest.MigrationNamespace"/>.</summary>
    public const string MigrationProvider = "projects";

    /// <summary>The manifest: needs the audit log (every write is audited).</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "projects",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["audit"],
        Provides: ["feature:projects"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(ProjectsOptions.Read(configuration.GetSection(ProjectsOptions.SectionName)));
        services.TryAddSingleton<IProjectMembership>(sp => new PgProjectMembership(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ProjectsOptions>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IProjectStore>(sp => new PgProjectStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ITransactionalAuditLog>(),
            sp.GetRequiredService<ProjectsOptions>(),
            sp.GetService<TimeProvider>()));
    }
}
