using System.Net.Sockets;
using Bazaar.Server.Secrets;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Admin.Composition;

/// <summary>
/// The real apply gate (doc 45 sections 1.2 and 1.7). It is open only when the durable audit store is reachable: the database
/// answers, <c>audit_log</c> exists (migration 0004, doc 15) and the connecting role may read and insert into it. Anything else
/// leaves it closed, so <c>setup apply</c> exits 2 and never builds the applier. No failure detail is kept, because a connection
/// error can carry the connection string.
/// </summary>
/// <remarks>
/// When open, <see cref="RegisterApplyServices"/> registers the Postgres instance lock, the hash-chained Postgres audit log and the
/// settings audit sink, all on the one data source. The connection string comes from the same secret reference the host uses
/// (<c>Bazaar:Host:DatabaseConnectionReference</c>, here the <c>BAZAAR__HOST__DATABASECONNECTIONREFERENCE</c> variable) and is resolved
/// by the bootstrap resolver, so only <c>env:</c> and <c>file:</c> references work from this command. A managed-Postgres install
/// keeps its connection string inside the host, so the gate stays closed there until the host runs the apply.
/// </remarks>
public sealed class PostgresApplyGate : ICliApplyGate, IAsyncDisposable
{
    /// <summary>The environment variable that names the database connection reference (host option, uppercase form).</summary>
    public const string ReferenceVariable = "BAZAAR__HOST__DATABASECONNECTIONREFERENCE";

    /// <summary>The same variable in the mixed-case form that the host configuration also accepts.</summary>
    public const string ReferenceVariableMixedCase = "Bazaar__Host__DatabaseConnectionReference";

    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(10);

    private const string _probeSql =
        "SELECT to_regclass('audit_log') IS NOT NULL " +
        "AND has_table_privilege('audit_log', 'SELECT') AND has_table_privilege('audit_log', 'INSERT')";

    private NpgsqlDataSource? _dataSource;

    private PostgresApplyGate(NpgsqlDataSource? dataSource)
    {
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public bool DurableAuditStoreAvailable => _dataSource is not null;

    /// <summary>A gate that is always closed, for when no database is configured or reachable.</summary>
    public static PostgresApplyGate Closed { get; } = new(null);

    /// <summary>
    /// Builds the gate from the process variables. A missing reference, an unresolvable one or an unreachable database yields a
    /// closed gate. Cancellation by the caller is not swallowed.
    /// </summary>
    public static async Task<PostgresApplyGate> FromEnvironmentAsync(
        IReadOnlyDictionary<string, string?> variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var reference = ReadVariable(variables, ReferenceVariable) ?? ReadVariable(variables, ReferenceVariableMixedCase);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Closed;
        }

        var resolver = new BootstrapSecretResolver(name => variables.GetValueOrDefault(name));
        NpgsqlDataSource? dataSource = null;
        try
        {
            if (!resolver.CanResolve(reference))
            {
                return Closed;
            }

            var connectionString = await resolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            dataSource = NpgsqlDataSource.Create(connectionString);
            return await ProbeAsync(dataSource, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception, cancellationToken))
        {
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync().ConfigureAwait(false);
            }

            return Closed;
        }
    }

    /// <summary>
    /// Checks that <paramref name="dataSource"/> reaches an audit store this role can write. Open on success, with the gate taking
    /// ownership of the data source. Closed on failure, with the data source disposed.
    /// </summary>
    public static async Task<PostgresApplyGate> ProbeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_probeTimeout);
            await using var cmd = dataSource.CreateCommand(_probeSql);
            var ready = await cmd.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
            if (ready is true)
            {
                return new PostgresApplyGate(dataSource);
            }
        }
        catch (Exception exception) when (IsConnectionFailure(exception, cancellationToken))
        {
            // Closed below. The detail is dropped on purpose.
        }

        await dataSource.DisposeAsync().ConfigureAwait(false);
        return Closed;
    }

    /// <inheritdoc />
    public void RegisterApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var dataSource = _dataSource ?? throw new InvalidOperationException("The apply gate is closed.");
        var audit = new PostgresAuditLog(dataSource);
        services.AddSingleton<ISetupInstanceLock>(new PostgresSetupInstanceLock(dataSource));
        services.AddSingleton<IAuditLog>(audit);
        var settingAudit = new PostgresSettingAuditSink(audit);
        services.AddSingleton<ISettingAuditSink>(settingAudit);
        services.AddSingleton<ISetupSettingAudit>(new SettingAuditSinkSetupAudit(settingAudit));
    }

    /// <summary>Disposes the data source this gate owns. The shared closed gate owns none.</summary>
    public async ValueTask DisposeAsync()
    {
        var dataSource = Interlocked.Exchange(ref _dataSource, null);
        if (dataSource is not null)
        {
            await dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? ReadVariable(IReadOnlyDictionary<string, string?> variables, string name) =>
        variables.TryGetValue(name, out var value) ? value : null;

    /// <summary>The failures that mean "store not reachable": the database, the socket, a bad connection string or secret, a timeout.</summary>
    private static bool IsConnectionFailure(Exception exception, CancellationToken callerToken) =>
        exception switch
        {
            OperationCanceledException => !callerToken.IsCancellationRequested,
            NpgsqlException or SocketException or TimeoutException or ArgumentException or InvalidOperationException
                or IOException or SecretResolutionException => true,
            _ => false,
        };
}
