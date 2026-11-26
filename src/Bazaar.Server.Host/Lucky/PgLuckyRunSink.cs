using Bazaar.Contracts.Gc;
using Bazaar.Lucky;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Host.Lucky;

/// <summary>
/// The Postgres-backed <see cref="ILuckyRunSink"/> (doc 26, section 6). It is host composition, not a module: it uses the GC,
/// inbox and Kanban services directly, and the Lucky module stays free of server references.
/// </summary>
/// <remarks>
/// Every step is keyed by the run, so a repeat call writes nothing new:
/// the GC id is <see cref="GcCreateService.DeterministicGcId"/> of the dedupe key; each message has a fixed
/// <c>ClientMsgId</c> per author, which the log answers with the original event; inbox rows are unique per
/// (agent, GC, seq, kind); and a card is skipped when a task already has that origin message.
/// </remarks>
public sealed class PgLuckyRunSink(
    NpgsqlDataSource dataSource,
    GcCreateService gcCreate,
    IGcLogWriter gcLog,
    IGcBodyCodec bodies,
    InboxRepository inbox,
    TaskService tasks) : ILuckyRunSink
{
    private const string _messageSeqSql = "SELECT message_id FROM gc_message WHERE gc_id = $1 AND first_seq = $2";
    private const string _cardExistsSql = "SELECT id FROM tasks WHERE origin_message_id = $1 LIMIT 1";

    /// <inheritdoc />
    public async Task<LuckyGcOpened> OpenGcAsync(LuckyRunPlan plan, Guid ownerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var gcId = GcCreateService.DeterministicGcId(plan.DedupeKey);
        var members = new List<Guid> { ownerId };
        foreach (var wake in plan.Wakes)
        {
            var agentId = ParseAgent(wake.AgentId);
            if (!members.Contains(agentId))
                members.Add(agentId);
        }

        var title = plan.Prompt.Length <= GcCreateService.MaxTitleLength
            ? plan.Prompt
            : plan.Prompt[..GcCreateService.MaxTitleLength];

        await gcCreate.EnsureAsync(new GcCreateRequest(gcId, title, ownerId, members, plan.GcTypeKey), ct).ConfigureAwait(false);

        var openingSeq = await AppendMessageAsync(
            gcId, ownerId, AuthorKind.Owner, SourceTrust.Owner, $"{plan.DedupeKey}:open", plan.Prompt, ct).ConfigureAwait(false);
        return new LuckyGcOpened(gcId, openingSeq);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LuckyStoredEntry>> AppendEntriesAsync(Guid gcId, IReadOnlyList<LuckyGcEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var stored = new List<LuckyStoredEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var authorId = ParseAgent(entry.AgentId);
            var seq = await AppendMessageAsync(
                gcId, authorId, AuthorKind.Agent, SourceTrust.Agent, $"lucky-idea:{gcId:N}:{entry.IdeaIndex}", entry.Text, ct)
                .ConfigureAwait(false);
            stored.Add(new LuckyStoredEntry(entry.IdeaIndex, await MessageIdAsync(gcId, seq, ct).ConfigureAwait(false)));
        }

        return stored;
    }

    /// <inheritdoc />
    public async Task<int> EnqueueWakesAsync(Guid gcId, long openingSeq, IReadOnlyList<LuckyWake> wakes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wakes);
        var priority = (short)InboxPriority.Rank(InboxKind.Brainstorm);
        var inserted = 0;
        foreach (var wake in wakes)
        {
            var agentId = ParseAgent(wake.AgentId);
            if (await inbox.EnqueueAsync(new InboxEnqueue(agentId, wake.Kind, priority, gcId, openingSeq, wake.DedupeKey), ct)
                    .ConfigureAwait(false))
            {
                inserted++;
            }
        }

        return inserted;
    }

    /// <inheritdoc />
    public async Task<Guid> CreateCardAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct)
    {
        await using (var exists = dataSource.CreateCommand(_cardExistsSql))
        {
            exists.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
            if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid existing)
                return existing;
        }

        var result = await tasks.CreateFromMessageAsync(projectId, gcId, messageId, title, TaskActor.SystemActor, ct).ConfigureAwait(false);
        return result.TaskId ?? throw new InvalidOperationException($"The card for message {messageId:D} was refused: {result.Status}.");
    }

    private async Task<long> AppendMessageAsync(
        Guid gcId, Guid authorId, AuthorKind authorKind, SourceTrust trust, string clientMsgId, string text, CancellationToken ct)
    {
        var body = await bodies.SealAsync(authorId, text, ct).ConfigureAwait(false);
        var request = new AppendRequest(gcId, GcEventKinds.Message, authorKind, authorId, trust, clientMsgId) { Message = body };
        var result = await gcLog.AppendAsync(request, ct).ConfigureAwait(false);
        return result.Seq;
    }

    private async Task<Guid> MessageIdAsync(Guid gcId, long seq, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_messageSeqSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        return (Guid)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"No message at seq {seq}."));
    }

    private static Guid ParseAgent(string agentId) =>
        Guid.TryParse(agentId, out var id) ? id : throw new ArgumentException($"Agent id '{agentId}' is not a GUID.", nameof(agentId));
}
