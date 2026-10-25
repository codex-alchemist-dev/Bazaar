using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Gc.Model;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gc.Properties;

/// <summary>Which properties a simulation run checks. The operations run the same way whatever is checked.</summary>
[Flags]
public enum GcChecks
{
    None = 0,
    GapFree = 1 << 0,
    Replay = 1 << 1,
    CursorInterleaving = 1 << 2,
    RolledBackAppends = 1 << 3,
    MessageHistory = 1 << 4,
    ViewerFilter = 1 << 5,
    ThinkingGc = 1 << 6,
    Mentions = 1 << 7,
    All = GapFree | Replay | CursorInterleaving | RolledBackAppends | MessageHistory | ViewerFilter | ThinkingGc | Mentions,
}

/// <summary>
/// Seeded random interleaving of GC operations against one GC. The planners under test decide every
/// write; <see cref="ReferenceGcLog"/> and the oracle state below record what was committed. All
/// randomness comes from one <see cref="DeterministicIds"/> stream, so a seed replays exactly.
/// </summary>
internal sealed class GcSimulation
{
    private static readonly string[] Words =
    [
        "ship", "it", "alpha", "beta", "review", "@ana", "later", "🙂",
    ];

    private readonly TestSeed _seed;
    private readonly FakeClock _clock;
    private readonly GcChecks _checks;
    private readonly DeterministicIds _draw;
    private readonly ReferenceGcLog _log = new();
    private readonly Guid _gcId;

    // Oracle state: committed rows only, in append order.
    private readonly List<GcMemberRecord> _rows = [];
    private readonly HashSet<Guid> _approved = [];
    private readonly HashSet<Guid> _pending = [];
    private readonly List<SimMessage> _messages = [];
    private readonly List<GcMentionRow> _mentionRows = [];
    private readonly List<InboxRow> _inboxRows = [];
    private readonly List<SimReader> _readers = [];

    private readonly SimPrincipal _owner;
    private readonly SimPrincipal[] _agents;
    private readonly SimPrincipal[] _humans;
    private readonly SimPrincipal[] _outsiders;
    private readonly SimPrincipal[] _everyone;

    private readonly int _maxBodyBytes;
    private readonly TimeSpan? _editWindow;
    private readonly GcInvitePolicy _invitePolicy;
    private readonly GcUnarchivePolicy _unarchivePolicy;
    private readonly GcDmAccessApprovers _dmApprovers;
    private readonly Guid? _thinkingOwner;

    private GcRecord _gc;
    private bool _faultsEnabled;
    private string _where = "bootstrap";

    public GcSimulation(TestSeed seed, FakeClock clock, GcChecks checks)
    {
        _seed = seed;
        _clock = clock;
        _checks = checks;
        _draw = new DeterministicIds(seed);
        _gcId = _draw.NextGuid();

        _owner = new SimPrincipal(_draw.NextGuid(), PrincipalKind.Owner);
        _agents = MakePool(4, PrincipalKind.Agent);
        _humans = MakePool(3, PrincipalKind.Human);
        _outsiders = [new SimPrincipal(_draw.NextGuid(), PrincipalKind.Human), new SimPrincipal(_draw.NextGuid(), PrincipalKind.Agent)];
        _everyone = [_owner, .. _agents, .. _humans, .. _outsiders];

        _maxBodyBytes = _draw.NextInt(2) == 0 ? 40 : MessageEditPlanner.DefaultMaxBodyBytes;
        _editWindow = _draw.NextInt(2) == 0 ? null : TimeSpan.FromSeconds(90 + _draw.NextInt(120));
        _invitePolicy = _draw.NextInt(2) == 0 ? GcInvitePolicy.AnyMember : GcInvitePolicy.OwnerOnly;
        _unarchivePolicy = _draw.NextInt(2) == 0 ? GcUnarchivePolicy.Owner : GcUnarchivePolicy.AnyMember;
        _dmApprovers = _draw.NextInt(2) == 0 ? GcDmAccessApprovers.Owner : GcDmAccessApprovers.OwnerOrDmMember;

        for (var i = 0; i < 3; i++)
        {
            _readers.Add(new SimReader());
        }

        // 0 and 1: general, 2: dm, 3: thinking (agent-owned, nested under a working GC).
        var typeRoll = _draw.NextInt(4);
        if (typeRoll == 3)
        {
            // Thinking GC: the agent-owned GC nested under a working GC (ThinkingGcRules.Create).
            var working = new GcRecord(_draw.NextGuid(), null, GcTypeKeys.General, null);
            var workingRows = new[] { new GcMemberRecord(working.GcId, _agents[0].Id, PrincipalKind.Agent, GcMemberRole.Member, 1, null) };
            var plan = ThinkingGcRules.Create(working, workingRows, _agents[0].Id, _gcId, agentHasThinkingGc: false);
            _gc = plan.Gc ?? throw new InvalidOperationException("Thinking GC plan refused in the harness setup.");
            _thinkingOwner = _agents[0].Id;
        }
        else
        {
            _gc = new GcRecord(_gcId, null, typeRoll == 2 ? GcTypeKeys.Dm : GcTypeKeys.General, null);
        }
    }

