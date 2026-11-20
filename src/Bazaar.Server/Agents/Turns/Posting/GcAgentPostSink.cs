using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.LoopGuard.Abstractions;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Gc.Model;
using Bazaar.Server.Gc.Wire;
using Bazaar.Server.Gc.Write;
using Bazaar.Souls.Abstractions;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>
/// Posts an agent's final text into a GC through the GC log writer (docs/design/06 "Turn loop" step 5). The message lands in the
/// log like any other message: author kind agent, source trust agent (soft taint, DECISIONS-LOG round 2), body sealed by
/// <see cref="IGcBodyCodec"/>.
/// </summary>
/// <remarks>
/// The client message id is <c>turn:{turnId}:{sha256(text)}</c>. The writer is idempotent by (GC, author, client message id), so a
/// retry of the same turn with the same text returns the original event and writes nothing. Keying on the text as well as the
/// turn means a second, different post in the same turn is not silently swallowed as a duplicate.
/// </remarks>
/// <remarks>
/// What applies to an agent post, in order:
/// <list type="number">
/// <item>Loop guard (<see cref="ILoopGuard"/>): the post is recorded as an agent message with mention depth 0. On a trip nothing is
/// appended and <see cref="AgentPostRefusedException"/> is thrown.</item>
/// <item>Mentions, when an <see cref="IGcMentionDirectory"/> is registered: "@handle" tokens are parsed against the GC's active
/// members (<see cref="AgentMentionParser"/>), validated (<see cref="MentionValidator"/>, author = the agent), and the accepted
/// targets go into <see cref="AppendRequest.Mentions"/>. Without a directory the post carries no mentions.</item>
/// <item>Inbox rows for the accepted mentions (<see cref="InboxPlanner"/>, <see cref="IInboxRowWriter"/>) after the append. A
/// self-mention gets no inbox row.</item>
/// <item>Soul stamp: the agent's Soul version is resolved once through <see cref="ISoulBindings"/> and written with the event. An
/// agent with no resolvable Soul is refused (<see cref="AgentPostUnboundException"/>) unless
/// <see cref="GcAgentPostOptions.RequireSoulBinding"/> is off.</item>
/// <item>On a loop-guard trip, <see cref="ILoopTripHandler"/> (when registered) writes <c>guard_event</c>, a system notice in the GC
/// and the Psych Ward admission before the refusal is thrown.</item>
/// </list>
/// One narrow race is accepted: the Soul is resolved before the append, so a retry of a post whose commit outcome was lost, made after
/// the agent was retired, is refused as unbound although the original message exists. The turn is then marked refused and its
/// items are released; the original post stays in the log. A retired agent stops posting at its next wake, not mid-turn.
/// Not applied here: nested mention depth (<c>GcMessage</c> has no parent field, so the depth is always 0 and the
/// <c>runtime.guard.mention_depth</c> guard never sees a chain; it needs the wake's depth carried on the inbox row, which doc 06 owns);
/// @here and @all (the parser reports them and they are ignored, DECISIONS-LOG #438, until the broadcast rate counter exists).
/// <c>AddGcAgentPostSink</c> registers <see cref="SqlGcMentionDirectory"/>, which reads handles from
/// <c>principal_handle</c>; a member without a handle cannot be mentioned.
/// </remarks>
public sealed class GcAgentPostSink : IAgentPostSink
{
    /// <summary>Prefix of the client message id that makes an agent post idempotent per turn.</summary>
    public const string ClientMsgIdPrefix = "turn:";

    private readonly IGcLogWriter _writer;
    private readonly IGcBodyCodec _bodyCodec;
    private readonly ILoopGuard _loopGuard;
    private readonly ISoulBindings _souls;
    private readonly IGcMentionDirectory? _directory;
    private readonly IInboxRowWriter? _inbox;
    private readonly ILoopTripHandler? _trips;
    private readonly GcAgentPostOptions _options;

    /// <summary>Creates the sink.</summary>
    /// <param name="writer">The GC log writer.</param>
    /// <param name="bodyCodec">Seals the message body.</param>
    /// <param name="loopGuard">Checked before every post. Required: a sink without a guard cannot be built.</param>
    /// <param name="souls">Resolves the Soul version each post is stamped with. Required: a sink cannot be built without one.</param>
    /// <param name="directory">Finds mention targets. Null posts with no mentions.</param>
    /// <param name="inbox">Writes inbox rows for accepted mentions. Required with <paramref name="directory"/>.</param>
    /// <param name="trips">Records a loop-guard trip (guard_event, system notice, Psych Ward). Null records nothing.</param>
    /// <param name="options">Post settings. Null uses the defaults (Soul binding required).</param>
    /// <exception cref="ArgumentException">A directory is given without an inbox writer.</exception>
    public GcAgentPostSink(
        IGcLogWriter writer,
        IGcBodyCodec bodyCodec,
        ILoopGuard loopGuard,
        ISoulBindings souls,
        IGcMentionDirectory? directory = null,
        IInboxRowWriter? inbox = null,
        ILoopTripHandler? trips = null,
        GcAgentPostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(bodyCodec);
        ArgumentNullException.ThrowIfNull(loopGuard);
        ArgumentNullException.ThrowIfNull(souls);
        if (directory is not null && inbox is null)
            throw new ArgumentException("Mention inbox rows need an inbox row writer.", nameof(inbox));

        _writer = writer;
        _bodyCodec = bodyCodec;
        _loopGuard = loopGuard;
        _souls = souls;
        _directory = directory;
        _inbox = inbox;
        _trips = trips;
        _options = options ?? new GcAgentPostOptions();
    }

