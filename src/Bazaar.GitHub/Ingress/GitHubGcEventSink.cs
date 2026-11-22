using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.GitHub.Events;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.GitHub.Ingress;

/// <summary>Resolves what a mapped effect points at: a GC for a target, an agent for a GitHub login (doc 21, Data and interfaces).</summary>
public interface IGitHubGcDirectory
{
    /// <summary>The GC for a target, or null when none exists yet (the PR service creates PR GCs, doc 21 WP6).</summary>
    Task<Guid?> ResolveGcAsync(GcTarget target, CancellationToken cancellationToken);

    /// <summary>The agent whose GitHub App login is <paramref name="login"/>, or null when it is not an agent.</summary>
    Task<Guid?> ResolveAgentAsync(string login, CancellationToken cancellationToken);
}

/// <summary>Writes one inbox item (doc 06). The default implementation wraps <see cref="InboxRepository"/>; it is a port so the sink is testable.</summary>
public interface IGitHubInboxWriter
{
    /// <summary>Returns false when the item already existed (a replay).</summary>
    Task<bool> EnqueueAsync(InboxEnqueue item, CancellationToken cancellationToken);
}

/// <summary><see cref="IGitHubInboxWriter"/> over the agent runtime's <see cref="InboxRepository"/>.</summary>
public sealed class InboxRepositoryGitHubInboxWriter(InboxRepository repository) : IGitHubInboxWriter
{
    public Task<bool> EnqueueAsync(InboxEnqueue item, CancellationToken cancellationToken) => repository.EnqueueAsync(item, cancellationToken);
}

/// <summary>
/// Postgres directory. A PR GC comes from <c>github_pr.gc_id</c> (this module's own table); the Inbox GC is the configured
/// <see cref="GitHubIngressOptions.InboxGcId"/>; an issue GC comes from <c>github_issue.gc_id</c>. An agent comes from
/// <c>github_app.account_login</c>.
/// </summary>
public sealed class PgGitHubGcDirectory(NpgsqlDataSource dataSource, GitHubIngressOptions options) : IGitHubGcDirectory
{
    public async Task<Guid?> ResolveGcAsync(GcTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (target.Kind)
        {
            case GcTargetKind.Inbox:
                return options.InboxGcId;
            case GcTargetKind.PullRequest when target.NodeId is not null:
                await using (var cmd = dataSource.CreateCommand("SELECT gc_id FROM github_pr WHERE pr_node_id = @id"))
                {
                    cmd.Parameters.AddWithValue("id", target.NodeId);
                    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
                }

            case GcTargetKind.Issue when target.NodeId is not null:
                await using (var cmd = dataSource.CreateCommand("SELECT gc_id FROM github_issue WHERE issue_node_id = @id"))
                {
                    cmd.Parameters.AddWithValue("id", target.NodeId);
                    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
                }

            default:
                return null;
        }
    }

    public async Task<Guid?> ResolveAgentAsync(string login, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        await using var cmd = dataSource.CreateCommand(
            "SELECT agent_id FROM github_app WHERE lower(account_login) = lower(@login) AND status = 'active' LIMIT 1");
        cmd.Parameters.AddWithValue("login", login);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }
}

