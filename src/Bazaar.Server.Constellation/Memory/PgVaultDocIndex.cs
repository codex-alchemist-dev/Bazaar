using System.Data;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>Postgres <see cref="IVaultDocIndex"/> over <c>vault_docs</c> and <c>vault_links</c> (common/0009, read-only).</summary>
/// <param name="dataSource">The shared data source.</param>
/// <param name="vaultId">The Vault to read (<c>Constellation:VaultId</c>), or null for any Vault.</param>
public sealed class PgVaultDocIndex(NpgsqlDataSource dataSource, Guid? vaultId = null) : IVaultDocIndex
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string _linksSql = """
        SELECT DISTINCT target_doc
        FROM vault_links
        WHERE src_doc = $1 AND target_doc IS NOT NULL
        ORDER BY target_doc
        """;

    /// <inheritdoc />
    public async Task<VaultIndexedDoc?> FindAsync(string noteId, string vaultPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        var candidates = new List<(Guid DocId, string? FrontMatterId)>(2);
        await using (var cmd = new NpgsqlCommand(MemoryDocIdResolution.CandidatesSql, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = vaultPath });
            cmd.Parameters.Add(MemoryDocIdResolution.VaultIdParameter(vaultId));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                candidates.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        var (docId, _) = MemoryDocIdResolution.Decide(noteId, candidates);
        if (docId is not { } found)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        var targets = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(_linksSql, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = found });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                targets.Add(reader.GetGuid(0));
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new VaultIndexedDoc(found, candidates[0].FrontMatterId, targets);
    }
}