    /// <inheritdoc />
    /// <exception cref="AgentPostRefusedException">A loop guard tripped. Nothing was appended.</exception>
    public async Task PostAsync(Guid agentId, Guid turnId, string gcId, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gcId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > GcSendHandler.MaxTextLength)
            throw new ArgumentException($"Agent post text is longer than {GcSendHandler.MaxTextLength} characters.", nameof(text));
        if (!Guid.TryParse(gcId, out var gcGuid))
            throw new ArgumentException("The GC id must be a GUID.", nameof(gcId));

        // The Soul version the turn runs under, resolved once for this post and before the guard, so a refused unbound post is not counted as a message. An agent with no Soul has no identity to stamp.
        var soul = await _souls.ResolveAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (soul is null && _options.RequireSoulBinding)
            throw new AgentPostUnboundException(agentId);

        // Mention depth is 0: GcMessage has no parent, so the nested chain depth is not tracked yet.
        var verdict = _loopGuard.Check(new AgentMessage(gcGuid, agentId.ToString("D"), 0));
        if (verdict.Tripped)
        {
            var kind = verdict.Kind ?? throw new InvalidOperationException("A tripped verdict has no kind.");
            await RecordTripAsync(gcGuid, kind, verdict, cancellationToken).ConfigureAwait(false);
            throw new AgentPostRefusedException(kind, verdict.Reason);
        }

        var mentions = await ResolveMentionsAsync(agentId, gcGuid, text, cancellationToken).ConfigureAwait(false);

        // Seal before the append, under the agent's own key (docs/design/04 Question 2).
        var body = await _bodyCodec.SealAsync(agentId, text, cancellationToken).ConfigureAwait(false);
        var request = new AppendRequest(
            gcGuid,
            GcEventKinds.Message,
            AuthorKind.Agent,
            agentId,
            SourceTrust.Agent,
            ClientMsgId(turnId, text))
        {
            Message = body,
            Mentions = mentions.Targets,
            Soul = soul,
        };
        var result = await _writer.AppendAsync(request, cancellationToken).ConfigureAwait(false);

        if (mentions.Accepted is { } accepted && _inbox is not null)
        {
            // GcRecord carries the GC's type and archive state for other planners; the mention planner reads only the id.
            var gc = new GcRecord(gcGuid, null, string.Empty, null);
            var rows = InboxPlanner.PlanMentionInbox(gc, result.Seq, accepted, agentId);
            await _inbox.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
        }
    }

    // A trip is reported before the refusal. The handler runs each of its steps independently and logs a failed one; whatever it
    // does or fails to do, the post stays refused.
    private async Task RecordTripAsync(Guid gcId, GuardKind kind, GuardVerdict verdict, CancellationToken cancellationToken)
    {
        if (_trips is null)
            return;

        var agents = verdict.AgentIds.Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null).OfType<Guid>().Distinct().Order().ToList();
        await _trips.HandleAsync(new LoopTrip(gcId, GuardKindName(kind), verdict.Reason, agents), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The lowercase snake_case name a guard is logged under in <c>guard_event.kind</c>.</summary>
    public static string GuardKindName(GuardKind kind) => kind switch
    {
        GuardKind.PingPong => "ping_pong",
        GuardKind.MentionDepth => "mention_depth",
        GuardKind.WakesPerHour => "wakes_per_hour",
        GuardKind.ThreadAgentMessages => "thread_agent_messages",
        GuardKind.RepeatToolCall => "repeat_tool_call",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown guard kind."),
    };

    /// <summary>The idempotency key for one turn's post of <paramref name="text"/>.</summary>
    public static string ClientMsgId(Guid turnId, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return $"{ClientMsgIdPrefix}{turnId:D}:{hash}";
    }

    /// <summary>Parses and validates the mentions of one post. With no directory, returns no targets and no accepted set.</summary>
    private async Task<(IReadOnlyList<Guid> Targets, IReadOnlyList<AcceptedMention>? Accepted)> ResolveMentionsAsync(
        Guid agentId,
        Guid gcId,
        string text,
        CancellationToken cancellationToken)
    {
        if (_directory is null)
            return ([], null);

        var roster = await _directory.LoadAsync(gcId, cancellationToken).ConfigureAwait(false);
        var scan = AgentMentionParser.Scan(text, roster.ActiveMembers);
        var active = roster.ActiveMembers.Select(m => m.PrincipalId).ToHashSet();
        var validation = MentionValidator.Validate(text, scan.Spans, active, roster.OwnerId, agentId);
        var targets = validation.Accepted.Select(a => a.TargetPrincipalId).Distinct().ToList();
        return (targets, validation.Accepted);
    }
}
