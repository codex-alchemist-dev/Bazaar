using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

/// <summary>
/// Seeded property tests (200 iterations each). The same set of writes, merged in a shuffled arrival
/// order, must give the same result where the data class requires it (doc 56 section 7).
/// </summary>
public sealed class MergeOrderPropertyTests
{
    private const int _seed = 20261118;
    private const int _iterations = 200;

    [Fact]
    public async Task Kanban_fields_on_distinct_task_fields_end_in_the_same_state_for_any_arrival_order()
    {
        var random = new Random(_seed);
        for (var iteration = 0; iteration < _iterations; iteration++)
        {
            var writes = new List<OfflineWrite>();
            for (var task = 0; task < 4; task++)
            {
                for (var field = 0; field < 3; field++)
                {
                    if (random.Next(3) > 0)
                    {
                        writes.Add(KanbanWrite(random, $"task-{task}", $"f{field}", $"v{iteration}-{task}-{field}"));
                    }
                }
            }

            var expected = await KanbanSnapshotAfter(writes, writes);
            for (var permutation = 0; permutation < 3; permutation++)
            {
                var shuffled = new List<OfflineWrite>(writes);
                RuleTestKit.Shuffle(shuffled, random);
                var actual = await KanbanSnapshotAfter(shuffled, writes);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public async Task Kanban_contested_field_has_exactly_one_winner_for_any_arrival_order()
    {
        var random = new Random(_seed + 1);
        for (var iteration = 0; iteration < _iterations; iteration++)
        {
            var contenders = Enumerable.Range(0, 2 + random.Next(4))
                .Select(i => KanbanWrite(random, "task-x", "title", $"v{iteration}-{i}", baseVersion: "0"))
                .ToList();

            for (var permutation = 0; permutation < 3; permutation++)
            {
                var shuffled = new List<OfflineWrite>(contenders);
                RuleTestKit.Shuffle(shuffled, random);
                var state = new InMemoryKanbanFieldState();
                var rule = new KanbanFieldRule(state, new FakeTransitionChecker(), new RecordingReviewSink());

                var outcomes = new List<OfflineWriteOutcome>();
                foreach (var write in shuffled)
                {
                    outcomes.Add(await rule.MergeAsync(write, CancellationToken.None));
                }

                var applied = outcomes.Where(o => o.Kind == OfflineOutcomeKind.Applied).ToList();
                Assert.Single(applied);
                Assert.Equal(outcomes.Count - 1, outcomes.Count(o => o.Kind == OfflineOutcomeKind.Conflict));

                var winner = contenders.Single(w => w.WriteId == applied[0].WriteId);
                var stored = await state.GetFieldAsync("task-x", "title", CancellationToken.None);
                Assert.Equal(RulePayloadCodec.ReadKanbanField(winner.Payload)?.Value, stored.Value);
            }
        }
    }

    [Fact]
    public async Task Last_writer_wins_keeps_the_highest_hlc_for_any_arrival_order()
    {
        var random = new Random(_seed + 2);
        for (var iteration = 0; iteration < _iterations; iteration++)
        {
            var writes = BuildLwwWrites(random);
            var expectedCurrent = writes
                .GroupBy(w => w.Target, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.MaxBy(w => w.Hlc)!.WriteId, StringComparer.Ordinal);

            for (var permutation = 0; permutation < 3; permutation++)
            {
                var shuffled = new List<OfflineWrite>(writes);
                RuleTestKit.Shuffle(shuffled, random);
                var state = new InMemoryLastWriterWinsState();
                var rule = new ConstellationPositionRule(state);
                foreach (var write in shuffled)
                {
                    await rule.MergeAsync(write, CancellationToken.None);
                }

                foreach (var (key, winnerId) in expectedCurrent)
                {
                    var current = await state.GetCurrentAsync(key, CancellationToken.None);
                    Assert.Equal(winnerId, current?.WriteId);
                    var history = state.GetHistory(key).Select(e => e.WriteId).ToHashSet();
                    var others = writes.Where(w => w.Target == key && w.WriteId != winnerId).Select(w => w.WriteId).ToHashSet();
                    Assert.True(others.SetEquals(history));
                }
            }
        }
    }

    [Fact]
    public async Task Attachments_store_the_same_blobs_for_any_arrival_order_and_every_write_is_applied()
    {
        var random = new Random(_seed + 3);
        for (var iteration = 0; iteration < _iterations; iteration++)
        {
            var pool = Enumerable.Range(0, 5).Select(_ => RandomHash(random)).ToList();
            var writes = Enumerable.Range(0, 20)
                .Select(_ => RuleTestKit.Write(OfflineDataClass.Attachment, pool[random.Next(pool.Count)], new byte[] { 1 }))
                .ToList();

            for (var permutation = 0; permutation < 3; permutation++)
            {
                var shuffled = new List<OfflineWrite>(writes);
                RuleTestKit.Shuffle(shuffled, random);
                var state = new InMemoryAttachmentState();
                var rule = new AttachmentRule(state);
                foreach (var write in shuffled)
                {
                    var outcome = await rule.MergeAsync(write, CancellationToken.None);
                    Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
                }

                Assert.Equal(writes.Select(w => w.Target).Distinct().Count(), state.Count);
            }
        }
    }

    [Fact]
    public async Task Acyclic_edge_adds_union_to_the_same_edge_set_for_any_arrival_order()
    {
        var random = new Random(_seed + 4);
        for (var iteration = 0; iteration < _iterations; iteration++)
        {
            // A random rank per node; edges only point from lower to higher rank, so the union is acyclic.
            var ranks = Enumerable.Range(0, 8).OrderBy(_ => random.Next()).ToArray();
            var chosen = new List<KanbanEdge>();
            for (var from = 0; from < 8; from++)
            {
                for (var to = 0; to < 8; to++)
                {
                    if (ranks[from] < ranks[to] && random.Next(5) < 2)
                    {
                        chosen.Add(new KanbanEdge($"n{from}", $"n{to}"));
                    }
                }
            }

            var writes = chosen
                .Select(e => RuleTestKit.Write(OfflineDataClass.KanbanEdge, "graph", RuleTestKit.Json(new KanbanEdgePayload(e.From, e.To, false))))
                .ToList();
            var expected = chosen.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();

            for (var permutation = 0; permutation < 3; permutation++)
            {
                var shuffled = new List<OfflineWrite>(writes);
                RuleTestKit.Shuffle(shuffled, random);
                var state = new InMemoryKanbanEdgeState();
                var rule = new KanbanEdgeRule(state, new RecordingReviewSink());
                foreach (var write in shuffled)
                {
                    var outcome = await rule.MergeAsync(write, CancellationToken.None);
                    Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
                }

                var actual = (await state.GetEdgesAsync("graph", CancellationToken.None))
                    .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public async Task When_adds_would_form_a_cycle_arrival_order_decides_which_edge_is_refused()
    {
        // Documented limit (see KanbanEdgeRule): the first arrival wins, so the refused edge depends on order.
        var forward = await TwoEdgeCycleOutcomes(first: ("A", "B"), second: ("B", "A"));
        var reverse = await TwoEdgeCycleOutcomes(first: ("B", "A"), second: ("A", "B"));

        Assert.Equal([OfflineOutcomeKind.Applied, OfflineOutcomeKind.Rejected], forward);
        Assert.Equal([OfflineOutcomeKind.Applied, OfflineOutcomeKind.Rejected], reverse);
    }

    private static async Task<List<OfflineOutcomeKind>> TwoEdgeCycleOutcomes((string From, string To) first, (string From, string To) second)
    {
        var rule = new KanbanEdgeRule(new InMemoryKanbanEdgeState(), new RecordingReviewSink());
        var kinds = new List<OfflineOutcomeKind>();
        foreach (var (from, to) in new[] { first, second })
        {
            var write = RuleTestKit.Write(OfflineDataClass.KanbanEdge, "graph", RuleTestKit.Json(new KanbanEdgePayload(from, to, false)));
            kinds.Add((await rule.MergeAsync(write, CancellationToken.None)).Kind);
        }

        return kinds;
    }

    private static OfflineWrite KanbanWrite(Random random, string task, string field, string value, string baseVersion = "0")
    {
        var device = random.Next(2) == 0 ? RuleTestKit.DeviceA : RuleTestKit.DeviceB;
        return RuleTestKit.Write(
            OfflineDataClass.KanbanField,
            task,
            RuleTestKit.Json(new KanbanFieldPayload(field, value)),
            baseVersion,
            RuleTestKit.NextGuid(random),
            RuleTestKit.At(1000, device: device));
    }

    /// <summary>Applies the arrival order, then reads every (task, field) of the full write set.</summary>
    private static async Task<List<string?>> KanbanSnapshotAfter(IEnumerable<OfflineWrite> arrival, IReadOnlyList<OfflineWrite> universe)
    {
        var state = new InMemoryKanbanFieldState();
        var rule = new KanbanFieldRule(state, new FakeTransitionChecker(), new RecordingReviewSink());
        foreach (var write in arrival)
        {
            var outcome = await rule.MergeAsync(write, CancellationToken.None);
            Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        }

        var snapshot = new List<string?>();
        foreach (var task in universe.Select(w => w.Target).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            foreach (var field in new[] { "f0", "f1", "f2" })
            {
                snapshot.Add((await state.GetFieldAsync(task, field, CancellationToken.None)).Value);
            }
        }

        return snapshot;
    }

    private static List<OfflineWrite> BuildLwwWrites(Random random)
    {
        var walls = Enumerable.Range(1, 1000).OrderBy(_ => random.Next()).Take(12).ToArray();
        var writes = new List<OfflineWrite>();
        for (var i = 0; i < walls.Length; i++)
        {
            var key = $"node-{random.Next(3)}";
            var device = random.Next(2) == 0 ? RuleTestKit.DeviceA : RuleTestKit.DeviceB;
            writes.Add(RuleTestKit.Write(
                OfflineDataClass.ConstellationPosition,
                key,
                RuleTestKit.Json(new ConstellationPositionPayload(i, -i)),
                writeId: RuleTestKit.NextGuid(random),
                hlc: RuleTestKit.At(walls[i], device: device)));
        }

        return writes;
    }

    private static string RandomHash(Random random)
    {
        const string Hex = "0123456789abcdef";
        return new string(Enumerable.Range(0, 64).Select(_ => Hex[random.Next(Hex.Length)]).ToArray());
    }
}
