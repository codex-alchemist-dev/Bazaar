using Bazaar.Contracts.Gc;
using Npgsql;

namespace Bazaar.Server.Mentions;

/// <summary>One principal's handle.</summary>
/// <param name="PrincipalId">The principal: an agent id or a human id.</param>
/// <param name="Kind">What kind of principal it is.</param>
/// <param name="Handle">The handle, lowercase, without the "@".</param>
public sealed record PrincipalHandle(Guid PrincipalId, PrincipalKind Kind, string Handle);

/// <summary>The handle does not have the shape <see cref="HandleSlug.IsValid"/> accepts, or is a broadcast word.</summary>
public sealed class InvalidHandleException(string handle)
    : ArgumentException($"'{handle}' is not a valid handle: use {HandleSlug.MinLength} to {HandleSlug.MaxLength} lowercase letters, digits, '_' or '-', starting with a letter or digit; 'here' and 'all' are reserved.")
{
    /// <summary>The rejected handle, after normalisation.</summary>
    public string Handle { get; } = handle;
}

/// <summary>Another principal already holds the handle.</summary>
public sealed class HandleTakenException(string handle, Exception? innerException = null)
    : InvalidOperationException($"The handle '{handle}' is already taken.", innerException)
{
    /// <summary>The handle that is taken.</summary>
    public string Handle { get; } = handle;
}

/// <summary>
/// SQL-first access to <c>principal_handle</c> (<c>mentions/0001</c>). A principal has at most one handle and a handle belongs
/// to at most one principal; both are enforced by the table, so concurrent writers cannot break them.
/// </summary>
public sealed class PrincipalHandleRepository(NpgsqlDataSource dataSource)
{
    /// <summary>How many suffixed candidates <see cref="EnsureAgentHandleAsync"/> tries before it gives up.</summary>
    public const int MaxSuffixAttempts = 1000;

    private const string _handleUniqueConstraint = "principal_handle_handle_key";

    private const string _getSql = "SELECT principal_id, principal_kind, handle FROM principal_handle WHERE principal_id = $1";

    private const string _findSql = "SELECT principal_id, principal_kind, handle FROM principal_handle WHERE handle = $1";

    private const string _setSql = """
        INSERT INTO principal_handle (principal_id, principal_kind, handle)
        VALUES ($1, $2, $3)
        ON CONFLICT (principal_id) DO UPDATE
            SET principal_kind = EXCLUDED.principal_kind, handle = EXCLUDED.handle, updated_at = now()
        """;

    // Inserts only when the agent has no handle. A handle held by another principal raises a unique violation.
    private const string _ensureSql = """
        INSERT INTO principal_handle (principal_id, principal_kind, handle)
        VALUES ($1, 'agent', $2)
        ON CONFLICT (principal_id) DO NOTHING
        RETURNING handle
        """;

    /// <summary>The handle of a principal, or null when it has none.</summary>
    public async Task<PrincipalHandle?> GetAsync(Guid principalId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_getSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The principal that holds <paramref name="handle"/>, or null. The handle is matched case-insensitively
    /// (<see cref="HandleSlug.Normalize"/>); a text that cannot be a handle finds nothing.
    /// </summary>
    public async Task<PrincipalHandle?> FindByHandleAsync(string handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var normalized = HandleSlug.Normalize(handle);
        if (!HandleSlug.IsValid(normalized))
            return null;

        await using var cmd = dataSource.CreateCommand(_findSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = normalized });
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives a principal a handle, replacing the one it had. The handle is normalised first (<see cref="HandleSlug.Normalize"/>).
    /// Setting the handle the principal already holds changes nothing but <c>updated_at</c>.
    /// </summary>
    /// <returns>The stored handle.</returns>
    /// <exception cref="InvalidHandleException">The handle is not valid.</exception>
    /// <exception cref="HandleTakenException">Another principal holds the handle.</exception>
    public async Task<PrincipalHandle> SetAsync(Guid principalId, PrincipalKind kind, string handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var normalized = HandleSlug.Normalize(handle);
        if (!HandleSlug.IsValid(normalized))
            throw new InvalidHandleException(normalized);

        await using var cmd = dataSource.CreateCommand(_setSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = KindText(kind) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = normalized });
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsHandleTaken(ex))
        {
            throw new HandleTakenException(normalized, ex);
        }

        return new PrincipalHandle(principalId, kind, normalized);
    }

    /// <summary>
    /// Makes sure an agent has a handle and returns it. An agent that already has one keeps it. Otherwise it gets the slug of
    /// <paramref name="name"/> (<see cref="HandleSlug.FromName"/>), with "-2", "-3", ... when the slug is taken or reserved.
    /// Returns null when the name gives no usable slug; the agent then stays without a handle.
    /// </summary>
    /// <remarks>
    /// Safe under concurrency: the insert either wins, finds the agent's own row, or hits the unique handle and moves on to the
    /// next candidate. Two agents with one name end up with different handles whatever the order.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No free candidate was found in <see cref="MaxSuffixAttempts"/> tries.</exception>
    public async Task<string?> EnsureAgentHandleAsync(Guid agentId, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var existing = await GetAsync(agentId, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing.Handle;

        var slug = HandleSlug.FromName(name);
        if (slug is null)
            return null;

        for (var n = 1; n <= MaxSuffixAttempts; n++)
        {
            var candidate = HandleSlug.WithSuffix(slug, n);
            if (HandleSlug.IsReserved(candidate))
                continue;

            try
            {
                await using var cmd = dataSource.CreateCommand(_ensureSql);
                cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = candidate });
                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string inserted)
                    return inserted;
            }
            catch (PostgresException ex) when (IsHandleTaken(ex))
            {
                continue;
            }

            // Nothing inserted and no error: a concurrent call gave this agent its handle first.
            existing = await GetAsync(agentId, ct).ConfigureAwait(false);
            if (existing is not null)
                return existing.Handle;
        }

        throw new InvalidOperationException($"No free handle was found for '{slug}' in {MaxSuffixAttempts} tries.");
    }

    private static bool IsHandleTaken(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == _handleUniqueConstraint;

    private static async Task<PrincipalHandle?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new PrincipalHandle(reader.GetGuid(0), ParseKind(reader.GetString(1)), reader.GetString(2));
    }

    private static string KindText(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Owner => "owner",
        PrincipalKind.Human => "human",
        PrincipalKind.Agent => "agent",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static PrincipalKind ParseKind(string text) => text switch
    {
        "owner" => PrincipalKind.Owner,
        "human" => PrincipalKind.Human,
        "agent" => PrincipalKind.Agent,
        _ => throw new InvalidDataException($"Unknown principal_kind '{text}'."),
    };
}
