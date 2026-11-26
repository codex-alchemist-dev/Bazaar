using Bazaar.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.CompanyBuild.Postgres;

/// <summary>One rebuild taken off the queue: the revision it builds and the module ids that revision selects.</summary>
public sealed record RebuildJob(string BuildId, long SelectionRevision, IReadOnlyList<string> SelectedModuleIds);

/// <summary>
/// The rebuild queue as the company build worker sees it (doc 37 section 4a). The worker claims the next queued job, runs the
/// company build, then completes it. The panel and the TUI only start jobs, through <c>IAdminModuleSelection</c>.
/// </summary>
public interface IRebuildJobs
{
    /// <summary>Moves the oldest queued job to running and returns it, or null when none is queued.</summary>
    Task<RebuildJob?> ClaimNextAsync(CancellationToken ct = default);

    /// <summary>Ends a running job as succeeded or failed. False when the job is not running.</summary>
    Task<bool> CompleteAsync(string buildId, bool succeeded, string? reportSummary, CancellationToken ct = default);
}

/// <summary>
/// Publishes the server's module catalogue into <c>module_catalogue</c>, so the separate TUI process can list modules without
/// referencing the host. Upserts every module and removes the ones no longer shipped.
/// </summary>
public static class PostgresModuleCatalogue
{
    public static async Task PublishAsync(NpgsqlDataSource dataSource, IEnumerable<ComponentManifest> manifests, TimeProvider clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(manifests);
        var list = manifests.ToList();
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var m in list)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO module_catalogue (module_id, name, version, tag, depends_on, updated_at) VALUES ($1, $2, $3, $4, $5, $6) " +
                "ON CONFLICT (module_id) DO UPDATE SET name = $2, version = $3, tag = $4, depends_on = $5, updated_at = $6",
                connection, tx);
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = m.Id });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = m.Id });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = m.Version });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = m.Kind.ToString().ToLowerInvariant() });
            cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. m.DependsOn] });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = clock.GetUtcNow() });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var prune = new NpgsqlCommand("DELETE FROM module_catalogue WHERE NOT (module_id = ANY($1))", connection, tx))
        {
            prune.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. list.Select(m => m.Id)] });
            await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Publishes the catalogue once at server start. A database that is not migrated yet, or is unreachable, is logged and skipped: it never stops the host.</summary>
internal sealed partial class ModuleCataloguePublisher(NpgsqlDataSource dataSource, IReadOnlyList<ComponentManifest> manifests, TimeProvider clock, ILogger<ModuleCataloguePublisher> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PostgresModuleCatalogue.PublishAsync(dataSource, manifests, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException ex)
        {
            LogNotPublished(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The module catalogue was not published; module selection stays empty until the next start.")]
    private static partial void LogNotPublished(ILogger logger, Exception ex);
}

public static class ModuleSelectionPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers the selection and rebuild port. Needs an <c>NpgsqlDataSource</c> in the container. Pass the server's manifests to
    /// publish the catalogue at start (the server does; the TUI only reads it).
    /// </summary>
    public static IServiceCollection AddBazaarModuleSelectionPostgres(this IServiceCollection services, IEnumerable<ComponentManifest>? publishManifests = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PostgresModuleSelection>(sp => new PostgresModuleSelection(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<Bazaar.Server.WebAdmin.IAdminModuleSelection>(sp => sp.GetRequiredService<PostgresModuleSelection>());
        services.TryAddSingleton<IRebuildJobs>(sp => sp.GetRequiredService<PostgresModuleSelection>());
        if (publishManifests is not null)
        {
            var list = publishManifests.ToList();
            services.AddSingleton<IHostedService>(sp => new ModuleCataloguePublisher(
                sp.GetRequiredService<NpgsqlDataSource>(), list, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ModuleCataloguePublisher>>()));
        }

        return services;
    }
}
