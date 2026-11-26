using System.Globalization;
using System.Text;
using Bazaar.Contracts.GitHub;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Ingress;
using Npgsql;

namespace Bazaar.Server.GitHub.Wire;

/// <summary>
/// The PR read model over <c>github_pr</c> (doc 21, Client PR wire). The directory comes from the table the PR service fills; checks and
/// the changed-file list come from the adapter at the stored head, so a client never sees a checks state for another head. A PR the table
/// does not know, or one the PR service has not yet given a repo name, is unknown: it is never guessed from GitHub.
/// </summary>
/// <param name="dataSource">The shared database source.</param>
/// <param name="adapter">The GitHub adapter, for checks and files.</param>
/// <param name="directory">Tells an agent author from a human one.</param>
/// <param name="installationId">The installation that reads the watched repos (<c>Bazaar:GitHub:Ingress:InstallationId</c>; PROPOSED, doc 21).</param>
public sealed class PgGitHubPrReadModel(NpgsqlDataSource dataSource, IGitHubAdapter adapter, IGitHubGcDirectory directory, long installationId) : IGitHubPrReadModel
{
    /// <summary>Largest page <see cref="ListAsync"/> returns.</summary>
    public const int MaxPageSize = 50;

    /// <summary>Page size when the request names none.</summary>
    public const int DefaultPageSize = 30;

    /// <inheritdoc />
    public async Task<PrRef?> ResolveAsync(string prId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(prId);
        await using var cmd = dataSource.CreateCommand("SELECT repo_full_name, number FROM github_pr WHERE pr_node_id = @id");
        cmd.Parameters.AddWithValue("id", prId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ToRef(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1))
            : null;
    }

