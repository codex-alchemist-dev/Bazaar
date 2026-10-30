using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gc.Properties;

/// <summary>
/// Seeded properties for message versions (docs/design/01-gc-data-model.md, Core model and Edits; docs/design/34-testing-strategy.md,
/// WP 10). Every edit adds one version and one <c>message_edit</c> event; no stored version is ever removed or rewritten, and
/// the history reader returns every version exactly as it was written. Each property runs every seed from <see cref="GcPropertySeeds"/>.
/// </summary>
public sealed class GcVersionPropertyTests
{
    private static Guid OwnerId { get; } = Guid.Parse("00000000-0000-0000-0000-00000000000f");

    [Fact]
    public void Edits_add_a_version_and_never_remove_or_rewrite_one()
    {
        foreach (var seed in GcPropertySeeds.All())
        {
            SeededRun.Execute(seed, Run);
        }
    }

    private static void Run(TestSeed seed)
    {
        var clock = new FakeClock();
        var draw = new DeterministicIds(seed);
        var gc = new GcRecord(draw.NextGuid(), null, GcTypeKeys.General, null);
        var members = Enumerable.Range(0, 4)
            .Select(i => new GcMemberRecord(gc.GcId, draw.NextGuid(), i % 2 == 0 ? PrincipalKind.Human : PrincipalKind.Agent, GcMemberRole.Member, 1, null))
            .ToList();
        var log = new ReferenceGcLog();
        var messages = new List<SimMessage>();

        Expect(log.Commit([new GcEventDraft(gc.GcId, GcEventKinds.System, AuthorKind.Owner, OwnerId, SourceTrust.Owner, PayloadJson: "{\"action\":\"created\"}")]) == CommitOutcome.Committed, seed, "bootstrap", "created event was not committed");

        for (var op = 0; op < GcPropertySeeds.OpsPerSequence; op++)
        {
            var where = $"op {op}";
            clock.Advance(TimeSpan.FromSeconds(draw.NextInt(60)));
            var headBefore = log.Head;
            var before = messages.ToDictionary(m => m.MessageId, m => m.Versions.ToList());

            if (messages.Count == 0 || draw.NextInt(3) == 0)
            {
                Post(draw, gc, members, log, messages, clock.GetUtcNow(), seed, where);
            }
            else
            {
                Edit(draw, gc, members, log, messages[draw.NextInt(messages.Count)], clock, seed, where);
            }

            // Every version that existed before the operation is still there, unchanged, in the same position.
            foreach (var message in messages)
            {
                if (!before.TryGetValue(message.MessageId, out var prior))
                {
                    continue;
                }

                Expect(message.Versions.Count >= prior.Count, seed, where, $"message {message.MessageId} lost a version");
                for (var i = 0; i < prior.Count; i++)
                {
                    Expect(message.Versions[i] == prior[i], seed, where, $"version {i + 1} of message {message.MessageId} changed");
                }
            }

            Expect(log.Head >= headBefore, seed, where, "the log head went backwards");
            CheckAgainstLog(gc, log, messages, seed, where, draw, members);
        }

        CheckAgainstLog(gc, log, messages, seed, "final", draw, members);
    }

    private static void Post(
        DeterministicIds draw, GcRecord gc, List<GcMemberRecord> members, ReferenceGcLog log, List<SimMessage> messages,
        DateTimeOffset now, TestSeed seed, string where)
    {
        var author = members[draw.NextInt(members.Count)];
        var messageId = draw.NextGuid();
        var body = RandomBody(draw);
        var seq = log.Head + 1;
        var draft = new GcEventDraft(gc.GcId, GcEventKinds.Message, KindOf(author), author.PrincipalId, TrustOf(author), MessageId: messageId, Version: 1);
        Expect(log.Commit([draft]) == CommitOutcome.Committed, seed, where, "post was not committed");

        var message = new SimMessage(messageId, author.PrincipalId, now);
        message.Versions.Add(new MessageVersionRecord(gc.GcId, messageId, 1, body, author.PrincipalId, seq, now));
        messages.Add(message);
    }