/// <summary>
/// The real <see cref="IGitHubEventSink"/> (doc 21, Event mapping). For each mapped effect it appends one GC event, then enqueues
/// the inbox rows the effect says wake. Rules: an effect that is not member-authored never wakes (hard taint, written with
/// <see cref="SourceTrust.External"/>); the event's own author is never woken by it (loop guard); a replay does nothing new,
/// because the GC append is idempotent on <c>(gc, author, client_msg_id)</c> and the inbox on <c>(agent, gc, seq, kind)</c> or
/// <c>(agent, kind, source_ref)</c>. A throw leaves the event unprocessed and it is delivered again.
/// </summary>
public sealed partial class GitHubGcEventSink(
    IGcLogWriter writer,
    IGitHubInboxWriter inbox,
    IGitHubGcDirectory directory,
    GitHubIngressOptions options,
    ILogger<GitHubGcEventSink> logger) : IGitHubEventSink
{
    private const int _maxKeyLength = 200;

    public async Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!delivery.Apply || delivery.Effects.Count == 0)
            return;
        var bridge = options.EffectiveBridgePrincipalId;

        for (var i = 0; i < delivery.Effects.Count; i++)
        {
            var effect = delivery.Effects[i];
            var key = KeyFor(delivery.DedupeKey, i);
            Guid? gcId = null;
            long? seq = null;

            if (effect.Entry is { } entry && await directory.ResolveGcAsync(entry.Target, cancellationToken).ConfigureAwait(false) is { } gc)
            {
                var result = await writer.AppendAsync(BuildAppend(gc, bridge, key, delivery, effect, entry), cancellationToken).ConfigureAwait(false);
                gcId = result.GcId;
                seq = result.Seq;
            }
            else if (effect.Entry is not null)
            {
                LogNoGc(logger, delivery.DedupeKey, effect.Entry.Target.Kind);
            }

            if (!effect.Wakes)
                continue;
            foreach (var item in effect.InboxItems.Where(static item => item.Wakes))
                await EnqueueAsync(delivery, key, gcId, seq, item, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnqueueAsync(GitHubEventDelivery delivery, string key, Guid? gcId, long? seq, InboxItemDraft item, CancellationToken ct)
    {
        // Owner notices and rows without a login are not agent inbox rows (inbox_item.agent_id references agent).
        if (item.RecipientRole == InboxRecipientRole.Owner || string.IsNullOrWhiteSpace(item.RecipientLogin))
            return;
        // Loop guard: an event never wakes the member that wrote it.
        if (string.Equals(item.RecipientLogin, delivery.Event.AuthorLogin, StringComparison.OrdinalIgnoreCase))
            return;
        if (await directory.ResolveAgentAsync(item.RecipientLogin, ct).ConfigureAwait(false) is not { } agentId)
        {
            LogUnknownAgent(logger, delivery.DedupeKey, item.RecipientLogin);
            return;
        }

        var kind = KindFor(item.Kind);
        var priority = (short)InboxPriority.Rank(kind);
        var row = gcId is { } g && seq is { } s
            ? new InboxEnqueue(agentId, kind, priority, g, s)
            : new InboxEnqueue(agentId, kind, priority, SourceRef: key);
        await inbox.EnqueueAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Doc 21 names <c>assignment</c> and <c>mention</c>; review feedback and check failure are "author inbox item" with no kind
    /// chosen. They map to <c>assignment</c> (work the author must act on) until doc 06 names a kind (OPEN).
    /// </summary>
    internal static string KindFor(Events.InboxKind kind) => kind switch
    {
        Events.InboxKind.Mention => Server.Agents.Inbox.InboxKind.Mention,
        _ => Server.Agents.Inbox.InboxKind.Assignment,
    };

    private static AppendRequest BuildAppend(Guid gc, Guid bridge, string key, GitHubEventDelivery delivery, MappedGitHubEffect effect, GcEntryDraft entry)
    {
        var external = effect.HardTaint;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["provider"] = "github",
            ["entry_kind"] = entry.Kind,
            ["summary"] = entry.Summary,
            ["excerpt"] = external ? null : entry.BodyExcerpt,
            ["event"] = delivery.Event.EventName,
            ["action"] = delivery.Event.Action,
            ["author_login"] = delivery.Event.AuthorLogin,
            ["node_id"] = entry.Target.NodeId,
            ["dedupe_key"] = delivery.DedupeKey,
        });
        return new AppendRequest(
            gc,
            entry.Kind == "pr_event" ? GcEventKinds.PrEvent : GcEventKinds.Structured,
            external ? AuthorKind.ExternalBridge : AuthorKind.System,
            bridge,
            external ? SourceTrust.External : SourceTrust.System,
            key)
        {
            PayloadJson = payload,
            TaintedAtWrite = external ? true : null,
        };
    }

    private static string KeyFor(string dedupeKey, int index)
    {
        var key = $"{dedupeKey}#{index}";
        return key.Length <= _maxKeyLength ? key : $"github:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub event {DedupeKey}: no GC exists yet for the {TargetKind} target; the entry is not posted.")]
    private static partial void LogNoGc(ILogger logger, string dedupeKey, GcTargetKind targetKind);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub event {DedupeKey}: login {Login} is not a registered agent; no inbox row.")]
    private static partial void LogUnknownAgent(ILogger logger, string dedupeKey, string login);
}