    /// <summary>Runs the bootstrap and <paramref name="opCount"/> random operations, checking the selected properties after each.</summary>
    public void Run(int opCount)
    {
        Bootstrap();
        CheckAfterOperation();

        for (var op = 0; op < opCount; op++)
        {
            _clock.Advance(TimeSpan.FromSeconds(_draw.NextInt(90)));
            var kind = PickOperation();
            _where = $"op {op} ({kind})";
            Perform(kind);
            CheckAfterOperation();
        }

        _where = "final";
        CheckFinal();
        if (On(GcChecks.MessageHistory))
        {
            CheckMessageHistory(final: true);
        }
    }

    /// <summary>Stable text of the committed log, for the determinism test.</summary>
    public string Fingerprint() =>
        string.Join(
            "\n",
            _log.All.Select(e => $"{e.Seq}|{e.Draft.Kind}|{e.Draft.AuthorKind}|{e.Draft.AuthorId:N}|{e.Draft.MessageId}|{e.Draft.Version}|{e.Draft.TargetSeq}|{e.Draft.PayloadJson}"));

    private enum GcOp
    {
        Post,
        Edit,
        AddMember,
        RemoveMember,
        Archive,
        Unarchive,
        Mention,
        Quarantine,
        AccessRequest,
        AccessDecision,
        ReaderStep,
    }

    private SimPrincipal[] MakePool(int count, PrincipalKind kind) =>
        Enumerable.Range(0, count).Select(_ => new SimPrincipal(_draw.NextGuid(), kind)).ToArray();

    private void Bootstrap()
    {
        // Setup is not part of the fault model: no injected faults until the first operation.
        _faultsEnabled = false;

        // The GC's created event is seq 1. A thinking GC's owning agent row also carries JoinedSeq 1.
        var created = new GcEventDraft(_gcId, GcEventKinds.System, AuthorKind.Owner, _owner.Id, SourceTrust.Owner, PayloadJson: "{\"action\":\"created\"}");
        Expect(Commit([created]), "bootstrap created event was rolled back");

        if (_thinkingOwner is { } owningAgent)
        {
            _rows.Add(new GcMemberRecord(_gcId, owningAgent, PrincipalKind.Agent, GcMemberRole.Member, 1, null));
            _faultsEnabled = true;
            return;
        }

        // Owner-added members; dm and general differ in size and an observer.
        AddMember(_owner, _humans[0], GcMemberRole.Member);
        AddMember(_owner, _agents[1], GcMemberRole.Member);
        if (_gc.TypeKey == GcTypeKeys.General)
        {
            AddMember(_owner, _agents[2], GcMemberRole.Observer);
        }

        _faultsEnabled = true;
    }

    private void Perform(GcOp kind)
    {
        switch (kind)
        {
            case GcOp.Post:
                PostMessage(Pick());
                break;
            case GcOp.Edit:
                EditMessage();
                break;
            case GcOp.AddMember:
                AddMember(Pick(), Pick(), _draw.NextInt(3) == 0 ? GcMemberRole.Observer : GcMemberRole.Member);
                break;
            case GcOp.RemoveMember:
                RemoveMember(Pick(), Pick());
                break;
            case GcOp.Archive:
                ArchiveOrUnarchive(archive: true);
                break;
            case GcOp.Unarchive:
                ArchiveOrUnarchive(archive: false);
                break;
            case GcOp.Mention:
                MentionMessage();
                break;
            case GcOp.Quarantine:
                QuarantineMessage();
                break;
            case GcOp.AccessRequest:
                RequestAccess(Pick());
                break;
            case GcOp.AccessDecision:
                DecideAccess(Pick(), Pick(), approve: _draw.NextInt(3) != 0);
                break;
            case GcOp.ReaderStep:
                ReaderStep();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown operation.");
        }
    }

