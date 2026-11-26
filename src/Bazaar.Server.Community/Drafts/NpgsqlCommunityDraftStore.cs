using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Community;
using Bazaar.Server.Community.Gates;
using Bazaar.Server.Community.Profile;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Community.Drafts;

/// <summary>Statements for <c>community_draft</c> (migration community/0005). Constants, so no statement is built from input.</summary>
public static class CommunityDraftSqlText
{
    private const string _columns =
        "draft_id, agent_id, sponsor_id, action, platform, kind, target, title, body, body_sha256, head_branch, base_branch, " +
        "gate_input::text, profile::text, approval_id, approval_hash, approval_expires_at, gate_failures::text, status, " +
        "taint_sources::text, external_id, url, reused, superseded_by";

    /// <summary>One draft by id.</summary>
    public const string Select = "SELECT " + _columns + " FROM community_draft WHERE draft_id = @draft_id";

    /// <summary>Open pending drafts, newest first. Served by the (status, created_at) index.</summary>
    public const string ListPending =
        "SELECT " + _columns + " FROM community_draft WHERE status = 'Pending' AND approval_expires_at > @open_at " +
        "ORDER BY created_at DESC, draft_id DESC LIMIT @limit";

    /// <summary>Inserts one draft. A repeated id inserts nothing (zero rows affected).</summary>
    public const string Insert = """
        INSERT INTO community_draft
            (draft_id, agent_id, sponsor_id, action, platform, kind, target, title, body, body_sha256, head_branch, base_branch,
             gate_input, profile, approval_id, approval_hash, approval_expires_at, gate_failures, status, taint_sources,
             external_id, url, reused, superseded_by)
        VALUES
            (@draft_id, @agent_id, @sponsor_id, @action, @platform, @kind, @target, @title, @body, @body_sha256, @head_branch, @base_branch,
             CAST(@gate_input AS jsonb), CAST(@profile AS jsonb), @approval_id, @approval_hash, @approval_expires_at,
             CAST(@gate_failures AS jsonb), @status, CAST(@taint_sources AS jsonb), @external_id, @url, @reused, @superseded_by)
        ON CONFLICT (draft_id) DO NOTHING
        """;

    /// <summary>Compare-and-set: replaces every mutable column only while the stored status is still @expected.</summary>
    public const string Replace = """
        UPDATE community_draft SET
            agent_id = @agent_id, sponsor_id = @sponsor_id, action = @action, platform = @platform, kind = @kind, target = @target,
            title = @title, body = @body, body_sha256 = @body_sha256, head_branch = @head_branch, base_branch = @base_branch,
            gate_input = CAST(@gate_input AS jsonb), profile = CAST(@profile AS jsonb), approval_id = @approval_id,
            approval_hash = @approval_hash, approval_expires_at = @approval_expires_at,
            gate_failures = CAST(@gate_failures AS jsonb), status = @status, taint_sources = CAST(@taint_sources AS jsonb),
            external_id = @external_id, url = @url, reused = @reused, superseded_by = @superseded_by, updated_at = now()
        WHERE draft_id = @draft_id AND status = @expected
        """;

    /// <summary>[PROPOSED] Expires every due Pending draft in one statement and returns the ids. The status test makes it a compare-and-set.</summary>
    public const string ExpireDue =
        "UPDATE community_draft SET status = 'Expired', updated_at = now() " +
        "WHERE status = 'Pending' AND approval_expires_at <= @now RETURNING draft_id";

    /// <summary>First half of a supersede: Pending to Edited, pointing at the replacement.</summary>
    public const string MarkEdited =
        "UPDATE community_draft SET status = 'Edited', superseded_by = @superseded_by, updated_at = now() " +
        "WHERE draft_id = @draft_id AND status = 'Pending'";
}

/// <summary>
/// Raw Npgsql store over the shared <see cref="NpgsqlDataSource"/> (doc 36 work package 8). Every value is a bound parameter. A
/// status change that the draft lifecycle does not allow throws <see cref="InvalidOperationException"/> before any SQL runs.
/// Holds per-person content: nothing here logs a body.
/// </summary>
public sealed class NpgsqlCommunityDraftStore(NpgsqlDataSource dataSource) : ICommunityDraftStore
{
    // Pending -> Posting | Denied | Expired; Posting -> Posted | Failed | Pending (a retryable adapter failure puts it back).
    // Pending -> Edited goes through TrySupersedeAsync only.
    private static readonly Dictionary<CommunityDraftStatus, CommunityDraftStatus[]> _allowed = new()
    {
        [CommunityDraftStatus.Pending] = [CommunityDraftStatus.Posting, CommunityDraftStatus.Denied, CommunityDraftStatus.Expired],
        [CommunityDraftStatus.Posting] = [CommunityDraftStatus.Posted, CommunityDraftStatus.Failed, CommunityDraftStatus.Pending],
    };

