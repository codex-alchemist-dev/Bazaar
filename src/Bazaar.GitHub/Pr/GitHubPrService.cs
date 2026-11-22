using System.Text.RegularExpressions;
using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.GitHub.Pr;

/// <summary>What <see cref="GitHubPrService.ApplyAsync"/> did for one event.</summary>
/// <param name="GcId">The PR's GC.</param>
/// <param name="GcCreated">True when this call created the GC.</param>
/// <param name="Applied">True when the event's PR state was written to the read model (false for an older event).</param>
public sealed record GitHubPrApplyResult(Guid GcId, bool GcCreated, bool Applied);

/// <summary>
/// PR GC creation and the PR read model (doc 21, work package 6 <c>PrService</c>). For an event about a PR it finds or creates the
/// PR GC once, keyed by the PR node id, and records <c>github_pr</c>: repo, number, title, state, draft, head and base, author login
/// and <c>gc_id</c>. The GC id is derived from the node id and <c>github_pr.pr_node_id</c> and <c>gc_id</c> are unique, so concurrent
/// callers and replays converge on one GC and one row. Only <c>pull_request</c> events change PR state, and an older one is never
/// applied over newer state (<see cref="GitHubEventOrdering"/>), decided under a row lock. Members are the owner, the bridge, and
/// the agents that authored, were assigned or were asked to review the PR (doc 21 Event mapping); agents named by a later event
/// are added then.
/// </summary>
public sealed partial class GitHubPrService(
    NpgsqlDataSource dataSource,
    IGitHubPrGcFactory factory,
    IGitHubGcDirectory directory,
    GitHubPrGcOptions options,
    Guid creator,
    Guid? bridge,
    ILogger<GitHubPrService> logger)
{
    private static readonly Regex _sha = ShaRegex();

    /// <summary>The GC id for a PR node id. Stable, so every caller derives the same one.</summary>
    public static Guid GcIdFor(string prNodeId) => DeterministicGuid("github:pr:" + prNodeId);

    // A SHA-256 prefix of the seed, the same construction as GcCreateService.DeterministicGcId, kept here so the module needs no GC internals.
    private static Guid DeterministicGuid(string seed) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));

    /// <summary>Registers the PR on first sight and applies the event's state in order. Returns null when the event is not about a PR or cannot register one.</summary>
    public async Task<GitHubPrApplyResult?> ApplyAsync(GitHubEvent ev, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (string.IsNullOrEmpty(ev.PrNodeId))
            return null;
        var node = ev.PrNodeId;
        var gcId = GcIdFor(node);
        var created = false;

        var known = await ReadAsync(node, cancellationToken).ConfigureAwait(false);
        if (known is null)
        {
            if (ev.Number is not { } number)
            {
                LogNoNumber(logger, ev.EventId);
                return null;
            }

            created = await factory.EnsureAsync(
                gcId, TitleFor(ev, number), creator, await MembersAtCreateAsync(ev, cancellationToken).ConfigureAwait(false),
                new Dictionary<string, string> { ["provider"] = "github", ["pr_node_id"] = node, ["repo_node_id"] = ev.RepoNodeId, ["number"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                cancellationToken).ConfigureAwait(false);
        }

        var (rowGc, applied) = await UpsertAsync(ev, gcId, cancellationToken).ConfigureAwait(false);
        if (rowGc is null)
            return null;

        // Agents named by this event join an existing GC. A GC created above already holds them.
        if (!created && applied)
            foreach (var login in NamedLogins(ev))
                if (await directory.ResolveAgentAsync(login, cancellationToken).ConfigureAwait(false) is { } agent)
                    await factory.AddAgentAsync(rowGc.Value, agent, login, cancellationToken).ConfigureAwait(false);

        return new GitHubPrApplyResult(rowGc.Value, created, applied);
    }

    private async Task<(Guid? GcId, bool Applied)> UpsertAsync(GitHubEvent ev, Guid gcId, CancellationToken ct)
    {
        var isState = ev.EventName == "pull_request";
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var inserted = false;
        if (ev.Number is { } number)
        {
            // A row first made by a comment-type event holds the epoch, so the first pull_request event is never "older".
            const string insert = """
                INSERT INTO github_pr (pr_node_id, repo_node_id, number, gc_id, head_sha, base_ref, state, draft, updated_at, title, author_login)
                VALUES (@node, @repo, @number, @gc, @head, @base, COALESCE(@state, 'open'), COALESCE(@draft, false), @updated, @title, @author)
                ON CONFLICT (pr_node_id) DO NOTHING
                """;
            await using var cmd = new NpgsqlCommand(insert, conn, tx);
            Bind(cmd, ev, gcId, number, isState);
            inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }

        Guid rowGc;
        DateTimeOffset lastApplied;
        string? lastHead;
        await using (var select = new NpgsqlCommand("SELECT gc_id, updated_at, head_sha FROM github_pr WHERE pr_node_id = @node FOR UPDATE", conn, tx))
        {
            select.Parameters.AddWithValue("node", ev.PrNodeId!);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return (null, false);
            rowGc = reader.GetGuid(0);
            lastApplied = reader.GetFieldValue<DateTimeOffset>(1);
            lastHead = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();
        }

        var applied = inserted && isState;
        if (!inserted && isState)
        {
            var epoch = lastApplied == DateTimeOffset.UnixEpoch;
            applied = GitHubEventOrdering.ShouldApply(ev, epoch ? null : lastApplied, lastHead);
            if (applied)
            {
                const string update = """
                    UPDATE github_pr SET
                        title = COALESCE(@title, title),
                        state = COALESCE(@state, state),
                        draft = COALESCE(@draft, draft),
                        head_sha = COALESCE(@head, head_sha),
                        base_ref = COALESCE(@base, base_ref),
                        author_login = COALESCE(author_login, @author),
                        updated_at = @updated
                    WHERE pr_node_id = @node
                    """;
                await using var cmd = new NpgsqlCommand(update, conn, tx);
                Bind(cmd, ev, gcId, ev.Number ?? 1, true);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (rowGc, applied);
    }

    private static void Bind(NpgsqlCommand cmd, GitHubEvent ev, Guid gcId, int number, bool isState)
    {
        var state = isState ? StateOf(ev) : null;
        cmd.Parameters.AddWithValue("node", ev.PrNodeId!);
        cmd.Parameters.AddWithValue("repo", ev.RepoNodeId);
        cmd.Parameters.AddWithValue("number", number);
        cmd.Parameters.AddWithValue("gc", gcId);
        cmd.Parameters.AddWithValue("head", (object?)ValidSha(ev.HeadSha) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("base", (object?)NonBlank(ev.BaseRef) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("state", (object?)state ?? DBNull.Value);
        cmd.Parameters.AddWithValue("draft", (object?)DraftOf(ev) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("updated", isState ? ev.UpdatedAt : DateTimeOffset.UnixEpoch);
        cmd.Parameters.AddWithValue("title", (object?)Truncate(NonBlank(ev.Title), 1024) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("author", (object?)Truncate(NonBlank(ev.PrAuthorLogin), 256) ?? DBNull.Value);
    }

    /// <summary>The PR state this event sets, or null to keep the stored one. The payload's own state wins over the action.</summary>
    internal static string? StateOf(GitHubEvent ev) =>
        NonBlank(ev.PrState) is { } s ? s.ToLowerInvariant()
        : ev.Action switch
        {
            "opened" or "reopened" => "open",
            "closed" => ev.Merged ? "merged" : "closed",
            _ => null,
        };

    private static bool? DraftOf(GitHubEvent ev) =>
        ev.Draft ?? ev.Action switch { "converted_to_draft" => true, "ready_for_review" => false, _ => null };

    private async Task<Guid[]> MembersAtCreateAsync(GitHubEvent ev, CancellationToken ct)
    {
        var members = new List<Guid> { creator };
        if (bridge is { } b)
            members.Add(b);
        var agents = 0;
        foreach (var login in NamedLogins(ev))
        {
            if (agents >= options.MaxAgentMembersAtCreate)
            {
                LogCapped(logger, ev.PrNodeId!);
                break;
            }

            if (await directory.ResolveAgentAsync(login, ct).ConfigureAwait(false) is { } agent && !members.Contains(agent))
            {
                members.Add(agent);
                agents++;
            }
        }

        return [.. members.Distinct()];
    }

    /// <summary>The PR author, the assignees and the requested reviewers, once each. The mapper's inbox rows name the same logins.</summary>
    private static IEnumerable<string> NamedLogins(GitHubEvent ev) =>
        new[] { ev.PrAuthorLogin, ev.ReviewerLogin }.Concat(ev.AssigneeLogins)
            .Where(static l => !string.IsNullOrWhiteSpace(l)).Select(static l => l!).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string TitleFor(GitHubEvent ev, int number) =>
        $"{ev.RepoFullName ?? ev.RepoNodeId}#{number} {ev.Title}".Trim();

    private async Task<Guid?> ReadAsync(string node, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT gc_id FROM github_pr WHERE pr_node_id = @node");
        cmd.Parameters.AddWithValue("node", node);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as Guid?;
    }

    private static string? ValidSha(string? sha) => sha is not null && _sha.IsMatch(sha) ? sha : null;

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Truncate(string? text, int max) => text is not null && text.Length > max ? text[..max] : text;

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex ShaRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub event {EventId} names a PR that is not registered and carries no PR number; the PR GC is created by a later event.")]
    private static partial void LogNoNumber(ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "PR {PrNodeId} names more agents than the create cap; the rest are added by their own events.")]
    private static partial void LogCapped(ILogger logger, string prNodeId);
}

/// <summary>
/// Decorates the real sink: the PR GC and read model are brought up to date first, so the sink's directory finds the GC
/// (<c>github_pr.gc_id</c>). A throw leaves the event unprocessed and it is delivered again, which the service's idempotence allows.
/// </summary>
public sealed class GitHubPrGcEventSink(IGitHubEventSink inner, GitHubPrService prService) : IGitHubEventSink
{
    public async Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await prService.ApplyAsync(delivery.Event, cancellationToken).ConfigureAwait(false);
        await inner.DeliverAsync(delivery, cancellationToken).ConfigureAwait(false);
    }
}
