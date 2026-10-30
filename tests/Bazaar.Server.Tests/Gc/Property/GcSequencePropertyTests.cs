using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gc.Properties;

/// <summary>
/// Seeded properties for per-GC sequence numbers across several GCs, and for idempotent appends
/// (docs/design/01-gc-data-model.md, Core model and Append; docs/design/34-testing-strategy.md, WP 10).
/// Each property runs every seed from <see cref="GcPropertySeeds"/>. A failure names the seed and the operation.
/// </summary>
public sealed class GcSequencePropertyTests
{
    [Fact]
    public void Per_gc_seqs_are_strictly_increasing_and_gap_free_across_interleaved_appends()
    {
        RunAll(InterleaveSeveralGcs);
    }

    [Fact]
    public void A_replayed_append_with_the_same_idempotency_key_does_not_advance_the_sequence()
    {
        RunAll(ReplayIdempotencyKeys);
    }

    private static void RunAll(Action<TestSeed> body)
    {
        foreach (var seed in GcPropertySeeds.All())
        {
            SeededRun.Execute(seed, body);
        }
    }

    /// <summary>Random batches of 1 to 3 messages to 2 to 5 GCs, some rolled back. Checked after every operation.</summary>
    private static void InterleaveSeveralGcs(TestSeed seed)
    {
        var draw = new DeterministicIds(seed);
        var gcIds = new Guid[2 + draw.NextInt(4)];
        for (var i = 0; i < gcIds.Length; i++)
        {
            gcIds[i] = draw.NextGuid();
        }

        var authors = Enumerable.Range(0, 3).Select(_ => draw.NextGuid()).ToArray();
        var logs = gcIds.ToDictionary(id => id, _ => new ReferenceGcLog());

        for (var op = 0; op < GcPropertySeeds.OpsPerSequence; op++)
        {
            var where = $"op {op}";
            var gc = gcIds[draw.NextInt(gcIds.Length)];
            var log = logs[gc];
            var author = authors[draw.NextInt(authors.Length)];

            var size = 1 + draw.NextInt(3);
            var batch = new List<GcEventDraft>(size);
            for (var i = 0; i < size; i++)
            {
                batch.Add(new GcEventDraft(gc, GcEventKinds.Message, AuthorKind.Human, author, SourceTrust.MemberHuman, MessageId: draw.NextGuid(), Version: 1));
            }

            int? fault = draw.NextInt(8) == 0 ? draw.NextInt(size) : null;
            var headBefore = log.Head;
            var otherHeads = gcIds.Where(id => id != gc).ToDictionary(id => id, id => logs[id].Head);

            var outcome = log.Commit(batch, fault);
            if (outcome == CommitOutcome.RolledBack)
            {
                Expect(log.Head == headBefore, seed, where, "a rolled-back batch moved this GC's head");
            }
            else
            {
                Expect(log.Head == headBefore + size, seed, where, "a committed batch did not add exactly its rows");
                for (var i = 0; i < size; i++)
                {
                    var stored = log.All[(int)headBefore + i];
                    Expect(stored.Seq == headBefore + i + 1, seed, where, $"committed row got seq {stored.Seq}, expected {headBefore + i + 1}");
                    Expect(stored.Draft == batch[i], seed, where, "the stored row is not the draft that was committed");
                }
            }

            // Appends to one GC never move another GC's head.
            foreach (var (id, head) in otherHeads)
            {
                Expect(logs[id].Head == head, seed, where, $"an append to one GC moved the head of another (gc {id})");
            }

            foreach (var (id, l) in logs)
            {
                CheckContiguous(l, seed, where, id);
            }
        }

        var gcsWithEvents = logs.Values.Count(l => l.Head > 0);
        Expect(gcsWithEvents >= 2, seed, "final", $"only {gcsWithEvents} GC received events; the run did not interleave");

        foreach (var (id, l) in logs)
        {
            // Reading in random chunk sizes from cursor 0 yields exactly the log, in strictly increasing seq.
            var cursor = 0L;
            var drained = new List<LoggedEvent>();
            while (true)
            {
                var chunk = l.Read(cursor, 1 + draw.NextInt(4));
                if (chunk.Count == 0)
                {
                    break;
                }

                drained.AddRange(chunk);
                cursor = chunk[^1].Seq;
            }

            Expect(drained.SequenceEqual(l.All), seed, "final", $"gc {id}: chunked reads do not equal the log");
        }
    }

