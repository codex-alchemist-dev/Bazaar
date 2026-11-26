using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.AuditReader;

/// <summary>
/// The audit section's read port (docs/design/51-web-admin-module.md section 5). Newest first; <c>before</c> is an exclusive seq
/// cursor and <c>NextBefore</c> is the last returned seq when more rows exist. Summaries are built from the kind and a
/// character-restricted "action" label only, never from raw payloads.
/// </summary>
public sealed class AdminAuditReader(IAuditRowSource source) : IAdminAuditReader
{
    public const int MaxLimit = 200;
    private const int _maxLabel = 64;

    public async Task<AdminAuditPage> ReadAsync(long? before, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        var fetched = await source.FetchAsync(before, take + 1, ct).ConfigureAwait(false);
        var page = fetched.Take(take).ToList();
        var rows = page.Select(r => new AdminAuditRow(r.Seq, r.Ts, Label(r.Actor), Label(r.Kind), Summarise(r))).ToList();
        return new AdminAuditPage(rows, fetched.Count > take && page.Count > 0 ? page[^1].Seq : null);
    }

    internal static string Summarise(AuditRawRow row)
    {
        var kind = Label(row.Kind);
        return row.Action is { Length: > 0 } action ? $"{kind}: {Label(action)}" : kind;
    }

    /// <summary>Keeps letters, digits and a few separators; anything else (spaces, quotes) becomes '_'. Capped in length.</summary>
    internal static string Label(string? value)
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
                dst[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/' ? c : '_';
            }
        });
    }
}
