using Npgsql;

namespace Bazaar.Server.Community.Blocklist;

/// <summary>Statements for <c>target_blocklist</c> (migration community/0004). Constants, so no statement is built from input.</summary>
public static class TargetBlocklistSqlText
{
    /// <summary>
    /// Adds one target. A target already on the list is left as it was, so the first reason and adder are kept.
    /// Parameter names match the columns.
    /// </summary>
    public const string Insert = """
        INSERT INTO target_blocklist (platform, target_ref, reason, added_by)
        VALUES (@platform, @target_ref, @reason, @added_by)
        ON CONFLICT (platform, target_ref) DO NOTHING
        """;

    /// <summary>True when the (platform, target_ref) pair is on the list. Served by the primary key.</summary>
    public const string IsBlocked = """
        SELECT EXISTS (
            SELECT 1 FROM target_blocklist
            WHERE platform = @platform AND target_ref = @target_ref
        )
        """;
}

/// <summary>
/// Targets that community actions must never reach again (doc 36, "Rate limits, audit and takedown"). Entries are
/// added, never removed: code has no remove operation, and reopening a target is an owner decision.
/// </summary>
public interface ITargetBlocklist
{
    /// <summary>True when the platform and target pair is on the list.</summary>
    Task<bool> IsBlockedAsync(string platform, string targetRef, CancellationToken ct = default);

    /// <summary>
    /// Adds the target. Adding a target that is already listed changes nothing. The first reason and adder are kept.
    /// </summary>
    Task AddAsync(string platform, string targetRef, string reason, string addedBy, CancellationToken ct = default);
}

/// <summary>Process-local blocklist. Thread-safe; cleared when the process restarts. Used by tests and by hosts without a database.</summary>
public sealed class InMemoryTargetBlocklist : ITargetBlocklist
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Platform, string TargetRef), string> _entries = new();

    /// <inheritdoc />
    public Task<bool> IsBlockedAsync(string platform, string targetRef, CancellationToken ct = default)
    {
        ValidateKey(platform, targetRef);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_entries.ContainsKey((platform, targetRef)));
        }
    }

    /// <inheritdoc />
    public Task AddAsync(string platform, string targetRef, string reason, string addedBy, CancellationToken ct = default)
    {
        ValidateKey(platform, targetRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));
        ArgumentException.ThrowIfNullOrWhiteSpace(addedBy, nameof(addedBy));
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.TryAdd((platform, targetRef), reason);
        }
        return Task.CompletedTask;
    }

    internal static void ValidateKey(string platform, string targetRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform, nameof(platform));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRef, nameof(targetRef));
    }
}

/// <summary>Raw Npgsql over the shared <see cref="NpgsqlDataSource"/>. Every value is a bound parameter.</summary>
public sealed class NpgsqlTargetBlocklist(NpgsqlDataSource dataSource) : ITargetBlocklist
{
    /// <inheritdoc />
    public async Task<bool> IsBlockedAsync(string platform, string targetRef, CancellationToken ct = default)
    {
        InMemoryTargetBlocklist.ValidateKey(platform, targetRef);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(TargetBlocklistSqlText.IsBlocked, conn);
        cmd.Parameters.AddWithValue("platform", platform);
        cmd.Parameters.AddWithValue("target_ref", targetRef);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is true;
    }

    /// <inheritdoc />
    public async Task AddAsync(string platform, string targetRef, string reason, string addedBy, CancellationToken ct = default)
    {
        InMemoryTargetBlocklist.ValidateKey(platform, targetRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));
        ArgumentException.ThrowIfNullOrWhiteSpace(addedBy, nameof(addedBy));

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(TargetBlocklistSqlText.Insert, conn);
        cmd.Parameters.AddWithValue("platform", platform);
        cmd.Parameters.AddWithValue("target_ref", targetRef);
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("added_by", addedBy);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