    private GcOp PickOperation() => _draw.NextInt(100) switch
    {
        < 22 => GcOp.Post,
        < 34 => GcOp.Edit,
        < 42 => GcOp.AddMember,
        < 48 => GcOp.RemoveMember,
        < 50 => GcOp.Archive,
        < 53 => GcOp.Unarchive,
        < 63 => GcOp.Mention,
        < 67 => GcOp.Quarantine,
        < 73 => GcOp.AccessRequest,
        < 78 => GcOp.AccessDecision,
        _ => GcOp.ReaderStep,
    };

    // ---- operations -------------------------------------------------------------------------

    private void PostMessage(SimPrincipal author)
    {
        // Posting is gated by the read-write rule itself; a refused post never reaches the log.
        if (!GcAccessPolicy.CanWrite(_gc, _rows, Viewer(author)))
        {
            return;
        }

        var messageId = _draw.NextGuid();
        var seq = _log.Head + 1;
        if (!Commit([MessageDraft(author, messageId, 1)]))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var acted = _draw.NextInt(4) == 0;
        _messages.Add(new SimMessage(messageId, author.Id, seq, now, acted, new MessageVersionRecord(_gcId, messageId, 1, RandomBody(), author.Id, seq, now)));
    }

    private void EditMessage()
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var message = _messages[_draw.NextInt(_messages.Count)];
        var editor = Pick();
        var current = message.Versions[^1];
        var newBody = PickEditBody(current.Body);
        var before = _log.Head;

        var result = MessageEditPlanner.Plan(
            _gc,
            Viewer(editor),
            message.MessageId,
            message.AuthorId,
            current.Version,
            current.Seq,
            current.Body,
            message.CreatedAt,
            newBody,
            _clock.GetUtcNow(),
            _editWindow,
            _maxBodyBytes,
            TrustOf(editor),
            message.ActedOn);

        // Author check runs first in the planner, so a non-author must see NotAuthor whatever else is wrong.
        if (editor.Id != message.AuthorId)
        {
            Expect(result is MessageEditRefused { Reason: MessageEditRefusal.NotAuthor }, "non-author edit was not refused as NotAuthor");
        }

        if (result is not MessageEditAccepted accepted)
        {
            Expect(_log.Head == before, "refused edit appended rows");
            return;
        }

        var plan = accepted.Plan;
        var drafts = new List<GcEventDraft> { plan.EditEvent };
        if (plan.SystemNote is { } note)
        {
            drafts.Add(note);
        }

        Expect(plan.EditEvent.Version == current.Version + 1, "edit event does not carry the next version");
        var editSeq = before + 1;
        if (!Commit(drafts))
        {
            return;
        }

        if (plan.SystemNote is { } committedNote)
        {
            Expect(committedNote.TargetSeq == current.Seq && committedNote.TargetSeq < editSeq, "source-edited note does not point at the prior version");
        }