    /// <inheritdoc />
    public async Task<PrListReply> ListAsync(PrListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = Math.Clamp(request.Limit ?? DefaultPageSize, 1, MaxPageSize);
        var after = PrListCursor.TryDecode(request.Cursor, out var cursor) ? cursor : null;

        var sql = new StringBuilder("""
            SELECT pr_node_id, repo_full_name, number, title, author_login, draft, head_sha, revision, updated_at
            FROM github_pr
            WHERE repo_full_name IS NOT NULL AND head_sha IS NOT NULL
            """);
        await using var cmd = dataSource.CreateCommand();
        if (StatesOf(request.State) is { } states)
        {
            sql.Append(" AND state = ANY(@states)");
            cmd.Parameters.AddWithValue("states", states);
        }

        if (!string.IsNullOrWhiteSpace(request.Repo))
        {
            sql.Append(" AND lower(repo_full_name) = lower(@repo)");
            cmd.Parameters.AddWithValue("repo", request.Repo.Trim());
        }

        if (after is not null)
        {
            sql.Append(" AND (updated_at, pr_node_id) < (@at, @node)");
            cmd.Parameters.AddWithValue("at", after.UpdatedAt);
            cmd.Parameters.AddWithValue("node", after.PrNodeId);
        }

        sql.Append(" ORDER BY updated_at DESC, pr_node_id DESC LIMIT @take");
        cmd.Parameters.AddWithValue("take", limit + 1);
        cmd.CommandText = sql.ToString();

        var rows = new List<(string Node, string Repo, int Number, string? Title, string? Author, bool Draft, string Head, long Revision, DateTimeOffset UpdatedAt)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetString(6).Trim(), reader.GetInt64(7),
                    reader.GetFieldValue<DateTimeOffset>(8)));
            }
        }

        var more = rows.Count > limit;
        var page = more ? rows.Take(limit).ToList() : rows;
        var result = new List<PrListRow>(page.Count);
        foreach (var row in page)
        {
            var pr = ToRef(row.Repo, row.Number)!;
            var checks = await GetChecksAsync(pr, row.Head, cancellationToken).ConfigureAwait(false);
            var author = row.Author ?? "unknown";
            var kind = row.Author is not null && await directory.ResolveAgentAsync(row.Author, cancellationToken).ConfigureAwait(false) is not null
                ? PrAuthorKind.Agent
                : PrAuthorKind.Human;
            result.Add(new PrListRow(row.Node, row.Repo, row.Number, row.Title ?? $"#{row.Number}", author, kind, row.Draft, row.Head, checks, row.Revision,
                pr.InstallationId, pr.Owner, pr.Repo));
        }

        var last = page.Count > 0 ? page[^1] : default;
        return new PrListReply(result, more ? PrListCursor.Encode(new PrListCursor(last.UpdatedAt, last.Node)) : null);
    }

    /// <inheritdoc />
    public async Task<PrSummaryReply?> GetSummaryAsync(string prId, PrRef pr, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(prId);
        ArgumentNullException.ThrowIfNull(pr);
        await using var cmd = dataSource.CreateCommand("SELECT head_sha, task_id FROM github_pr WHERE pr_node_id = @id AND head_sha IS NOT NULL");
        cmd.Parameters.AddWithValue("id", prId);
        string head;
        Guid? card;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            head = reader.GetString(0).Trim();
            card = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }

        var files = await adapter.GetPrFilesAsync(pr, head, cancellationToken).ConfigureAwait(false);
        var changed = files.Select(f => new PrChangedFile(f.Path, f.Status, f.Additions, f.Deletions)).ToList();
        var checks = await GetChecksAsync(pr, head, cancellationToken).ConfigureAwait(false);
        return new PrSummaryReply(prId, head, SummaryText(changed), changed, checks, ReviewerApproved: false, card,
            pr.InstallationId, pr.Owner, pr.Repo, pr.Number);
    }

    /// <inheritdoc />
    public async Task<PrChecksRollup> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        return ToRollup(await adapter.GetChecksStateAsync(pr, headSha, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The one-line summary text: no author-written summary is stored, so it states the size of the change.</summary>
    internal static string SummaryText(IReadOnlyList<PrChangedFile> files)
    {
        var added = files.Sum(f => f.Additions);
        var removed = files.Sum(f => f.Deletions);
        return string.Create(CultureInfo.InvariantCulture, $"{files.Count} {(files.Count == 1 ? "file" : "files")} changed, +{added} -{removed}");
    }

    internal static PrChecksRollup ToRollup(PrChecksState state) => state switch
    {
        PrChecksState.Passing => PrChecksRollup.Passing,
        PrChecksState.Failing => PrChecksRollup.Failing,
        PrChecksState.Pending => PrChecksRollup.Pending,
        _ => PrChecksRollup.None,
    };

    private PrRef? ToRef(string? fullName, int number)
    {
        var slash = fullName?.IndexOf('/') ?? -1;
        if (fullName is null || installationId <= 0 || slash <= 0)
        {
            return null;
        }

        return new PrRef(installationId, fullName[..slash], fullName[(slash + 1)..], number);
    }

    private static string[]? StatesOf(PrListState state) => state switch
    {
        PrListState.Open => ["open"],
        PrListState.Closed => ["closed"],
        PrListState.Merged => ["merged"],
        _ => null,
    };
}

/// <summary>The opaque <c>pr.list</c> paging cursor: the last row's sort key. Decoding never throws; a bad cursor is the first page.</summary>
/// <param name="UpdatedAt">The last row's <c>updated_at</c>.</param>
/// <param name="PrNodeId">The last row's node id.</param>
internal sealed record PrListCursor(DateTimeOffset UpdatedAt, string PrNodeId)
{
    public static string Encode(PrListCursor cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(cursor.UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + cursor.PrNodeId));

    public static bool TryDecode(string? text, out PrListCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            var bar = raw.IndexOf('|', StringComparison.Ordinal);
            if (bar <= 0 || bar == raw.Length - 1 || !long.TryParse(raw[..bar], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            cursor = new PrListCursor(new DateTimeOffset(ticks, TimeSpan.Zero), raw[(bar + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The read model of a host with no database: it knows no PR, so the wire lists nothing and answers not_found.</summary>
public sealed class EmptyGitHubPrReadModel : IGitHubPrReadModel
{
    /// <inheritdoc />
    public Task<PrRef?> ResolveAsync(string prId, CancellationToken cancellationToken) => Task.FromResult<PrRef?>(null);

    /// <inheritdoc />
    public Task<PrListReply> ListAsync(PrListRequest request, CancellationToken cancellationToken) => Task.FromResult(new PrListReply([], null));

    /// <inheritdoc />
    public Task<PrSummaryReply?> GetSummaryAsync(string prId, PrRef pr, CancellationToken cancellationToken) => Task.FromResult<PrSummaryReply?>(null);

    /// <inheritdoc />
    public Task<PrChecksRollup> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => Task.FromResult(PrChecksRollup.None);
}
