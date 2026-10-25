using Npgsql;
using Testcontainers.PostgreSql;

namespace Bazaar.Testing.Postgres;

/// <summary>Raised when no PostgreSQL is reachable; the message says how to provide one.</summary>
public sealed class PgUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The PostgreSQL server a test run uses. <c>BAZAAR_TEST_PG</c> (a connection string, as set by the CI service
/// container or a locally installed server) wins; otherwise a Testcontainers pgvector container is started.
/// Shared per process and torn down at process exit.
/// </summary>
public sealed class PgSource : IAsyncDisposable
{
    public const string EnvConnectionString = "BAZAAR_TEST_PG";
    public const string EnvImage = "BAZAAR_TEST_PG_IMAGE";
    public const string DefaultImage = "pgvector/pgvector:pg18";

    /// <summary>
    /// True on Windows without <c>BAZAAR_TEST_PG</c>: hosted Windows runners cannot run Linux containers, so database
    /// tests there wait for a locally installed server (docs/design/34, <c>test.ci.windows_db_job</c> = false).
    /// Linux never counts as unsupported; a missing database there must fail loudly.
    /// </summary>
    public static bool IsUnsupportedHost =>
        OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvConnectionString));

    private static readonly Lazy<Task<PgSource>> _sharedLazy = new(StartAsync);

    private readonly PostgreSqlContainer? _container;

    private PgSource(string adminConnectionString, string origin, PostgreSqlContainer? container)
    {
        AdminConnectionString = adminConnectionString;
        Origin = origin;
        _container = container;
    }

    /// <summary>Connection string for the maintenance database (<c>postgres</c>); used to create and drop databases.</summary>
    public string AdminConnectionString { get; }

    /// <summary>"BAZAAR_TEST_PG" or "testcontainers", for logs and failure messages.</summary>
    public string Origin { get; }

    /// <summary>The per-process shared source. Starts on first use.</summary>
    public static Task<PgSource> GetSharedAsync() => _sharedLazy.Value;

    /// <summary>Builds a connection string for <paramref name="database"/> on the same server and credentials.</summary>
    public string ConnectionStringFor(string database, bool pooling = true) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database, Pooling = pooling }.ConnectionString;

    private static async Task<PgSource> StartAsync()
    {
        var env = Environment.GetEnvironmentVariable(EnvConnectionString);
        PgSource source;
        if (!string.IsNullOrWhiteSpace(env))
        {
            var admin = new NpgsqlConnectionStringBuilder(env) { Database = "postgres", Pooling = false };
            source = new PgSource(admin.ConnectionString, EnvConnectionString, container: null);
        }
        else
        {
            var image = Environment.GetEnvironmentVariable(EnvImage);
            var container = new PostgreSqlBuilder(string.IsNullOrWhiteSpace(image) ? DefaultImage : image).Build();
            try
            {
                await container.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new PgUnavailableException(
                    $"No test PostgreSQL: {EnvConnectionString} is not set and Testcontainers could not start a container " +
                    "(is a Docker daemon running?). Set BAZAAR_TEST_PG to a PostgreSQL 18 connection string, " +
                    "for example 'Host=localhost;Username=postgres;Password=...'.", ex);
            }

            var admin = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "postgres", Pooling = false };
            source = new PgSource(admin.ConnectionString, "testcontainers", container);
        }

        try
        {
            await using var conn = new NpgsqlConnection(source.AdminConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw new PgUnavailableException($"Test PostgreSQL from {source.Origin} is not reachable: {ex.Message}", ex);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync().ConfigureAwait(false);
    }
}