        message.Versions.Add(plan.NewVersion with { Seq = editSeq });
    }

    private void AddMember(SimPrincipal actor, SimPrincipal target, GcMemberRole role)
    {
        var before = _log.Head;
        var plan = GcMembershipPlanner.PlanAddMember(_gc, _rows, Viewer(actor), target.Id, target.Kind, role, before, _invitePolicy);
        ApplyMembership(plan, before, adding: true);
    }

    private void RemoveMember(SimPrincipal actor, SimPrincipal target)
    {
        var before = _log.Head;
        var plan = GcMembershipPlanner.PlanRemoveMember(_gc, _rows, Viewer(actor), target.Id, before);
        ApplyMembership(plan, before, adding: false);
    }

    private void ApplyMembership(GcPlanResult plan, long before, bool adding)
    {
        if (!plan.Succeeded)
        {
            Expect(_log.Head == before, "refused membership change appended rows");
            return;
        }

        if (!Commit([plan.Event ?? throw new InvalidOperationException("Membership plan without an event.")]))
        {
            return;
        }

        var row = plan.Member ?? throw new InvalidOperationException("Membership plan without a row.");
        var stamped = adding ? row.JoinedSeq : row.LeftSeq;
        Expect(stamped == before + 1, $"membership row stamped {stamped}, expected the event seq {before + 1}");
        _rows.Add(row);
    }

    private void ArchiveOrUnarchive(bool archive)
    {
        var actor = Pick();
        var plan = archive
            ? GcMembershipPlanner.PlanArchive(_gc, Viewer(actor), _clock.GetUtcNow())
            : GcMembershipPlanner.PlanUnarchive(_gc, _rows, Viewer(actor), _unarchivePolicy);
        if (!plan.Succeeded || !Commit([plan.Event!]))
        {
            return;
        }

        _gc = plan.Gc ?? throw new InvalidOperationException("Archive plan without a header.");
    }

    private void MentionMessage()
    {
        var author = Pick();
        if (!GcAccessPolicy.CanWrite(_gc, _rows, Viewer(author)))
        {
            return;
        }

        var body = RandomBody() + (_draw.NextInt(2) == 0 ? " @ana" : string.Empty);
        var spanCount = _draw.NextInt(4);
        var spans = new MentionSpan[spanCount];
        for (var i = 0; i < spanCount; i++)
        {
            var start = _draw.NextInt(body.Length + 4) - 2;
            var length = _draw.NextInt(7) - 1;
            var target = _draw.NextInt(5) == 0 ? _owner : Pick();
            spans[i] = new MentionSpan(start, length, target.Id);
        }

        var active = ActiveIds();
        var validation = MentionValidator.Validate(body, spans, active, _owner.Id, author.Id);

        var seq = _log.Head + 1;
        var messageId = _draw.NextGuid();
        if (!Commit([MessageDraft(author, messageId, 1)]))
        {
            return;
        }

        var mentionRows = InboxPlanner.PlanMentionRows(_gc, seq, validation.Accepted);
        var inboxRows = InboxPlanner.PlanMentionInbox(_gc, seq, validation.Accepted, author.Id);
        _mentionRows.AddRange(mentionRows);
        _inboxRows.AddRange(inboxRows);

        var now = _clock.GetUtcNow();
        _messages.Add(new SimMessage(messageId, author.Id, seq, now, false, new MessageVersionRecord(_gcId, messageId, 1, body, author.Id, seq, now)));

        if (On(GcChecks.Mentions))
        {
            CheckMentionWrite(validation, mentionRows, inboxRows, active, author.Id);
        }
    }

    private void QuarantineMessage()
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var target = _messages[_draw.NextInt(_messages.Count)];
        Commit([new GcEventDraft(_gcId, GcEventKinds.Quarantine, AuthorKind.Owner, _owner.Id, SourceTrust.Owner, TargetSeq: target.FirstSeq)]);
    }

    private void RequestAccess(SimPrincipal requester)
    {
        var plan = GcMembershipPlanner.PlanAccessRequest(_gc, _rows, _approved, Viewer(requester), _log.Head, _pending.Contains(requester.Id));
        if (!plan.Succeeded || !Commit([plan.Event!]))
        {
            return;
        }

        _pending.Add(requester.Id);
    }

    private void DecideAccess(SimPrincipal approver, SimPrincipal requester, bool approve)
    {
        var plan = GcMembershipPlanner.PlanAccessDecision(_gc, _rows, Viewer(approver), requester.Id, approve, _dmApprovers, _pending.Contains(requester.Id));
        if (!plan.Succeeded || !Commit([plan.Event!]))
        {
            return;
        }

        _pending.Remove(requester.Id);
        if (approve)
        {
            _approved.Add(requester.Id);
        }
    }

    private void ReaderStep()
    {
        var reader = _readers[_draw.NextInt(_readers.Count)];
        var limit = 1 + _draw.NextInt(4);
        var chunk = _log.Read(reader.Cursor, limit);

        if (On(GcChecks.CursorInterleaving))
        {
            Expect(chunk.Count <= limit, "reader chunk exceeded its limit");
            for (var i = 0; i < chunk.Count; i++)
            {
                if (chunk[i].Seq != reader.Cursor + i + 1)
                {
                    Expect(false, $"reader chunk not contiguous at seq {chunk[i].Seq}");
                }
            }
        }

        reader.Received.AddRange(chunk);
        if (chunk.Count > 0)
        {
            reader.Cursor = chunk[^1].Seq;
        }
    }

    // ---- writes -----------------------------------------------------------------------------

    /// <summary>
    /// Commits a batch, possibly with an injected fault. On a fault, the head must not move.
    /// Returns true only when the batch is visible.
    /// </summary>
    private bool Commit(List<GcEventDraft> drafts)
    {
        var before = _log.Head;
        int? fault = null;
        if (_faultsEnabled && drafts.Count > 0 && _draw.NextInt(16) == 0)
        {
            fault = _draw.NextInt(drafts.Count);
        }

        var outcome = _log.Commit(drafts, fault);
        if (outcome == CommitOutcome.RolledBack)
        {
            if (On(GcChecks.RolledBackAppends))
            {
                Expect(_log.Head == before, "rolled-back batch moved the head");
            }

            return false;
        }

        Expect(_log.Head == before + drafts.Count, "committed batch did not add exactly its rows");
        return true;
    }

    private GcEventDraft MessageDraft(SimPrincipal author, Guid messageId, int version) =>
        new(_gcId, GcEventKinds.Message, AuthorKindOf(author), author.Id, TrustOf(author), MessageId: messageId, Version: version);

    // ---- oracle and checks ------------------------------------------------------------------

    private void CheckAfterOperation()
    {
        if (On(GcChecks.GapFree))
        {
            CheckGapFree();
        }

        if (On(GcChecks.Replay))
        {
            CheckReplayFrom(_log.Head / 2);
        }

        if (On(GcChecks.MessageHistory))
        {
            CheckMessageHistory(final: false);
        }

        if (On(GcChecks.ViewerFilter))
        {
            CheckViewers();
        }

        if (On(GcChecks.ThinkingGc))
        {
            CheckThinkingExclusion();
        }
    }

    private void CheckFinal()
    {
        CheckGapFree();
        for (var n = 0; n <= _log.Head; n++)
        {
            if (On(GcChecks.Replay))
            {
                CheckReplayFrom(n);
            }
        }

        if (On(GcChecks.Mentions))
        {
            // Every mention and inbox row points at a committed message event of this GC.
            foreach (var seq in _mentionRows.Select(r => r.Seq).Concat(_inboxRows.Select(r => r.Seq)))
            {
                Expect(seq >= 1 && seq <= _log.Head, $"mention or inbox row at seq {seq} has no committed event");
                Expect(_log.All[(int)seq - 1].Draft.Kind == GcEventKinds.Message, $"mention or inbox row at seq {seq} does not point at a message");
            }
        }

        if (On(GcChecks.CursorInterleaving))
        {
            foreach (var reader in _readers)
            {
                var drained = new List<LoggedEvent>(reader.Received);
                var cursor = reader.Cursor;
                while (true)
                {
                    var chunk = _log.Read(cursor, 7);
                    if (chunk.Count == 0)
                    {
                        break;
                    }

                    drained.AddRange(chunk);
                    cursor = chunk[^1].Seq;
                }

                Expect(drained.SequenceEqual(_log.All), $"reader chunks do not concatenate to the full log ({drained.Count} of {_log.Head})");
            }
        }
    }

    private void CheckGapFree()
    {
        var all = _log.All;
        Expect(all.Count == _log.Head, "head does not match the number of rows");
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Seq != i + 1)
            {
                Expect(false, $"seq {all[i].Seq} at position {i + 1}: gap or duplicate");
            }
        }
    }

    private void CheckReplayFrom(long afterSeq)
    {
        var suffix = _log.All.Skip((int)afterSeq).ToList();
        var replayed = _log.Read(afterSeq, int.MaxValue);
        Expect(replayed.SequenceEqual(suffix), $"replay from {afterSeq} differs from the log suffix");
    }

    private void CheckHistoryVisibility(SimMessage message, Dictionary<Guid, bool> readable)
    {
        foreach (var viewer in _everyone)
        {
            var canRead = readable[viewer.Id];
            var result = MessageHistoryReader.GetHistory(message.Versions, Viewer(viewer), v => readable[v.PrincipalId]);
            if ((result is MessageHistoryDenied) == canRead)
            {
                Expect(false, $"history visibility for {viewer.Id} disagrees with the read rule");
            }
        }
    }

    private void CheckMessageHistory(bool final)
    {
        var versionEvents = _log.All
            .Where(e => e.Draft.MessageId is not null
                && (e.Draft.Kind == GcEventKinds.Message || e.Draft.Kind == GcEventKinds.MessageEdit))
            .GroupBy(e => e.Draft.MessageId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => (Version: e.Draft.Version, e.Seq)).ToList());

        // Read access is computed once per viewer per operation, not once per message.
        var readable = _everyone.ToDictionary(
            p => p.Id,
            p => GcAccessPolicy.ReadableUpToSeq(_gc, _rows, Viewer(p), _approved) is not null);

        foreach (var message in _messages)
        {
            var stored = message.Versions;
            Expect(versionEvents.TryGetValue(message.MessageId, out var logged), $"message {message.MessageId} has no events in the log");
            if (!logged!.SequenceEqual(stored.Select(v => (Version: (int?)v.Version, v.Seq))))
            {
                Expect(false, $"versions of {message.MessageId} do not match the log");
            }

            for (var i = 0; i < stored.Count; i++)
            {
                if (stored[i].Version != i + 1)
                {
                    Expect(false, $"message {message.MessageId} versions not contiguous at {stored[i].Version}");
                }

                if (i > 0 && stored[i].Seq <= stored[i - 1].Seq)
                {
                    Expect(false, $"message {message.MessageId} seq does not increase with version");
                }
            }

            var owner = MessageHistoryReader.GetHistory(stored, Viewer(_owner), _ => true);
            if (owner is not MessageHistoryFound found)
            {
                Expect(false, $"owner cannot read history of {message.MessageId}");
                return;
            }

            if (found.History.Edited != (stored.Count > 1))
            {
                Expect(false, $"Edited flag wrong for {stored.Count} versions of {message.MessageId}");
            }

            if (final)
            {
                CheckHistoryVisibility(message, readable);
            }
        }
    }

    private void CheckViewers()
    {
        foreach (var principal in _everyone)
        {
            var viewer = Viewer(principal);
            var readable = GcAccessPolicy.ReadableUpToSeq(_gc, _rows, viewer, _approved);
            var last = LastRow(principal.Id);
            var approved = _approved.Contains(principal.Id);

            if (principal.Kind != PrincipalKind.Owner && !approved)
            {
                if (last is { LeftSeq: { } left } && readable is { } upTo && upTo > left)
                {
                    Expect(false, $"former member {principal.Id} reads up to {upTo}, past LeftSeq {left}");
                }

                if (last is null && readable is not null)
                {
                    Expect(false, $"outsider {principal.Id} reads a GC without approval");
                }
            }

            var canWrite = GcAccessPolicy.CanWrite(_gc, _rows, viewer);
            if (last is { LeftSeq: null, Role: GcMemberRole.Observer } && canWrite)
            {
                Expect(false, $"observer {principal.Id} can write");
            }

            var expectedWrite = ExpectedCanWrite(principal);
            if (canWrite != expectedWrite)
            {
                Expect(false, $"CanWrite for {principal.Id} is {canWrite}, expected {expectedWrite}");
            }
        }
    }

    private void CheckThinkingExclusion()
    {
        foreach (var agent in _everyone.Where(p => p.Kind == PrincipalKind.Agent))
        {
            var excluded = ThinkingGcRules.IsExcludedFromContext(_gc, _rows, agent.Id);
            var expected = _thinkingOwner is { } owner && agent.Id != owner;
            if (_gc.TypeKey != GcTypeKeys.Thinking)
            {
                Expect(!excluded, "a non-thinking GC was excluded from an agent's context");
                continue;
            }

            Expect(excluded == expected, $"thinking GC visibility for agent {agent.Id} is wrong (excluded={excluded})");
        }
    }

    private void CheckMentionWrite(
        MentionValidation validation,
        IReadOnlyList<GcMentionRow> mentionRows,
        IReadOnlyList<InboxRow> inboxRows,
        HashSet<Guid> active,
        Guid authorId)
    {
        foreach (var accepted in validation.Accepted)
        {
            Expect(accepted.TargetPrincipalId == _owner.Id || active.Contains(accepted.TargetPrincipalId), $"accepted mention targets non-member {accepted.TargetPrincipalId}");
        }

        var acceptedTargets = validation.Accepted.Select(a => a.TargetPrincipalId).Distinct().ToHashSet();
        Expect(mentionRows.Select(r => r.TargetPrincipalId).ToHashSet().SetEquals(acceptedTargets), "mention rows differ from the accepted set");
        Expect(mentionRows.Count == acceptedTargets.Count, "duplicate mention rows for one target");

        foreach (var rejected in validation.Rejected.Where(r => r.Reason == MentionRejectReason.NotAMember))
        {
            var target = rejected.Span.PrincipalId;
            Expect(acceptedTargets.Contains(target) || mentionRows.All(r => r.TargetPrincipalId != target), $"rejected mention target {target} was written");
        }

        foreach (var inbox in inboxRows)
        {
            Expect(inbox.PrincipalId != authorId, "author received an inbox row for their own mention");
            Expect(inbox.PrincipalId == _owner.Id || active.Contains(inbox.PrincipalId), $"inbox row for non-member {inbox.PrincipalId}");
        }
    }

    // ---- helpers ----------------------------------------------------------------------------

    private bool On(GcChecks check) => (_checks & check) != 0;

    /// <summary>Fails the run with the seed, the operation and the message. The text is built only on failure.</summary>
    private void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Assert.Fail($"seed={_seed} {_where}: {message}. Replay with {TestSeed.EnvironmentVariable}={_seed}.");
        }
    }

    private SimPrincipal Pick() => _everyone[_draw.NextInt(_everyone.Length)];

    private GcMemberRecord? LastRow(Guid principalId) => _rows.LastOrDefault(r => r.PrincipalId == principalId);

    private HashSet<Guid> ActiveIds() =>
        _everyone
            .Where(p => p.Kind != PrincipalKind.Owner && LastRow(p.Id) is { LeftSeq: null })
            .Select(p => p.Id)
            .ToHashSet();

    private bool ExpectedCanWrite(SimPrincipal principal)
    {
        if (_gc.ArchivedAt is not null)
        {
            return false;
        }

        if (principal.Kind == PrincipalKind.Owner)
        {
            return true;
        }

        if (_thinkingOwner is { } owner && principal.Id != owner && _gc.TypeKey == GcTypeKeys.Thinking)
        {
            return false;
        }

        return LastRow(principal.Id) is { LeftSeq: null, Role: GcMemberRole.Member };
    }

    private string RandomBody()
    {
        var count = 1 + _draw.NextInt(8);
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = Words[_draw.NextInt(Words.Length)];
        }

        return string.Join(' ', parts);
    }

    private string PickEditBody(string current) => _draw.NextInt(10) switch
    {
        0 => current,
        1 => "   ",
        2 => new string('x', 64),
        _ => RandomBody(),
    };

    private static GcViewer Viewer(SimPrincipal principal) => new(principal.Id, principal.Kind);

    private static AuthorKind AuthorKindOf(SimPrincipal principal) => principal.Kind switch
    {
        PrincipalKind.Owner => AuthorKind.Owner,
        PrincipalKind.Agent => AuthorKind.Agent,
        _ => AuthorKind.Human,
    };

    private static SourceTrust TrustOf(SimPrincipal principal) => principal.Kind switch
    {
        PrincipalKind.Owner => SourceTrust.Owner,
        PrincipalKind.Agent => SourceTrust.Agent,
        _ => SourceTrust.MemberHuman,
    };

    /// <summary>A principal in the simulated pool. Ids come from the seeded stream.</summary>
    private sealed record SimPrincipal(Guid Id, PrincipalKind Kind);

    /// <summary>Oracle record of one message: its versions as the planners produced them.</summary>
    private sealed class SimMessage(Guid messageId, Guid authorId, long firstSeq, DateTimeOffset createdAt, bool actedOn, MessageVersionRecord first)
    {
        public Guid MessageId { get; } = messageId;

        public Guid AuthorId { get; } = authorId;

        public long FirstSeq { get; } = firstSeq;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public bool ActedOn { get; } = actedOn;

        public List<MessageVersionRecord> Versions { get; } = [first];
    }

    /// <summary>A reader that consumes the log in chunks from its own cursor.</summary>
    private sealed class SimReader
    {
        public long Cursor { get; set; }

        public List<LoggedEvent> Received { get; } = [];
    }
}
