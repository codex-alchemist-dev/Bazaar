using Npgsql;

namespace Bazaar.Server.Setup.Probes;

/// <summary>What a PostgreSQL server reports about itself. <see cref="VersionNum"/> is <c>server_version_num</c> (for example 180001).</summary>
public sealed record PostgresServerReading(int VersionNum, bool PgvectorAvailable);

/// <summary>Reads the PostgreSQL server's version and whether the pgvector extension can be installed or is. Read-only.</summary>
public interface IPostgresServerReader
{
    Task<PostgresServerReading> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The schema state of the database against the migration files in this build.</summary>
public enum SetupSchemaState
{
    Current,
    Behind,
    Ahead,
}

/// <summary>Compares the database with this build's migration files without changing anything. Supplied by the server host, which owns the file list.</summary>
public interface ISchemaStateReader
{
    Task<SetupSchemaState> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Whether this process holds the running-instance lock. Supplied by the server host.</summary>
public interface IInstanceLockReader
{
    bool IsHeld { get; }
}

/// <summary>One role's providers and how many are usable.</summary>
public sealed record ProviderRoleReading(string Role, int Registered, int Usable);

/// <summary>The provider roles (doc 03) and what each provider reports. Supplied by the server host, which owns the report.</summary>
public interface IProviderRoleReader
{
    IReadOnlyList<ProviderRoleReading> Read();
}

/// <summary>Answers whether a URL answers at all. Any HTTP reply counts, including a refusal: reachable is not the same as authorised.</summary>
public interface IReachabilityClient
{
    Task<bool> IsReachableAsync(Uri url, TimeSpan limit, CancellationToken cancellationToken);
}

/// <summary><see cref="IPostgresServerReader"/> over the one shared data source. Two read-only queries.</summary>
public sealed class NpgsqlPostgresServerReader(NpgsqlDataSource dataSource) : IPostgresServerReader
{
    /// <inheritdoc />
    public async Task<PostgresServerReading> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var version = new NpgsqlCommand("SELECT current_setting('server_version_num')::int", connection);
        var versionNum = (int)(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await using var vector = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector')", connection);
        var pgvector = (bool)(await vector.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new PostgresServerReading(versionNum, pgvector);
    }
}

/// <summary>
/// <see cref="IReachabilityClient"/> over HTTP. A <c>HEAD</c> with no credentials, no redirect following and the caller's limit. Any
/// status code is reachable. A refused connection, a failed TLS handshake or a timeout is not.
/// </summary>
public sealed class HttpReachabilityClient(HttpMessageHandler? handler = null) : IReachabilityClient, IDisposable
{
    private readonly HttpClient _client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <inheritdoc />
    public async Task<bool> IsReachableAsync(Uri url, TimeSpan limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(limit);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
