using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.MemberRoster;

/// <summary>
/// The members section's read port (docs/design/51-web-admin-module.md section 5). Limit is clamped to 1 to 200 and offset to zero or more.
/// Names, roles and model labels pass through a character filter, so a hostile display name cannot inject markup into a log or page.
/// </summary>
public sealed class AdminMemberRoster(IMemberRowSource source) : IAdminMemberRoster
{
    public const int MaxLimit = 200;
    private const int _maxLabel = 200;

    public async Task<AdminMemberPage> ListAsync(string? kind, int limit, int offset, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        var skip = Math.Max(offset, 0);
        var page = await source.FetchAsync(kind, take, skip, ct).ConfigureAwait(false);
        var rows = page.Rows
            .Take(take)
            .Select(r => new AdminMember(r.Id, Clean(r.Name), Clean(r.Kind), Clean(r.Role), Clean(r.Status), r.LastSeen, r.Model is null ? null : Clean(r.Model), Math.Max(r.Tokens24h, 0)))
            .ToList();
        var next = (long)skip + rows.Count;
        return new AdminMemberPage(rows, page.Total, next < page.Total && rows.Count > 0 ? (int)next : null);
    }

    /// <summary>Control characters and angle brackets become '_'. Capped in length.</summary>
    internal static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Create(Math.Min(value.Length, _maxLabel), value, static (dst, src) =>
        {
            for (var i = 0; i < dst.Length; i++)
            {
                var c = src[i];
                dst[i] = char.IsControl(c) || c is '<' or '>' ? '_' : c;
            }
        });
    }
}
