namespace Bazaar.Server.MemberRoster;

/// <summary>One member as stored, cut down to what the admin panel may show.</summary>
public sealed record MemberRawRow(Guid Id, string Name, string Kind, string Role, string Status, DateTimeOffset? LastSeen, string? Model, long Tokens24h);

/// <summary>A page from the data seam plus the total row count for the filter.</summary>
public sealed record MemberRawPage(IReadOnlyList<MemberRawRow> Rows, long Total);

/// <summary>The data seam under <see cref="AdminMemberRoster"/>: ordered by kind (agents first), then name, then id.</summary>
public interface IMemberRowSource
{
    Task<MemberRawPage> FetchAsync(string? kind, int limit, int offset, CancellationToken ct);
}
