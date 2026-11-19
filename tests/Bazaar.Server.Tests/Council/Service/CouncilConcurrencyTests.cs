using System.Collections.Concurrent;
using Bazaar.Server.Council;
using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Tests.Council.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Council.Service;

/// <summary>
/// Parallel callers against the real store (doc 10): one ballot per voter and phase, one reveal, one decision, whatever
/// the interleaving of ballots, deadline processing and the all-cast trigger.
/// </summary>
public sealed class CouncilConcurrencyTests(CouncilDb db) : IClassFixture<CouncilDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    /// <summary>The test cipher keeps plain dictionaries, so parallel callers get a locked wrapper.</summary>
    private sealed class LockedCipher(ICouncilReasoningCipher inner) : ICouncilReasoningCipher
    {
        private readonly object _gate = new();

        public ReasoningCiphertext Encrypt(string voterId, string plaintext)
        {
            lock (_gate)
                return inner.Encrypt(voterId, plaintext);
        }

        public string Decrypt(string voterId, ReasoningCiphertext sealedText)
        {
            lock (_gate)
                return inner.Decrypt(voterId, sealedText);
        }
    }

    private sealed class ConcurrentEvents : ICouncilGcEvents
    {
        private readonly ConcurrentDictionary<string, string> _byKey = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _kinds = new();

        public int Count(string kind) => _kinds.Count(k => k == kind);

        public Task AppendAsync(Guid gcId, string kind, string payloadJson, string idempotencyKey, CancellationToken ct)
        {
            if (_byKey.TryAdd(idempotencyKey, kind))
                _kinds.Enqueue(kind);
            return Task.CompletedTask;
        }
    }

    private sealed class Members(IReadOnlyList<GcMember> list) : IGcMembership
    {
        public Task<IReadOnlyList<GcMember>> ListMembersAsync(Guid gcId, CancellationToken ct) => Task.FromResult(list);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(CouncilService Service, CouncilStore Store, ConcurrentEvents Events, Guid VoteId, string[] Voters, Clock Clock)> OpenAsync(int voters)
    {
        var gcId = await db.CreateGcAsync();
        var names = Enumerable.Range(0, voters).Select(i => $"voter-{i:D2}").ToArray();
        var members = new List<GcMember> { new("proposer", GcMemberKind.Agent) };
        members.AddRange(names.Select(n => new GcMember(n, GcMemberKind.Agent)));
        var store = new CouncilStore(db.DataSource, new LockedCipher(new CouncilTestCipher()));
        var events = new ConcurrentEvents();
        var clock = new Clock();
        var service = new CouncilService(store, events, new Members(members), new CouncilOptions(), clock);
        var draft = new MotionDraft("task/9", VoteKind.ApproveReject, MotionClass.Ordinary, [], gcId, "proposer", null, clock.Now.AddHours(10));
        var id = await service.OpenAsync("proposer", draft, Ct);
        return (service, store, events, id, names, clock);
    }

    [Fact]
    public async Task Each_voter_double_casting_in_parallel_records_one_ballot_and_one_reveal()
    {
        SkipWithoutDatabase();
        var (service, store, events, id, voters, _) = await OpenAsync(12);

        // Every voter submits twice at the same instant; the all-cast trigger fires from whichever ballot lands last.
        var attempts = voters.SelectMany(v => new[] { v, v })
            .Select(v => Task.Run(async () =>
            {
                try
                {
                    await service.SubmitBallotAsync(id, v, Ballots.Independent(v, "yes"), Ct);
                    return true;
                }
                catch (CouncilRejectedException)
                {
                    return false;
                }
            }, Ct)).ToArray();
        var accepted = await Task.WhenAll(attempts);

        Assert.Equal(voters.Length, accepted.Count(a => a));
        var ballots = await store.ListBallotsAsync(id, Ct);
        Assert.Equal(voters.Length, ballots.Count);
        Assert.Equal(1, events.Count("council.reveal"));
        Assert.Equal(1, events.Count("council.decision"));
        Assert.Equal(VoteState.Decided, (await store.FindVoteAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task Deadline_processing_racing_late_ballots_never_reveals_or_decides_twice()
    {
        SkipWithoutDatabase();
        var (service, store, events, id, voters, clock) = await OpenAsync(9);

        // Eight of nine have cast; the ninth ballot (twice) and six deadline workers then race.
        foreach (var v in voters.Take(8))
            await service.SubmitBallotAsync(id, v, Ballots.Independent(v, "yes"), Ct);
        clock.Now = clock.Now.AddHours(11);

        var tasks = new List<Task>();
        for (var i = 0; i < 6; i++)
            tasks.Add(Task.Run(() => service.ProcessDeadlineAsync(id, Ct), Ct));
        for (var i = 0; i < 2; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await service.SubmitBallotAsync(id, voters[8], Ballots.Independent(voters[8], "no"), Ct);
                }
                catch (CouncilRejectedException)
                {
                }
            }, Ct));
        }

        await Task.WhenAll(tasks);
        await service.ProcessDeadlineAsync(id, Ct);

        Assert.Equal(1, events.Count("council.reveal"));
        Assert.True(events.Count("council.decision") <= 1);
        var final = (await store.FindVoteAsync(id, Ct))!;
        Assert.NotEqual(VoteState.Collecting, final.State);
        var perVoter = (await store.ListBallotsAsync(id, Ct)).GroupBy(b => b.VoterId).Max(g => g.Count());
        Assert.Equal(1, perVoter);
    }
}
