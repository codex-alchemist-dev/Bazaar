using Bazaar.Contracts.Gc;
using Bazaar.Server.PsychWard.Abstractions;
using Npgsql;

namespace Bazaar.Server.PsychWard;

/// <summary>
/// <see cref="IWardOwnerSource"/> over the <c>human</c> table (remote namespace, doc 32): the one active person with role
/// <c>owner</c>. There is one owner per instance in v1, enforced by a partial unique index. Null before an owner exists. Identity has
/// no owner lookup service yet; swap this when it does (identity thread, 2026-11-22).
/// </summary>
public sealed class PgWardOwnerSource(NpgsqlDataSource dataSource) : IWardOwnerSource
{
    /// <inheritdoc />
    public async Task<GcPrincipal?> GetOwnerAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, display_name FROM human WHERE role = 'owner' AND status = 'active' LIMIT 1");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new GcPrincipal(reader.GetGuid(0), PrincipalKind.Owner, reader.GetString(1))
            : null;
    }
}