    private static void Edit(
        DeterministicIds draw, GcRecord gc, List<GcMemberRecord> members, ReferenceGcLog log, SimMessage message,
        FakeClock clock, TestSeed seed, string where)
    {
        // Mostly the author; sometimes another member or the owner, who must be refused.
        var editor = members[draw.NextInt(members.Count)];
        if (draw.NextInt(2) == 0)
        {
            editor = members.Single(m => m.PrincipalId == message.AuthorId);
        }

        var viewer = draw.NextInt(5) == 0
            ? new GcViewer(OwnerId, PrincipalKind.Owner)
            : new GcViewer(editor.PrincipalId, editor.Kind);

        var current = message.Versions[^1];
        var newBody = draw.NextInt(8) == 0 ? current.Body : RandomBody(draw);
        var headBefore = log.Head;

        var result = MessageEditPlanner.Plan(
            gc,
            viewer,
            message.MessageId,
            message.AuthorId,
            current.Version,
            current.Seq,
            current.Body,
            message.CreatedAt,
            newBody,
            clock.GetUtcNow(),
            editWindow: null,
            MessageEditPlanner.DefaultMaxBodyBytes,
            SourceTrust.MemberHuman,
            messageWasActedOn: false);

        if (result is not MessageEditAccepted accepted)
        {
            Expect(log.Head == headBefore, seed, where, "a refused edit appended rows");
            return;
        }

        Expect(viewer.PrincipalId == message.AuthorId, seed, where, "an edit by a non-author was accepted");
        var plan = accepted.Plan;
        Expect(plan.EditEvent.Version == current.Version + 1, seed, where, "the edit event does not carry the next version");
        Expect(plan.NewVersion.Version == current.Version + 1, seed, where, "the new version is not the next number");

        var drafts = new List<GcEventDraft> { plan.EditEvent };
        if (plan.SystemNote is { } note)
        {
            drafts.Add(note);
        }

        var editSeq = headBefore + 1;
        Expect(log.Commit(drafts) == CommitOutcome.Committed, seed, where, "edit was not committed");
        Expect(log.Head == headBefore + drafts.Count, seed, where, "edit did not add exactly its rows");

        // Add, never replace: the new version is appended after the existing ones.
        message.Versions.Add(plan.NewVersion with { Seq = editSeq });
    }

    /// <summary>Checks the stored versions against the log, the history reader and the Edited flag, for every message.</summary>
    private static void CheckAgainstLog(
        GcRecord gc, ReferenceGcLog log, List<SimMessage> messages, TestSeed seed, string where, DeterministicIds draw, List<GcMemberRecord> members)
    {
        var owner = new GcViewer(OwnerId, PrincipalKind.Owner);
        var member = members[draw.NextInt(members.Count)];
        var memberViewer = new GcViewer(member.PrincipalId, member.Kind);

        foreach (var message in messages)
        {
            var stored = message.Versions;
            for (var i = 0; i < stored.Count; i++)
            {
                Expect(stored[i].Version == i + 1, seed, where, $"message {message.MessageId} versions are not 1..n at {i + 1}");
                Expect(stored[i].MessageId == message.MessageId && stored[i].GcId == gc.GcId, seed, where, "a version belongs to another message or GC");
                Expect(i == 0 || stored[i].Seq > stored[i - 1].Seq, seed, where, $"message {message.MessageId} seq does not increase with version");
            }

            // The log has one event per version: the post (version 1) and one message_edit per later version.
            var logged = log.All
                .Where(e => e.Draft.MessageId == message.MessageId && (e.Draft.Kind == GcEventKinds.Message || e.Draft.Kind == GcEventKinds.MessageEdit))
                .Select(e => (e.Draft.Version, e.Seq))
                .ToList();
            Expect(logged.SequenceEqual(stored.Select(v => ((int?)v.Version, v.Seq))), seed, where, $"versions of message {message.MessageId} do not match the log");
            Expect(logged.Count(e => e.Version > 1) == stored.Count - 1, seed, where, $"message {message.MessageId} lost an edit event");

            // The history reader returns every version, exactly as stored, to the owner and to a member.
            foreach (var viewer in new[] { owner, memberViewer })
            {
                var result = MessageHistoryReader.GetHistory(stored, viewer, _ => true);
                Expect(result is MessageHistoryFound, seed, where, $"history of message {message.MessageId} was not returned to {viewer.PrincipalId}");
                var history = ((MessageHistoryFound)result).History;
                Expect(history.Versions.SequenceEqual(stored), seed, where, $"history of message {message.MessageId} does not round-trip its versions");
                Expect(history.Edited == (stored.Count > 1), seed, where, $"Edited flag wrong for {stored.Count} versions");
            }

            var denied = MessageHistoryReader.GetHistory(stored, memberViewer, _ => false);
            Expect(denied is MessageHistoryDenied, seed, where, "history was returned to a viewer who cannot read the GC");
        }
    }

    private static string RandomBody(DeterministicIds draw)
    {
        string[] words = ["ship", "it", "alpha", "beta", "review", "@ana", "later", "🙂"];
        var count = 1 + draw.NextInt(8);
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = words[draw.NextInt(words.Length)];
        }

        return string.Join(' ', parts);
    }

    private static AuthorKind KindOf(GcMemberRecord member) => member.Kind == PrincipalKind.Agent ? AuthorKind.Agent : AuthorKind.Human;

    private static SourceTrust TrustOf(GcMemberRecord member) => member.Kind == PrincipalKind.Agent ? SourceTrust.Agent : SourceTrust.MemberHuman;

    private static void Expect(bool condition, TestSeed seed, string where, string message)
    {
        if (!condition)
        {
            Assert.Fail($"seed={seed} {where}: {message}. Replay with {TestSeed.EnvironmentVariable}={seed}.");
        }
    }

    /// <summary>Oracle record of one message: its versions as the planners and the writer produced them.</summary>
    private sealed class SimMessage(Guid messageId, Guid authorId, DateTimeOffset createdAt)
    {
        public Guid MessageId { get; } = messageId;

        public Guid AuthorId { get; } = authorId;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public List<MessageVersionRecord> Versions { get; } = [];
    }
}
