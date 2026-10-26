using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Soul;
using Npgsql;

namespace Bazaar.Server.Souls;

/// <summary>
/// Append-only version chain for a Soul over <c>soul_versions</c> (migration 0014, docs/design/07-soul-system.md).
/// An append takes a per-Soul advisory lock, reads the head, and refuses unless the caller's expected version is still
/// the head. The primary key is the backstop: a concurrent insert that gets past the lock still cannot fork the chain.
/// </summary>
public sealed partial class SoulVersioningService(NpgsqlDataSource dataSource)
{
    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";

    private const string _headSql = "SELECT COALESCE(max(version), 0)::int FROM soul_versions WHERE soul_id = @soul_id";

    private const string _insertSql = """
        INSERT INTO soul_versions (soul_id, version, parent_version, charter_sha256, preferences, content_sha256, author, reason, decision_ref)
        VALUES (@soul_id, @version, @parent_version, @charter_sha256, @preferences, @content_sha256, @author, @reason, @decision_ref)
        RETURNING created_at
        """;

    private const string _selectSql = """
        SELECT soul_id, version, parent_version, charter_sha256, preferences, content_sha256, author, reason, decision_ref, created_at
        FROM soul_versions
        """;

    /// <summary>Appends one version. Throws <see cref="SoulVersionConflictException"/> when the expected version is not the head.</summary>
    public async Task<SoulVersion> AppendAsync(SoulVersionAppend append, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(append);
        ValidateAppend(append);
        var preferences = SoulCanonicalJson.Canonicalize(append.PreferencesJson);
        var contentSha256 = ContentHash(append.CharterSha256, preferences);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = _lockSql;
            lockCmd.Parameters.AddWithValue("lock_key", $"soul_version:{append.SoulId:D}");
            await lockCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var head = await ReadHeadAsync(conn, tx, append.SoulId, cancellationToken).ConfigureAwait(false);
        if (head != append.ExpectedVersion)
            throw new SoulVersionConflictException(append.SoulId, append.ExpectedVersion, head);

        var version = head + 1;
        var decisionRef = string.IsNullOrWhiteSpace(append.DecisionRef) ? null : append.DecisionRef.Trim();
        var author = append.Author.Trim();
        var reason = append.Reason.Trim();
        DateTimeOffset createdAt;
        try
        {
            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = _insertSql;
            insert.Parameters.AddWithValue("soul_id", append.SoulId);
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("parent_version", version == 1 ? DBNull.Value : head);
            insert.Parameters.AddWithValue("charter_sha256", append.CharterSha256);
            insert.Parameters.AddWithValue("preferences", preferences);
            insert.Parameters.AddWithValue("content_sha256", contentSha256);
            insert.Parameters.AddWithValue("author", author);
            insert.Parameters.AddWithValue("reason", reason);
            insert.Parameters.AddWithValue("decision_ref", (object?)decisionRef ?? DBNull.Value);
            var stamp = (DateTime)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            createdAt = new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new SoulVersionConflictException(append.SoulId, append.ExpectedVersion, null);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SoulVersion(
            append.SoulId,
            version,
            version == 1 ? null : head,
            append.CharterSha256,
            preferences,
            contentSha256,
            author,
            reason,
            decisionRef,
            createdAt);
    }

    /// <summary>The head version number, or 0 when the Soul has no versions yet.</summary>
    public async Task<int> GetHeadAsync(Guid soulId, CancellationToken cancellationToken = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadHeadAsync(conn, null, soulId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One version, or null when it does not exist.</summary>
    public async Task<SoulVersion?> GetAsync(Guid soulId, int version, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_selectSql + " WHERE soul_id = @soul_id AND version = @version");
        cmd.Parameters.AddWithValue("soul_id", soulId);
        cmd.Parameters.AddWithValue("version", version);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>All versions of one Soul, oldest first.</summary>
    public async Task<IReadOnlyList<SoulVersion>> ListAsync(Guid soulId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_selectSql + " WHERE soul_id = @soul_id ORDER BY version");
        cmd.Parameters.AddWithValue("soul_id", soulId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<SoulVersion>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            versions.Add(Map(reader));
        return versions;
    }

    /// <summary>SHA-256 (lowercase hex) over the charter hash, a newline, and the canonical preferences.</summary>
    public static string ContentHash(string charterSha256, string canonicalPreferences) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{charterSha256}\n{canonicalPreferences}")));

    private static async Task<int> ReadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid soulId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = _headSql;
        cmd.Parameters.AddWithValue("soul_id", soulId);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static SoulVersion Map(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetInt32(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetFieldValue<DateTimeOffset>(9));

    private static void ValidateAppend(SoulVersionAppend append)
    {
        if (append.SoulId == Guid.Empty)
            throw new ArgumentException("A Soul id is required.", nameof(append));
        if (append.ExpectedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(append), append.ExpectedVersion, "The expected version cannot be negative.");
        if (!Sha256Pattern().IsMatch(append.CharterSha256))
            throw new ArgumentException("The charter hash must be lowercase hex SHA-256.", nameof(append));
        if (string.IsNullOrWhiteSpace(append.Author))
            throw new ArgumentException("An author is required.", nameof(append));
        if (string.IsNullOrWhiteSpace(append.Reason))
            throw new ArgumentException("A reason is required.", nameof(append));
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}
