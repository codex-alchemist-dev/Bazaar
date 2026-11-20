using System.Text.Json;
using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Mail.Quarantine;

namespace Bazaar.Server.Mail.Tests.Quarantine;

public class QuarantineStoreTests
{
    private static readonly Guid _owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _person = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly QuarantineTransitionContext _none = new();
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    private static QuarantineItem Item(QuarantineState state = QuarantineState.Held, string domain = "spam.example", DateTimeOffset? received = null, Guid? person = null, DateTimeOffset? until = null) =>
        new(Guid.NewGuid(), person ?? _person, domain, ["AuthenticationFailed"], "spf=fail", received ?? _t0, state, until);

    private sealed class FakeHolds(params Guid[] held) : ILegalHoldReader
    {
        public int Calls { get; private set; }

        public Task<bool> IsPersonHeldAsync(Guid personId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(held.Contains(personId));
        }
    }

    // ---- state machine

    [Fact]
    public void Owner_releases_a_held_item()
    {
        var result = QuarantineStateMachine.Apply(Item(), QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none);
        Assert.True(result.IsAllowed);
        Assert.Equal(QuarantineState.Released, result.Item!.State);
    }

    [Fact]
    public void Release_keeps_the_taint()
    {
        var released = QuarantineStateMachine.Apply(Item(), QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none).Item!;
        Assert.True(released.Tainted);
    }

    [Fact]
    public void Reader_cannot_release_or_report()
    {
        foreach (var to in new[] { QuarantineState.Released, QuarantineState.Reported })
        {
            var result = QuarantineStateMachine.Apply(Item(), to, QuarantineActor.Reader, new QuarantineTransitionContext(ReportApprovalId: Guid.NewGuid()));
            Assert.Equal(QuarantineTransitionOutcome.ReaderCannotRelease, result.Outcome);
            Assert.Equal(QuarantineState.Held, result.Item!.State);
        }
    }

