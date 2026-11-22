using System.Text.RegularExpressions;
using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Bazaar.GitHub.Pr;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.GitHub.Issues;

/// <summary>What <see cref="GitHubIssueService.ApplyAsync"/> did for one event.</summary>
/// <param name="GcId">The issue's GC.</param>
/// <param name="GcCreated">True when this call created the GC.</param>
/// <param name="Applied">True when the event's issue state was written to the read model (false for an older event or a non-<c>issues</c> event).</param>
public sealed record GitHubIssueApplyResult(Guid GcId, bool GcCreated, bool Applied);

/// <summary>
/// Issue GC creation and the issue read model (doc 21, issue GCs), the sibling of the PR service. For an event about an issue it
/// finds or creates the issue GC once, keyed by the issue node id, and records <c>github_issue</c>. The GC id is derived from the node
/// id and <c>issue_node_id</c> and <c>gc_id</c> are unique, so concurrent callers and replays converge on one GC and one row. Only
/// <c>issues</c> events change state, and an older one is never applied over newer state (<see cref="GitHubEventOrdering"/>), decided
/// under a row lock. Members are the owner, the bridge, and the agents that authored the issue, were assigned, or (for a member's
/// content only) were @-mentioned; an agent named later is added then.
/// A GC is created only when the event is a member's, or names an agent through the issue's own fields: a stranger's issue that no
/// agent is involved in stays in the Inbox GC (doc 21 mapping) and gets no GC of its own.
/// </summary>
public sealed partial class GitHubIssueService(
    NpgsqlDataSource dataSource,
    IGitHubIssueGcFactory factory,
    IGitHubGcDirectory directory,
    GitHubIssueGcOptions options,
    Guid creator,
    Guid? bridge,
    ILogger<GitHubIssueService> logger,
    IGitHubOwnerResolver? ownerResolver = null)
{
    /// <summary>The GC id for an issue node id. Stable, so every caller derives the same one.</summary>
    public static Guid GcIdFor(string issueNodeId) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("github:issue:" + issueNodeId)).AsSpan(0, 16));

    /// <summary>Registers the issue on first sight and applies the event's state in order. Returns null when the event is not about an issue or cannot register one.</summary>
    public async Task<GitHubIssueApplyResult?> ApplyAsync(GitHubEvent ev, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (string.IsNullOrEmpty(ev.IssueNodeId))
            return null;
        var node = ev.IssueNodeId;
        var gcId = GcIdFor(node);
        var trusted = GitHubEventMapper.IsTrustedAssociation(ev.AuthorAssociation);
        var created = false;
        Guid? owner = null;

        var known = await ReadAsync(node, cancellationToken).ConfigureAwait(false);
        if (known is null)
        {
            if (ev.IssueNumber is not { } number)
            {
                LogNoNumber(logger, ev.EventId);
                return null;
            }

            owner = ownerResolver is null ? null : await ownerResolver.GetOwnerPrincipalIdAsync(cancellationToken).ConfigureAwait(false);
            var (members, agents) = await MembersAtCreateAsync(ev, trusted, owner, cancellationToken).ConfigureAwait(false);
            if (!trusted && agents == 0)
            {
                LogNoAgentInvolved(logger, ev.EventId);
                return null;
            }

            created = await factory.EnsureAsync(
                gcId, TitleFor(ev, number), creator, members,
                new Dictionary<string, string> { ["provider"] = "github", ["issue_node_id"] = node, ["repo_node_id"] = ev.RepoNodeId, ["number"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                cancellationToken).ConfigureAwait(false);
        }

        var (rowGc, applied) = await UpsertAsync(ev, gcId, cancellationToken).ConfigureAwait(false);
        if (rowGc is null)
            return null;

        // The owner joins at create when one resolved then; otherwise (no owner yet) at the first event that finds one. Idempotent.
        if (owner is null && ownerResolver is not null && await ownerResolver.GetOwnerPrincipalIdAsync(cancellationToken).ConfigureAwait(false) is { } later && later != creator)
            await factory.AddOwnerAsync(rowGc.Value, later, cancellationToken).ConfigureAwait(false);

        // Agents named by this event join an existing GC (a mention needs the GC to be readable). A GC created above already holds them.
        if (!created)
            foreach (var login in NamedLogins(ev, trusted))
                if (await directory.ResolveAgentAsync(login, cancellationToken).ConfigureAwait(false) is { } agent)
                    await factory.AddAgentAsync(rowGc.Value, agent, login, cancellationToken).ConfigureAwait(false);

        return new GitHubIssueApplyResult(rowGc.Value, created, applied);
    }

    private async Task<(Guid? GcId, bool Applied)> UpsertAsync(GitHubEvent ev, Guid gcId, CancellationToken ct)
    {
        var isState = ev.EventName == "issues";
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var inserted = false;
        if (ev.IssueNumber is { } number)
        {
            // No conflict target: gc_id is unique too, and a concurrent insert of the same issue can otherwise trip that index instead of the key.
            const string insert = """
                INSERT INTO github_issue (issue_node_id, repo_node_id, number, gc_id, state, title, author_login, updated_at)
                VALUES (@node, @repo, @number, @gc, COALESCE(@state, 'open'), @title, @author, @updated)
                ON CONFLICT DO NOTHING
                """;
            await using var cmd = new NpgsqlCommand(insert, conn, tx);
            Bind(cmd, ev, gcId, number, isState);
            inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }

        Guid rowGc;
        DateTimeOffset lastApplied;
        await using (var select = new NpgsqlCommand("SELECT gc_id, updated_at FROM github_issue WHERE issue_node_id = @node FOR UPDATE", conn, tx))
        {
            select.Parameters.AddWithValue("node", ev.IssueNodeId!);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return (null, false);
            rowGc = reader.GetGuid(0);
            lastApplied = reader.GetFieldValue<DateTimeOffset>(1);
        }

        var applied = inserted && isState;
        if (!inserted && isState)
        {
            applied = GitHubEventOrdering.ShouldApply(ev, lastApplied == DateTimeOffset.UnixEpoch ? null : lastApplied, null);
            if (applied)
            {
                const string update = """
                    UPDATE github_issue SET
                        title = COALESCE(@title, title),
                        state = COALESCE(@state, state),
                        author_login = COALESCE(author_login, @author),
                        updated_at = @updated
                    WHERE issue_node_id = @node
                    """;
                await using var cmd = new NpgsqlCommand(update, conn, tx);
                Bind(cmd, ev, gcId, ev.IssueNumber ?? 1, true);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (rowGc, applied);
    }

    private static void Bind(NpgsqlCommand cmd, GitHubEvent ev, Guid gcId, int number, bool isState)
    {
        cmd.Parameters.AddWithValue("node", ev.IssueNodeId!);
        cmd.Parameters.AddWithValue("repo", ev.RepoNodeId);
        cmd.Parameters.AddWithValue("number", number);
        cmd.Parameters.AddWithValue("gc", gcId);
        cmd.Parameters.AddWithValue("state", (object?)(isState ? StateOf(ev) : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("updated", isState ? ev.UpdatedAt : DateTimeOffset.UnixEpoch);
        cmd.Parameters.AddWithValue("title", (object?)Truncate(NonBlank(ev.IssueTitle), 1024) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("author", (object?)Truncate(NonBlank(ev.IssueAuthorLogin), 256) ?? DBNull.Value);
    }

    /// <summary>The issue state this event sets, or null to keep the stored one. The payload's own state wins over the action.</summary>
    internal static string? StateOf(GitHubEvent ev) =>
        NonBlank(ev.IssueState) is { } s ? s.ToLowerInvariant()
        : ev.Action switch
        {
            "opened" or "reopened" => "open",
            "closed" => "closed",
            _ => null,
        };

    private async Task<(Guid[] Members, int Agents)> MembersAtCreateAsync(GitHubEvent ev, bool trusted, Guid? owner, CancellationToken ct)
    {
        var members = new List<Guid> { creator };
        if (bridge is { } b)
            members.Add(b);
        if (owner is { } o)
            members.Add(o);
        var agents = 0;
        foreach (var login in NamedLogins(ev, trusted))
        {
            if (agents >= options.MaxAgentMembersAtCreate)
            {
                LogCapped(logger, ev.IssueNodeId!);
                break;
            }

            if (await directory.ResolveAgentAsync(login, ct).ConfigureAwait(false) is { } agent && !members.Contains(agent))
            {
                members.Add(agent);
                agents++;
            }
        }

        return ([.. members.Distinct()], agents);
    }

    /// <summary>
    /// The issue author, the assignees and the login just assigned, once each, plus the agents @-mentioned in a member's text.
    /// A stranger's text never adds a member: its mentions are not read here.
    /// </summary>
    private static IEnumerable<string> NamedLogins(GitHubEvent ev, bool trusted)
    {
        IEnumerable<string?> named = new[] { ev.IssueAuthorLogin, ev.AssignedLogin }.Concat(ev.IssueAssigneeLogins);
        if (trusted && !string.IsNullOrEmpty(ev.Body))
            named = named.Concat(MentionRegex().Matches(ev.Body).Select(static m => (string?)m.Groups[1].Value));
        return named.Where(static l => !string.IsNullOrWhiteSpace(l)).Select(static l => l!).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string TitleFor(GitHubEvent ev, int number) =>
        Truncate($"{ev.RepoFullName ?? ev.RepoNodeId}#{number} {ev.IssueTitle}".Trim(), 256)!;

    private async Task<Guid?> ReadAsync(string node, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT gc_id FROM github_issue WHERE issue_node_id = @node");
        cmd.Parameters.AddWithValue("node", node);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as Guid?;
    }

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Truncate(string? text, int max) => text is not null && text.Length > max ? text[..max] : text;

    // An @login not preceded by a login character (so e-mail addresses do not match), with an optional [bot] suffix. Same shape as the mapper's.
    [GeneratedRegex(@"(?<![A-Za-z0-9-])@([A-Za-z0-9][A-Za-z0-9-]{0,38}(?:\[bot\])?)", RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub event {EventId} names an issue that is not registered and carries no issue number; the issue GC is created by a later event.")]
    private static partial void LogNoNumber(ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub event {EventId} is not a member's and names no agent; no issue GC is created.")]
    private static partial void LogNoAgentInvolved(ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Issue {IssueNodeId} names more agents than the create cap; the rest are added by their own events.")]
    private static partial void LogCapped(ILogger logger, string issueNodeId);
}

/// <summary>
/// Decorates the sink: the issue GC and read model are brought up to date first, so the directory finds the GC (<c>github_issue.gc_id</c>).
/// A throw leaves the event unprocessed and it is delivered again, which the service's idempotence allows.
/// </summary>
public sealed class GitHubIssueGcEventSink(IGitHubEventSink inner, GitHubIssueService issueService) : IGitHubEventSink
{
    public async Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await issueService.ApplyAsync(delivery.Event, cancellationToken).ConfigureAwait(false);
        await inner.DeliverAsync(delivery, cancellationToken).ConfigureAwait(false);
    }
}
