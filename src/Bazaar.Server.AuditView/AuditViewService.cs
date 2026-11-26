using System.Globalization;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AuditView;

/// <summary>
/// The scoped audit viewer (doc 48 section 5.4). It reads through <see cref="IAuditViewSource"/>, keeps only rows whose
/// category the viewer's scope allows, and passes payloads through as stored, so encrypted personal fields stay ciphertext.
/// It writes nothing to the chain except one <c>audit.view</c> row per call, appended before any row is returned: if that
/// append fails the call fails and shows nothing.
/// </summary>
public sealed class AuditViewService(
    IAuditViewSource source,
    IAuditViewScopeResolver resolver,
    IAuditLog log,
    AuditViewOptions options,
    TimeProvider time) : IAuditViewService
{
    public const string ViewKind = "audit.view";

    private readonly IAuditViewSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IAuditViewScopeResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IAuditLog _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly AuditViewOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    public async Task<AuditViewPage> ViewAsync(AuditViewer viewer, AuditViewQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scope = await _resolver.ResolveAsync(viewer, ct).ConfigureAwait(false);
        if (scope.IsDenied)
        {
            await AuditAsync(viewer, query, "denied", scope, 0, ct).ConfigureAwait(false);
            return AuditViewPage.Refused(AuditViewAccess.Denied);
        }

        if (!TryCursor(query.Cursor, out var after) || !ValidRange(query) || query.PageSize is < 1)
        {
            await AuditAsync(viewer, query, "invalid", scope, 0, ct).ConfigureAwait(false);
            return AuditViewPage.Refused(AuditViewAccess.InvalidQuery);
        }

        var pageSize = Math.Min(query.PageSize ?? _options.DefaultPageSize, _options.MaxPageSize);
        after = Math.Max(after, (query.FromSeq ?? 1) - 1);
        var rows = new List<AuditViewRow>(pageSize);
        var scanned = 0;
        var lastScanned = after;
        var exhausted = false;

        while (rows.Count < pageSize && scanned < _options.MaxScanRows && !exhausted)
        {
            var limit = Math.Min(_options.MaxScanRows - scanned, Math.Max(pageSize, 100));
            var batch = await _source.ReadAsync(new AuditSourceQuery(lastScanned, query.ToSeq, query.From, query.To, limit), ct)
                .ConfigureAwait(false);
            exhausted = batch.Count < limit;
            foreach (var row in batch)
            {
                if (row.Seq <= lastScanned || (query.ToSeq is { } to && row.Seq > to))
                {
                    exhausted = true;
                    break;
                }

                scanned++;
                lastScanned = row.Seq;
                var category = _options.CategoryOf(row.Kind);
                if (!scope.Allows(category) || !KindMatches(query.Kinds, row.Kind))
                    continue;
                if ((query.From is { } f && row.Ts < f) || (query.To is { } t && row.Ts > t))
                    continue;
                rows.Add(new AuditViewRow(row.Seq, row.Ts, row.Actor, row.Kind, category, row.Payload));
                if (rows.Count == pageSize)
                    break;
            }
        }

        await AuditAsync(viewer, query, "granted", scope, rows.Count, ct).ConfigureAwait(false);
        var next = exhausted && rows.Count < pageSize ? null : lastScanned.ToString(CultureInfo.InvariantCulture);
        return new AuditViewPage(AuditViewAccess.Granted, rows, next);
    }

    private static bool ValidRange(AuditViewQuery q) =>
        (q.FromSeq is null || q.FromSeq >= 1)
        && (q.ToSeq is null || q.FromSeq is null || q.ToSeq >= q.FromSeq)
        && (q.From is null || q.To is null || q.To >= q.From);

    private static bool TryCursor(string? cursor, out long after)
    {
        after = 0;
        return cursor is null || (long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after) && after >= 0);
    }

    private static bool KindMatches(IReadOnlyList<string>? kinds, string kind)
    {
        if (kinds is null || kinds.Count == 0)
            return true;
        foreach (var k in kinds)
        {
            if (k.EndsWith('.') ? kind.StartsWith(k, StringComparison.Ordinal) : kind == k)
                return true;
        }

        return false;
    }

    private Task AuditAsync(AuditViewer viewer, AuditViewQuery query, string outcome, AuditViewScope scope, int returned, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("outcome", outcome);
            w.WriteString("viewerKind", viewer.Kind.ToString());
            w.WriteBoolean("scopeAll", scope.All);
            w.WriteStartArray("categories");
            foreach (var c in scope.Categories.Order(StringComparer.Ordinal))
                w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteNumber("returned", returned);
            if (query.FromSeq is { } f)
                w.WriteNumber("fromSeq", f);
            if (query.ToSeq is { } t)
                w.WriteNumber("toSeq", t);
            if (query.Kinds is { } kinds)
            {
                w.WriteStartArray("kinds");
                foreach (var k in kinds)
                    w.WriteStringValue(k);
                w.WriteEndArray();
            }

            if (query.Cursor is { } cur)
                w.WriteString("cursor", cur);
            w.WriteEndObject();
        }

        var payload = Encoding.UTF8.GetString(ms.ToArray());
        return _log.AppendAsync(new AuditEntry(_time.GetUtcNow(), viewer.PrincipalId.ToString("D"), ViewKind, payload), ct);
    }
}