    [Fact]
    public void Agent_and_system_cannot_release()
    {
        Assert.Equal(QuarantineTransitionOutcome.AgentCannotChange,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Released, QuarantineActor.Agent, _none).Outcome);
        Assert.Equal(QuarantineTransitionOutcome.NotOwner,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Released, QuarantineActor.System, _none).Outcome);
    }

    [Fact]
    public void Owner_actor_without_a_human_id_is_not_an_owner()
    {
        var result = QuarantineStateMachine.Apply(Item(), QuarantineState.Released, new QuarantineActor(QuarantineActorKind.Owner), _none);
        Assert.Equal(QuarantineTransitionOutcome.NotOwner, result.Outcome);
    }

    [Fact]
    public void Report_needs_owner_and_a_per_report_approval()
    {
        var owner = QuarantineActor.ForOwner(_owner);
        Assert.Equal(QuarantineTransitionOutcome.ReportNeedsApproval,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Reported, owner, _none).Outcome);
        Assert.Equal(QuarantineTransitionOutcome.ReportNeedsApproval,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Reported, owner, new QuarantineTransitionContext(ReportApprovalId: Guid.Empty)).Outcome);
        Assert.Equal(QuarantineTransitionOutcome.NotOwner,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Reported, QuarantineActor.System, new QuarantineTransitionContext(ReportApprovalId: Guid.NewGuid())).Outcome);

        var ok = QuarantineStateMachine.Apply(Item(), QuarantineState.Reported, owner, new QuarantineTransitionContext(ReportApprovalId: Guid.NewGuid()));
        Assert.True(ok.IsAllowed);
        Assert.Equal(QuarantineState.Reported, ok.Item!.State);
    }

    [Fact]
    public void System_purges_a_due_item_only()
    {
        Assert.Equal(QuarantineTransitionOutcome.NotDueForPurge,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Purged, QuarantineActor.System, _none).Outcome);
        var ok = QuarantineStateMachine.Apply(Item(), QuarantineState.Purged, QuarantineActor.System, new QuarantineTransitionContext(RetentionDue: true));
        Assert.Equal(QuarantineState.Purged, ok.Item!.State);
        Assert.Equal(QuarantineTransitionOutcome.NotOwner,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Purged, QuarantineActor.ForOwner(_owner), new QuarantineTransitionContext(RetentionDue: true)).Outcome);
    }

    [Fact]
    public void Legal_hold_blocks_purge_even_when_due()
    {
        var result = QuarantineStateMachine.Apply(Item(), QuarantineState.Purged, QuarantineActor.System, new QuarantineTransitionContext(RetentionDue: true, LegalHold: true));
        Assert.Equal(QuarantineTransitionOutcome.LegalHold, result.Outcome);
        Assert.Equal(QuarantineState.Held, result.Item!.State);
    }

    [Theory]
    [InlineData(QuarantineState.Released)]
    [InlineData(QuarantineState.Purged)]
    [InlineData(QuarantineState.Reported)]
    public void Only_held_items_can_move_on(QuarantineState from)
    {
        var owner = QuarantineActor.ForOwner(_owner);
        var context = new QuarantineTransitionContext(true, false, Guid.NewGuid());
        foreach (var to in Enum.GetValues<QuarantineState>())
        {
            var result = QuarantineStateMachine.Apply(Item(from), to, owner, context);
            Assert.Equal(QuarantineTransitionOutcome.IllegalTransition, result.Outcome);
            Assert.Equal(from, result.Item!.State);
        }
    }

    [Fact]
    public void Held_to_held_is_illegal()
    {
        Assert.Equal(QuarantineTransitionOutcome.IllegalTransition,
            QuarantineStateMachine.Apply(Item(), QuarantineState.Held, QuarantineActor.ForOwner(_owner), _none).Outcome);
    }

    [Fact]
    public void Reader_and_system_may_admit_agent_may_not()
    {
        Assert.True(QuarantineStateMachine.CanAdmit(QuarantineActor.Reader));
        Assert.True(QuarantineStateMachine.CanAdmit(QuarantineActor.System));
        Assert.False(QuarantineStateMachine.CanAdmit(QuarantineActor.Agent));
        Assert.False(QuarantineStateMachine.CanAdmit(QuarantineActor.ForOwner(_owner)));
    }

    // ---- in-memory store

    [Fact]
    public async Task Store_admits_as_held_and_applies_transitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryMailQuarantineStore();
        var item = Item(QuarantineState.Released);
        var admit = await store.AdmitAsync(item, QuarantineActor.Reader, ct);
        Assert.Equal(QuarantineState.Held, admit.Item!.State);

        var denied = await store.TransitionAsync(item.Id, QuarantineState.Released, QuarantineActor.Reader, _none, ct);
        Assert.False(denied.IsAllowed);
        Assert.Equal(QuarantineState.Held, (await store.GetAsync(item.Id, ct))!.State);

        var ok = await store.TransitionAsync(item.Id, QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none, ct);
        Assert.True(ok.IsAllowed);
        Assert.Equal(QuarantineState.Released, (await store.GetAsync(item.Id, ct))!.State);
        Assert.Equal(0, await store.CountHeldAsync(ct));
    }

    [Fact]
    public async Task Store_refuses_agent_admit_missing_item_and_readmit()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryMailQuarantineStore();
        var item = Item();
        Assert.Equal(QuarantineTransitionOutcome.AgentCannotChange, (await store.AdmitAsync(item, QuarantineActor.Agent, ct)).Outcome);
        Assert.Equal(QuarantineTransitionOutcome.NotFound,
            (await store.TransitionAsync(Guid.NewGuid(), QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none, ct)).Outcome);

        await store.AdmitAsync(item, QuarantineActor.System, ct);
        await store.TransitionAsync(item.Id, QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none, ct);
        var again = await store.AdmitAsync(item, QuarantineActor.Reader, ct);
        Assert.Equal(QuarantineTransitionOutcome.IllegalTransition, again.Outcome);
        Assert.Equal(QuarantineState.Released, (await store.GetAsync(item.Id, ct))!.State);
    }

    // ---- digest

    [Fact]
    public void Digest_counts_states_and_ranks_held_domains()
    {
        var items = new[]
        {
            Item(domain: "a.example"), Item(domain: "b.example"), Item(domain: "b.example"), Item(domain: "A.example"),
            Item(QuarantineState.Released), Item(QuarantineState.Purged), Item(QuarantineState.Purged), Item(QuarantineState.Reported),
        };
        var digest = QuarantineDigestBuilder.Build(items, new DateOnly(2026, 11, 18), topDomains: 2);

        Assert.Equal(new DateOnly(2026, 11, 18), digest.Date);
        Assert.Equal((4, 1, 2, 1), (digest.Held, digest.Released, digest.Purged, digest.Reported));
        Assert.Equal([new TopSenderDomain("a.example", 2), new TopSenderDomain("b.example", 2)], digest.TopSenderDomains);
    }

    [Fact]
    public void Digest_has_no_body_or_full_address()
    {
        var withAddress = Item(domain: "alice@secret-sender.example");
        var digest = QuarantineDigestBuilder.Build([withAddress], new DateOnly(2026, 11, 18));
        var json = JsonSerializer.Serialize(digest);

        Assert.DoesNotContain("@", json);
        Assert.DoesNotContain("alice", json);
        Assert.Contains("secret-sender.example", json);
        Assert.DoesNotContain("body", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Digest_property_names_match_the_pane_model_keys()
    {
        var json = JsonSerializer.Serialize(QuarantineDigestBuilder.Build([], new DateOnly(2026, 11, 18)), _web);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        string[] expected = ["date", "held", "purged", "released", "reported", "topSenderDomains"];
        Assert.Equal(expected.Order().ToArray(), names);
    }

    // ---- agent view

    [Fact]
    public async Task Agent_view_is_a_count_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryMailQuarantineStore();
        await store.AdmitAsync(Item(), QuarantineActor.Reader, ct);
        await store.AdmitAsync(Item(), QuarantineActor.Reader, ct);
        var view = QuarantineDigestBuilder.CountView(await store.CountHeldAsync(ct));

        Assert.Equal(2, view.Held);
        var props = typeof(QuarantineCountView).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(["Held"], props);
        Assert.Equal(0, QuarantineDigestBuilder.CountView(-3).Held);
    }

    // ---- retention

    [Fact]
    public void Retention_boundary_is_inclusive()
    {
        var item = Item(received: _t0);
        var due = _t0.AddDays(90);
        Assert.False(QuarantineRetention.IsDue(item, 90, due.AddTicks(-1)));
        Assert.True(QuarantineRetention.IsDue(item, 90, due));
        Assert.True(QuarantineRetention.IsDue(item, 90, due.AddTicks(1)));
    }

    [Fact]
    public void Explicit_retention_until_overrides_the_setting()
    {
        var item = Item(received: _t0, until: _t0.AddDays(5));
        Assert.True(QuarantineRetention.IsDue(item, 90, _t0.AddDays(5)));
        Assert.Equal(_t0.AddDays(5), QuarantineRetention.DueAt(item, 90));
    }

    [Fact]
    public void Retention_days_below_one_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuarantineRetention.DueAt(Item(), 0));
    }

    [Fact]
    public void Due_list_skips_held_persons_and_other_states_and_is_oldest_first()
    {
        var other = Guid.NewGuid();
        var now = _t0.AddDays(100);
        var oldest = Item(received: _t0.AddDays(-5));
        var newer = Item(received: _t0);
        var notDue = Item(received: _t0.AddDays(50));
        var released = Item(QuarantineState.Released, received: _t0);
        var onHold = Item(person: other, received: _t0);

        var due = QuarantineRetention.DueForPurge([newer, notDue, released, onHold, oldest], 90, now, new HashSet<Guid> { other });
        Assert.Equal([oldest.Id, newer.Id], due.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Async_due_list_asks_the_legal_hold_reader_once_per_person()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = Guid.NewGuid();
        var holds = new FakeHolds(other);
        var a = Item(received: _t0);
        var b = Item(received: _t0);
        var c = Item(person: other, received: _t0);

        var due = await QuarantineRetention.DueForPurgeAsync(new[] { a, b, c }, 90, _t0.AddDays(91), holds, ct);

        Assert.Equal(new[] { a.Id, b.Id }.Order().ToArray(), due.Select(i => i.Id).Order().ToArray());
        Assert.Equal(2, holds.Calls);
    }
}