    /// <summary>
    /// Replays and fresh appends under idempotency keys. A replay returns the original event and writes nothing; a
    /// key is recorded only when its first attempt commits, so a retry after a rollback writes once.
    /// </summary>
    private static void ReplayIdempotencyKeys(TestSeed seed)
    {
        var draw = new DeterministicIds(seed);
        var gcIds = new Guid[2 + draw.NextInt(3)];
        for (var i = 0; i < gcIds.Length; i++)
        {
            gcIds[i] = draw.NextGuid();
        }

        var authors = Enumerable.Range(0, 3).Select(_ => draw.NextGuid()).ToArray();
        var logs = gcIds.ToDictionary(id => id, _ => new KeyedGcLog());
        var issued = new List<PendingAppend>();
        var rolledBack = new List<PendingAppend>();
        var committedWrites = 0L;

        for (var op = 0; op < GcPropertySeeds.OpsPerSequence; op++)
        {
            var where = $"op {op}";
            PendingAppend attempt;
            var mode = draw.NextInt(10);
            if (mode < 3 && issued.Count > 0)
            {
                // Replay of an earlier, committed key. The payload is changed on some replays to show the original is kept.
                var original = issued[draw.NextInt(issued.Count)];
                var draft = draw.NextInt(2) == 0 ? original.Draft : original.Draft with { PayloadJson = "{\"changed\":true}" };
                attempt = new PendingAppend(original.GcId, original.Draft.AuthorId, original.ClientMsgId, draft);
                var headBefore = logs[attempt.GcId].Log.Head;
                var result = logs[attempt.GcId].Append(attempt.Draft, attempt.ClientMsgId, fault: null);
                Expect(result.Duplicate, seed, where, "a replay of a committed key was not reported as a duplicate");
                Expect(result.Stored == original.Stored, seed, where, "a replay did not return the original event");
                Expect(logs[attempt.GcId].Log.Head == headBefore, seed, where, "a replay advanced the sequence");
            }
            else if (mode < 5 && rolledBack.Count > 0)
            {
                // Retry of a key whose first attempt rolled back: it is not recorded, so it writes exactly once.
                var index = draw.NextInt(rolledBack.Count);
                attempt = rolledBack[index];
                rolledBack.RemoveAt(index);
                var headBefore = logs[attempt.GcId].Log.Head;
                var result = logs[attempt.GcId].Append(attempt.Draft, attempt.ClientMsgId, fault: null);
                Expect(!result.Duplicate, seed, where, "a retry after a rollback was reported as a duplicate");
                Expect(result.Stored is not null && result.Stored.Seq == headBefore + 1, seed, where, "a retry after a rollback did not write at the next seq");
                committedWrites++;
                issued.Add(attempt with { Stored = result.Stored });
            }
            else
            {
                var gc = gcIds[draw.NextInt(gcIds.Length)];
                var author = authors[draw.NextInt(authors.Length)];
                var key = draw.NextId("key");
                var draft = new GcEventDraft(gc, GcEventKinds.Message, AuthorKind.Human, author, SourceTrust.MemberHuman, MessageId: draw.NextGuid(), Version: 1);
                attempt = new PendingAppend(gc, author, key, draft);
                var headBefore = logs[gc].Log.Head;
                var fault = draw.NextInt(6) == 0 ? 0 : (int?)null;
                var result = logs[gc].Append(draft, key, fault);
                if (result.Stored is null)
                {
                    Expect(logs[gc].Log.Head == headBefore, seed, where, "a rolled-back append moved the head");
                    rolledBack.Add(attempt);
                }
                else
                {
                    Expect(!result.Duplicate, seed, where, "a fresh key was reported as a duplicate");
                    Expect(result.Stored.Seq == headBefore + 1, seed, where, "a fresh append did not take the next seq");
                    committedWrites++;
                    issued.Add(attempt with { Stored = result.Stored });
                }
            }

            // Every committed write is one row; replays and rollbacks add none.
            var totalRows = logs.Values.Sum(l => l.Log.Head);
            Expect(totalRows == committedWrites, seed, where, $"{totalRows} rows for {committedWrites} committed writes");
            foreach (var (id, l) in logs)
            {
                CheckContiguous(l.Log, seed, where, id);
            }
        }

        // Each recorded key maps to exactly one row, and that row is the draft that first committed.
        foreach (var issuedAppend in issued)
        {
            var log = logs[issuedAppend.GcId].Log;
            var rows = log.All.Where(e => e.Seq == issuedAppend.Stored!.Seq).ToList();
            Expect(rows.Count == 1 && rows[0].Draft == issuedAppend.Draft, seed, "final", "a recorded key does not map to its one committed row");
        }
    }

    private static void CheckContiguous(ReferenceGcLog log, TestSeed seed, string where, Guid gcId)
    {
        var all = log.All;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Seq != i + 1)
            {
                Expect(false, seed, where, $"gc {gcId}: seq {all[i].Seq} at position {i + 1}: gap, duplicate or out of order");
            }
        }
    }

    private static void Expect(bool condition, TestSeed seed, string where, string message)
    {
        if (!condition)
        {
            Assert.Fail($"seed={seed} {where}: {message}. Replay with {TestSeed.EnvironmentVariable}={seed}.");
        }
    }

    /// <summary>An append as the caller sent it, with the stored row once it has committed.</summary>
    private sealed record PendingAppend(Guid GcId, Guid AuthorId, string ClientMsgId, GcEventDraft Draft)
    {
        public LoggedEvent? Stored { get; init; }
    }

    /// <summary>What one append attempt returned: the stored event (null when rolled back) and whether it was a replay.</summary>
    private sealed record AppendOutcome(LoggedEvent? Stored, bool Duplicate);

    /// <summary>
    /// Test model of the idempotent append contract over <see cref="ReferenceGcLog"/>. The key is recorded only after
    /// its append commits. This is the contract the property checks; the SQL writer (GcLogWriter) is covered by the
    /// database tests, which need PostgreSQL and do not run in this property folder.
    /// </summary>
    private sealed class KeyedGcLog
    {
        private readonly Dictionary<(Guid AuthorId, string Key), LoggedEvent> _keys = [];

        public ReferenceGcLog Log { get; } = new();

        public AppendOutcome Append(GcEventDraft draft, string clientMsgId, int? fault)
        {
            if (_keys.TryGetValue((draft.AuthorId, clientMsgId), out var original))
            {
                return new AppendOutcome(original, Duplicate: true);
            }

            if (Log.Commit([draft], fault) == CommitOutcome.RolledBack)
            {
                return new AppendOutcome(null, Duplicate: false);
            }

            var stored = Log.All[^1];
            _keys[(draft.AuthorId, clientMsgId)] = stored;
            return new AppendOutcome(stored, Duplicate: false);
        }
    }
}
