using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests.KillSwitch;

/// <summary>One agent is one agent however its id is spelled: stops, resumes, evaluation and containment all compare canonical ids.</summary>
public class KillSwitchAgentIdSpellingTests
{
    private static readonly Guid _agent = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly string _canonical = _agent.ToString("D");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));

    public static TheoryData<string, string> SpellingPairs()
    {
        string[] spellings =
        [
            _agent.ToString("D"),
            _agent.ToString("D").ToUpperInvariant(),
            _agent.ToString("B"),
            _agent.ToString("B").ToUpperInvariant(),
            _agent.ToString("N"),
            "  " + _agent.ToString("D") + " ",
        ];
        var data = new TheoryData<string, string>();
        foreach (var engaged in spellings)
        {
            foreach (var resumed in spellings)
            {
                data.Add(engaged, resumed);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SpellingPairs))]
    public async Task A_stop_under_one_spelling_stops_resumes_and_evaluates_under_every_spelling(string engaged, string resumed)
    {
        var ct = TestContext.Current.CancellationToken;
        var containment = new RecordingContainment();
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), containment, _clock);

        await engine.EngageAgentAsync("owner-1", engaged, "Rate breach", ct);

        Assert.False(engine.Evaluate(resumed, ToolEffect.Read).Allowed);
        Assert.False(engine.Evaluate(_canonical, ToolEffect.Write).Allowed);
        var stop = Assert.Single(engine.GetStatus().AgentStops);
        Assert.Equal(_canonical, stop.AgentId);
        Assert.Equal(["contain:" + _canonical], containment.Calls);

        // A second engage under another spelling replaces the stop rather than adding a second one.
        await engine.EngageAgentAsync("owner-1", resumed, "Again", ct);
        Assert.Single(engine.GetStatus().AgentStops);

        await engine.ResumeAgentAsync(resumed, "All clear", ReleaseRequests.Unsigned(resumed, "dev-owner"), ct);

        Assert.True(engine.Evaluate(engaged, ToolEffect.Write).Allowed);
        Assert.Empty(engine.GetStatus().AgentStops);
    }

    [Fact]
    public async Task A_stop_stored_under_another_spelling_is_loaded_enforced_and_cleared()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new LegacySpellingStore(new KillSwitchStop(KillSwitchScope.Agent, _agent.ToString("B").ToUpperInvariant(), "owner-1", "Old stop", _clock.GetUtcNow()));
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _clock);
        await engine.WaitUntilStateLoadedAsync(ct);

        Assert.False(engine.Evaluate(_canonical, ToolEffect.Read).Allowed);
        Assert.Equal(_canonical, Assert.Single(engine.GetStatus().AgentStops).AgentId);

        await engine.ResumeAgentAsync(_agent.ToString("N"), "Clear", ReleaseRequests.Unsigned(_canonical, "dev-owner"), ct);

        Assert.True(engine.Evaluate(_canonical, ToolEffect.Read).Allowed);
        Assert.Equal([_canonical], store.Cleared);
    }

    [Fact]
    public async Task The_in_memory_state_store_keeps_one_canonical_stop_per_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryKillSwitchStateStore();

        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, _agent.ToString("N"), "a", "first", _clock.GetUtcNow()), ct);
        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, _agent.ToString("B"), "a", "second", _clock.GetUtcNow()), ct);

        var stop = Assert.Single((await store.LoadAsync(ct)).AgentStops);
        Assert.Equal(_canonical, stop.AgentId);
        Assert.Equal("second", stop.Reason);

        await store.ClearAgentStopAsync(_agent.ToString("D").ToUpperInvariant(), ct);
        Assert.Empty((await store.LoadAsync(ct)).AgentStops);
    }

    [Fact]
    public async Task The_in_memory_containment_store_matches_every_spelling()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryContainmentStateStore();

        await store.AddAsync(new ContainedAgent(_agent.ToString("N"), _clock.GetUtcNow(), "first"), ct);
        await store.AddAsync(new ContainedAgent(_agent.ToString("B"), _clock.GetUtcNow().AddMinutes(1), "second"), ct);

        var contained = Assert.Single(await store.LoadAsync(ct));
        Assert.Equal(_canonical, contained.AgentId);
        Assert.Equal("first", contained.ActorId);

        await store.RemoveAsync(_agent.ToString("D").ToUpperInvariant(), ct);
        Assert.Empty(await store.LoadAsync(ct));
    }

    [Fact]
    public async Task Containment_and_release_match_across_spellings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, ownerKey, store) = Adapter();

        await adapter.ContainAsync(_agent.ToString("B"), "owner-1", ct);
        Assert.Equal(_canonical, Assert.Single(await store.LoadAsync(ct)).AgentId);

        var challenge = await adapter.IssueReleaseChallengeAsync(_agent.ToString("N"), "dev-owner", ct);
        Assert.NotNull(challenge);
        var signedAs = _agent.ToString("D").ToUpperInvariant();
        var request = new ContainmentReleaseRequest(signedAs, "dev-owner", challenge.Nonce, challenge.ExpiresAt, Sign(ownerKey, signedAs, challenge));

        var result = await adapter.ReleaseAsync(_agent.ToString("B").ToUpperInvariant(), request, ct);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.Empty(await store.LoadAsync(ct));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    public async Task A_forged_signature_leaves_the_stop_in_place_under_every_spelling(string format)
    {
        var ct = TestContext.Current.CancellationToken;
        var (adapter, _, _) = Adapter();
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), adapter, _clock);
        var spelling = _agent.ToString(format);
        await engine.EngageAgentAsync("owner-1", spelling, "Rate breach", ct);

        var challenge = await adapter.IssueReleaseChallengeAsync(spelling, "dev-owner", ct);
        Assert.NotNull(challenge);
        var request = new ContainmentReleaseRequest(spelling, "dev-owner", challenge.Nonce, challenge.ExpiresAt, Sign(forger, spelling, challenge));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.ResumeAgentAsync(_agent.ToString("B").ToUpperInvariant(), "Forged", request, ct));

        Assert.False(engine.Evaluate(_canonical, ToolEffect.Read).Allowed);
        Assert.False(engine.Evaluate(_agent.ToString("N"), ToolEffect.Read).Allowed);
        Assert.Single(engine.GetStatus().AgentStops);
    }

    private (AgentContainmentAdapter Adapter, ECDsa OwnerKey, InMemoryContainmentStateStore Store) Adapter()
    {
        var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var owners = new InMemoryOwnerKeyRegistry();
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", ownerKey.ExportSubjectPublicKeyInfo()));
        var store = new InMemoryContainmentStateStore();
        var order = new List<string>();
        var adapter = new AgentContainmentAdapter(
            new OrderedVmPauser(order), new OrderedTokenRevoker(order), owners, new RecordingAuditLog(), new InMemoryNonceStore(_clock), store, _clock);
        return (adapter, ownerKey, store);
    }

    private static byte[] Sign(ECDsa key, string agentId, ReleaseChallenge challenge) =>
        key.SignData(
            ApprovalSignature.ReleaseMessage(agentId, "dev-owner", challenge.Nonce, challenge.ExpiresAt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>A state store that already holds a stop under a spelling the engine no longer writes, and records what is cleared.</summary>
    private sealed class LegacySpellingStore(KillSwitchStop legacy) : IKillSwitchStateStore
    {
        public List<string> Cleared { get; } = [];

        public Task<KillSwitchStatus> LoadAsync(CancellationToken ct = default) => Task.FromResult(new KillSwitchStatus(null, [legacy]));

        public Task SetGlobalAsync(KillSwitchStop engaged, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearGlobalAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetAgentStopAsync(KillSwitchStop engaged, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearAgentStopAsync(string agentId, CancellationToken ct = default)
        {
            Cleared.Add(agentId);
            return Task.CompletedTask;
        }
    }
}
