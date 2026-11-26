namespace Bazaar.Server.AuditReader;

/// <summary>One audit row as stored, cut down to what the admin panel may show: no payload, no hashes. <c>Action</c> is the payload's top-level "action" string, if any.</summary>
public sealed record AuditRawRow(long Seq, DateTimeOffset Ts, string Actor, string Kind, string? Action);

/// <summary>The data seam under <see cref="AdminAuditReader"/>: newest first, rows with seq below <c>before</c> when given.</summary>
public interface IAuditRowSource
{
    Task<IReadOnlyList<AuditRawRow>> FetchAsync(long? before, int limit, CancellationToken ct);
}