    /// <inheritdoc />
    public async Task<CommunityDraft?> GetAsync(Guid draftId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(CommunityDraftSqlText.Select);
        cmd.Parameters.AddWithValue("draft_id", draftId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(CommunityDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var cmd = dataSource.CreateCommand(CommunityDraftSqlText.Insert);
        Bind(cmd, draft);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryReplaceAsync(Guid draftId, CommunityDraftStatus expected, CommunityDraft updated, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (updated.DraftId != draftId) throw new ArgumentException("The updated draft has a different id.", nameof(updated));
        if (!_allowed.TryGetValue(expected, out var next) || !next.Contains(updated.Status))
            throw new InvalidOperationException($"A draft cannot move from {expected} to {updated.Status}.");

        await using var cmd = dataSource.CreateCommand(CommunityDraftSqlText.Replace);
        Bind(cmd, updated);
        cmd.Parameters.AddWithValue("expected", expected.ToString());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TrySupersedeAsync(Guid originalId, CommunityDraft replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.DraftId == originalId) throw new ArgumentException("The replacement needs its own id.", nameof(replacement));

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var mark = new NpgsqlCommand(CommunityDraftSqlText.MarkEdited, conn, tx))
        {
            mark.Parameters.AddWithValue("draft_id", originalId);
            mark.Parameters.AddWithValue("superseded_by", replacement.DraftId);
            if (await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        await using (var insert = new NpgsqlCommand(CommunityDraftSqlText.Insert, conn, tx))
        {
            Bind(insert, replacement);
            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CommunityDraft>> ListPendingAsync(DateTimeOffset openAt, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var rows = new List<CommunityDraft>();
        await using var cmd = dataSource.CreateCommand(CommunityDraftSqlText.ListPending);
        cmd.Parameters.AddWithValue("open_at", openAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Map(reader));
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var ids = new List<Guid>();
        await using var cmd = dataSource.CreateCommand(CommunityDraftSqlText.ExpireDue);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static void Bind(NpgsqlCommand cmd, CommunityDraft d)
    {
        var p = cmd.Parameters;
        p.AddWithValue("draft_id", d.DraftId);
        p.AddWithValue("agent_id", d.AgentId);
        p.AddWithValue("sponsor_id", d.SponsorId);
        p.AddWithValue("action", d.Action.ToString());
        p.AddWithValue("platform", d.Post.Platform);
        p.AddWithValue("kind", d.Post.Kind);
        p.AddWithValue("target", d.Post.Target);
        p.AddWithValue("title", OrNull(d.Post.Title));
        p.AddWithValue("body", d.Post.Body);
        p.AddWithValue("body_sha256", d.Post.BodySha256);
        p.AddWithValue("head_branch", OrNull(d.Post.HeadBranch));
        p.AddWithValue("base_branch", OrNull(d.Post.BaseBranch));
        p.AddWithValue("gate_input", JsonSerializer.Serialize(d.GateInput, CommunityDraftJson.Default.DraftPost));
        p.AddWithValue("profile", JsonSerializer.Serialize(ProfileRow.From(d.Profile), CommunityDraftJson.Default.ProfileRow));
        p.AddWithValue("approval_id", d.ApprovalId);
        p.AddWithValue("approval_hash", d.ApprovalHash);
        p.AddWithValue("approval_expires_at", d.ApprovalExpiresAt.ToUniversalTime());
        p.AddWithValue("gate_failures", JsonSerializer.Serialize(d.GateFailures.ToList(), CommunityDraftJson.Default.ListGateFailure));
        p.AddWithValue("status", d.Status.ToString());
        p.AddWithValue("taint_sources", d.TaintSourcesJson);
        p.AddWithValue("external_id", OrNull(d.ExternalId));
        p.AddWithValue("url", OrNull(d.Url));
        p.AddWithValue("reused", d.Reused);
        p.Add(new NpgsqlParameter("superseded_by", NpgsqlDbType.Uuid) { Value = d.SupersededBy is { } id ? id : DBNull.Value });
    }

    private static object OrNull(string? value) => value is null ? DBNull.Value : value;

    private static CommunityDraft Map(NpgsqlDataReader r)
    {
        var draftId = r.GetGuid(0);
        var post = new OutboundPost(
            draftId, r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
            r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11));
        var gateInput = JsonSerializer.Deserialize(r.GetString(12), CommunityDraftJson.Default.DraftPost)
            ?? throw new InvalidDataException("community_draft.gate_input is empty.");
        var profile = JsonSerializer.Deserialize(r.GetString(13), CommunityDraftJson.Default.ProfileRow)?.ToProfile()
            ?? throw new InvalidDataException("community_draft.profile is empty.");
        var failures = JsonSerializer.Deserialize(r.GetString(17), CommunityDraftJson.Default.ListGateFailure) ?? [];
        return new CommunityDraft(
            draftId, r.GetString(1), r.GetString(2), Enum.Parse<CommunityAction>(r.GetString(3)), post, gateInput, profile,
            r.GetGuid(14), r.GetFieldValue<byte[]>(15), r.GetFieldValue<DateTimeOffset>(16), failures,
            Enum.Parse<CommunityDraftStatus>(r.GetString(18)), r.GetString(19),
            r.IsDBNull(20) ? null : r.GetString(20), r.IsDBNull(21) ? null : r.GetString(21), r.GetBoolean(22),
            r.IsDBNull(23) ? null : r.GetGuid(23));
    }
}

/// <summary>Stored form of a <see cref="BotProfile"/>: the disclosure URL as text.</summary>
internal sealed record ProfileRow(string AgentId, string Platform, string DisplayName, string SponsorName, string Contact, string DisclosureUrl)
{
    public static ProfileRow From(BotProfile p) => new(p.AgentId, p.Platform, p.DisplayName, p.SponsorName, p.Contact, p.DisclosureUrl.ToString());

    public BotProfile ToProfile() => new(AgentId, Platform, DisplayName, SponsorName, Contact, new Uri(DisclosureUrl, UriKind.Absolute));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DraftPost))]
[JsonSerializable(typeof(ProfileRow))]
[JsonSerializable(typeof(List<GateFailure>))]
internal sealed partial class CommunityDraftJson : JsonSerializerContext;
