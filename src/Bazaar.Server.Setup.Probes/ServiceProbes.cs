using System.Globalization;

namespace Bazaar.Server.Setup.Probes;

/// <summary>
/// <c>database.postgres</c>: the server answers, its major version is the plan's, and pgvector is installed or installable. Messages carry
/// the version number only: no host, no database name, no exception text.
/// </summary>
public sealed class PostgresHealthProbe(IPostgresServerReader reader, TimeSpan? limit = null) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "database.postgres";

    /// <inheritdoc />
    public async Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        PostgresServerReading reading;
        try
        {
            reading = await Bounded.RunAsync(reader.ReadAsync, limit ?? Bounded.DefaultLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new SetupProbeReading(SetupProbeOutcome.Failed, "PostgreSQL did not answer in time.", "Check that the database is running and reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SetupProbeReading(SetupProbeOutcome.Failed, "PostgreSQL could not be reached.", "Check that the database is running and that its credential is right.");
        }

        var major = reading.VersionNum / 10000;
        if (!reading.PgvectorAvailable)
        {
            return new SetupProbeReading(SetupProbeOutcome.Failed, "The pgvector extension is not available on this PostgreSQL server.", "Install pgvector for this PostgreSQL version.");
        }

        var planned = context.Plan.Database.PostgresMajor;
        return major == planned
            ? new SetupProbeReading(SetupProbeOutcome.Healthy, string.Create(CultureInfo.InvariantCulture, $"PostgreSQL {major} answers and pgvector is available."))
            : new SetupProbeReading(
                SetupProbeOutcome.Degraded,
                string.Create(CultureInfo.InvariantCulture, $"PostgreSQL {major} answers, but the plan names major version {planned}."),
                "Change database.postgresMajor in the plan, or move to that PostgreSQL version.");
    }
}

/// <summary><c>schema.migrations</c>: the database matches this build's migration files exactly (doc 04). Read-only; it never migrates.</summary>
public sealed class SchemaMigrationsProbe(ISchemaStateReader reader, TimeSpan? limit = null) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "schema.migrations";

    /// <inheritdoc />
    public async Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var state = await Bounded.RunAsync(reader.ReadAsync, limit ?? Bounded.DefaultLimit, cancellationToken).ConfigureAwait(false);
            return state switch
            {
                SetupSchemaState.Current => new SetupProbeReading(SetupProbeOutcome.Healthy, "Every migration of this build is applied."),
                SetupSchemaState.Behind => new SetupProbeReading(SetupProbeOutcome.Failed, "The database schema is older than this build.", "Take a backup, then run the migrate command."),
                _ => new SetupProbeReading(SetupProbeOutcome.Failed, "The database schema is newer than this build.", "Run a newer build."),
            };
        }
        catch (TimeoutException)
        {
            return new SetupProbeReading(SetupProbeOutcome.Failed, "The database did not answer the schema check in time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SetupProbeReading(SetupProbeOutcome.Unavailable, "The schema state could not be read");
        }
    }
}

/// <summary><c>instance.lock</c>: this process holds the running-instance lock (doc 03). Never takes the lock itself.</summary>
public sealed class InstanceLockProbe(IInstanceLockReader reader) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "instance.lock";

    /// <inheritdoc />
    public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(reader.IsHeld
            ? new SetupProbeReading(SetupProbeOutcome.Healthy, "This server holds the instance lock.")
            : new SetupProbeReading(SetupProbeOutcome.Failed, "This server does not hold the instance lock.", "Another instance may be running against the same database."));
    }
}

/// <summary>
/// <c>providers.roles</c>: the cache and search roles each have a usable provider (doc 03; the cache is Garnet or Valkey, search is
/// Postgres full-text or pg_search). The service host and VM roles are listed, and a missing one is amber (a console run has no service host).
/// </summary>
public sealed class ProviderRolesProbe(IProviderRoleReader reader) : ISetupInfrastructureProbe
{
    private static readonly string[] _required = ["cache", "search"];

    private static readonly string[] _optional = ["service_host", "vm"];

    /// <inheritdoc />
    public string CheckId => "providers.roles";

    /// <inheritdoc />
    public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var roles = reader.Read();
        var missingRequired = _required.Where(role => !HasUsable(roles, role)).ToList();
        if (missingRequired.Count > 0)
        {
            return Task.FromResult(new SetupProbeReading(
                SetupProbeOutcome.Failed,
                $"No usable provider for: {string.Join(", ", missingRequired)}.",
                "Install or start the provider for that role (cache: Garnet or Valkey; search: Postgres full-text or pg_search)."));
        }

        var missingOptional = _optional.Where(role => !HasUsable(roles, role)).ToList();
        return Task.FromResult(missingOptional.Count > 0
            ? new SetupProbeReading(SetupProbeOutcome.Degraded, $"Cache and search are usable. No usable provider for: {string.Join(", ", missingOptional)}.", "Install a provider for that role if this host should run it.")
            : new SetupProbeReading(SetupProbeOutcome.Healthy, "Every provider role has a usable provider."));
    }

    private static bool HasUsable(IReadOnlyList<ProviderRoleReading> roles, string role) =>
        roles.Any(reading => string.Equals(reading.Role, role, StringComparison.Ordinal) && reading.Usable > 0);
}
