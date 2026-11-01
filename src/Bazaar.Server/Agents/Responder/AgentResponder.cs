using System.Collections.Concurrent;
using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Agents.Responder;

/// <summary>
/// Decides and writes replies (docs/design/06, the mention and DM wake). For each new message in a GC it asks: is the author a
/// human, is an agent a member, and does the message mention that agent or is the GC a two-member DM with the agent? If so, the
/// agent reads the recent history, calls the model through the gateway, and posts the answer as itself.
/// </summary>
/// <remarks>
/// Rules: agent, system and bridge messages never trigger a reply, so agents cannot loop. A reply's idempotency key is derived
/// from the trigger's message id, and a trigger the agent already answered is skipped before the model is called, so a restart
/// does not pay for or post the same reply twice. A failed model call posts nothing and logs a warning; the trigger is not retried.
/// Work for one GC runs one pass at a time (a per-GC gate), so at most one reply per GC is in flight.
/// </remarks>
public sealed partial class AgentResponder(
    IResponderGcStore store,
    IGcLogWriter log,
    IModelGateway gateway,
    IGcBodyCodec bodies,
    IOptions<AgentResponderOptions> options,
    ILogger<AgentResponder> logger)
{
    private readonly AgentResponderOptions _options = options.Value;
    private readonly Dictionary<Guid, AgentResponderPersona> _personas = options.Value.Agents.ToDictionary(p => p.AgentId);
    private readonly IReadOnlyDictionary<Guid, string> _names = options.Value.Agents.ToDictionary(p => p.AgentId, p => p.Name);
    private readonly ConcurrentDictionary<Guid, long> _cursors = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ILogger<AgentResponder> _logger = logger;

    /// <summary>
    /// Records the first seq the process heard of for a GC, so its first pass starts there and does not answer older history.
    /// Only the first call for a GC has an effect. The service calls it with the seq of the first notification, before any
    /// coalescing, so a later pass with an unbounded seq never sets the starting point.
    /// </summary>
    public void Observe(Guid gcId, long seq) => _cursors.TryAdd(gcId, Math.Max(0, seq - 1));

    /// <summary>
    /// Handles every new message in <paramref name="gcId"/> up to <paramref name="throughSeq"/> that has not been handled yet.
    /// The first pass for a GC starts at its newest seq, so history from before this process started is not answered.
    /// </summary>
    public async Task ProcessAsync(Guid gcId, long throughSeq, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(gcId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cursor = _cursors.GetOrAdd(gcId, static (_, through) => Math.Max(0, through - 1), throughSeq);
            if (cursor >= throughSeq)
                return;

            var snapshot = await store.GetSnapshotAsync(gcId, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                LogUnknownGc(gcId);
                return;
            }

            if (snapshot.Archived)
                return;

            while (true)
            {
                var batch = await store.ReadNewMessagesAsync(gcId, cursor, throughSeq, _options.ReadBatchSize, ct).ConfigureAwait(false);
                foreach (var message in batch)
                {
                    await HandleMessageAsync(gcId, snapshot, message, ct).ConfigureAwait(false);
                    cursor = message.Seq;
                    _cursors[gcId] = cursor;
                }

                if (batch.Count < _options.ReadBatchSize)
                    break;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task HandleMessageAsync(Guid gcId, ResponderGcSnapshot snapshot, ResponderMessage message, CancellationToken ct)
    {
        // Only people start a reply. Agent, system and bridge messages never do, so two agents cannot answer each other.
        if (message.AuthorKind is not (AuthorKind.Owner or AuthorKind.Human))
            return;

        foreach (var persona in _personas.Values)
        {
            if (!snapshot.ActiveMembers.Contains(persona.AgentId) || persona.AgentId == message.AuthorId)
                continue;

            var mentioned = message.Mentions.Contains(persona.AgentId);
            var directMessage = snapshot.ActiveMembers.Count == 2;
            if (!mentioned && !directMessage)
                continue;

            try
            {
                await ReplyAsync(gcId, persona, message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogReplyFailed(gcId, persona.AgentId, ex);
            }
        }
    }

    private async Task ReplyAsync(Guid gcId, AgentResponderPersona persona, ResponderMessage trigger, CancellationToken ct)
    {
        var key = ReplyKey(trigger.MessageId);
        if (await store.HasEventAsync(gcId, persona.AgentId, key, ct).ConfigureAwait(false))
            return;

        var history = await store.ReadHistoryAsync(gcId, trigger.Seq, _options.HistoryLimit, ct).ConfigureAwait(false);
        var chat = ResponderPrompt.Build(persona, history, _names);

        var text = await CompleteAsync(gcId, persona, chat, ct).ConfigureAwait(false);
        if (text is null)
            return;

        var request = new AppendRequest(gcId, GcEventKinds.Message, AuthorKind.Agent, persona.AgentId, SourceTrust.Agent, key)
        {
            ReplyToSeq = trigger.Seq,
            Message = bodies.Seal(text),
        };

        try
        {
            var result = await log.AppendAsync(request, ct).ConfigureAwait(false);
            if (result.Duplicate)
                LogDuplicateReply(gcId, persona.AgentId, result.Seq);
        }
        catch (GcArchivedException)
        {
            LogArchivedDuringReply(gcId);
        }
    }

    private async Task<string?> CompleteAsync(Guid gcId, AgentResponderPersona persona, IReadOnlyList<ChatMessage> chat, CancellationToken ct)
    {
        try
        {
            var client = new GatewayChatClient(gateway, persona.AgentId.ToString("D"), persona.Route ?? _options.DefaultRoute);
            var response = await client.GetResponseAsync(chat, null, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                LogEmptyReply(gcId, persona.AgentId);
                return null;
            }

            return response.Text.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GatewayModelException ex)
        {
            LogModelFailed(gcId, persona.AgentId, ex.Code, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            LogModelFailed(gcId, persona.AgentId, "unexpected", ex.Message);
            return null;
        }
    }

    /// <summary>The idempotency key of the reply to one trigger. Unique per agent within the GC.</summary>
    public static string ReplyKey(Guid triggerMessageId) => $"responder:{triggerMessageId:N}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent responder: model call for agent {AgentId} in GC {GcId} failed ({Code}): {Detail}. No reply was posted.")]
    private partial void LogModelFailed(Guid gcId, Guid agentId, string code, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent responder: agent {AgentId} in GC {GcId} produced an empty reply. No reply was posted.")]
    private partial void LogEmptyReply(Guid gcId, Guid agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent responder: reply by agent {AgentId} in GC {GcId} failed; the trigger is skipped.")]
    private partial void LogReplyFailed(Guid gcId, Guid agentId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent responder: reply by agent {AgentId} in GC {GcId} was already appended at seq {Seq}.")]
    private partial void LogDuplicateReply(Guid gcId, Guid agentId, long seq);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent responder: GC {GcId} was archived before the reply was written.")]
    private partial void LogArchivedDuringReply(Guid gcId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent responder: GC {GcId} does not exist.")]
    private partial void LogUnknownGc(Guid gcId);
}
