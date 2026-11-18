using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>
/// Owner-driven grant changes (doc 25 Hive row). Every change is audited. Revoking sets the grant's existing copies to
/// <c>deprecated</c> through the Vault writer port and never deletes them.
/// </summary>
public sealed class HiveGrantService(
    IHiveGrantStore grants,
    IHiveCopyStore copies,
    IHiveVaultWriter writer,
    IHiveAudit audit,
    TimeProvider clock)
{
    /// <summary>Grants <paramref name="from"/> a copy path into <paramref name="to"/> for one kind. False when it already existed.</summary>
    public async Task<bool> GrantAsync(ProjectId from, ProjectId to, string kind, string grantedBy, CancellationToken ct)
    {
        if (from.IsEmpty || to.IsEmpty || from.Equals(to))
            throw new ArgumentException("A grant needs two different projects.");
        if (!HiveKinds.All.Contains(kind))
            throw new ArgumentException($"Kind '{kind}' cannot cross projects.", nameof(kind));
        if (string.IsNullOrWhiteSpace(grantedBy))
            throw new ArgumentException("A grant records who granted it.", nameof(grantedBy));

        var now = clock.GetUtcNow();
        var added = await grants.AddAsync(new HiveGrant(from, to, kind, grantedBy, now), ct).ConfigureAwait(false);
        if (added)
            await audit.AppendAsync(new HiveAuditEvent(now, grantedBy, "hive.grant.added", Describe(from, to, kind)), ct).ConfigureAwait(false);
        return added;
    }

    /// <summary>
    /// Removes a grant, then deprecates every live copy made under it. Copies still in review were never written to the Vault, so
    /// they are only marked. False when there was no such grant (nothing is deprecated then).
    /// </summary>
    public async Task<bool> RevokeAsync(ProjectId from, ProjectId to, string kind, string revokedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(revokedBy))
            throw new ArgumentException("A revoke records who revoked it.", nameof(revokedBy));

        var removed = await grants.RemoveAsync(from, to, kind, ct).ConfigureAwait(false);
        if (!removed)
            return false;

        var now = clock.GetUtcNow();
        await audit.AppendAsync(new HiveAuditEvent(now, revokedBy, "hive.grant.revoked", Describe(from, to, kind)), ct).ConfigureAwait(false);

        foreach (var copy in await copies.ListLiveAsync(from, to, kind, ct).ConfigureAwait(false))
            await HiveCopyService.DeprecateAsync(copy, copies, writer, audit, now, revokedBy, ct).ConfigureAwait(false);
        return true;
    }

    private static string Describe(ProjectId from, ProjectId to, string kind) => $"from={from} to={to} kind={kind}";
}
