using Cdax.Core;
using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.AccountApproval.OwnerQueue.Tests;

public sealed class OwnerQueueApprovalHandOffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingEnqueuer : IOwnerApprovalEnqueuer
    {
        public List<OwnerApprovalEnqueueRequest> Calls { get; } = [];

        public Exception? Throw { get; init; }

        public Task<Guid> EnqueueAsync(OwnerApprovalEnqueueRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add(request);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private static OwnerApprovalRequest Request(string id = "req-1", ApprovalEvidence? evidence = null) =>
        new(id, "SplitVote", evidence ?? new ApprovalEvidence("example.org", "agent-a", "https://example.org/terms", null, []));

    [Fact]
    public async Task Maps_fields_to_the_enqueue_request()
    {
        var enqueuer = new RecordingEnqueuer();
        await new OwnerQueueApprovalHandOff(enqueuer).RequestOwnerApprovalAsync(Request(), Ct);

        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal("agent-a", call.AgentId);
        Assert.Equal("account.request", call.Tool);
        Assert.Equal("account-approval:req-1", call.ActionKey);
        Assert.Contains("example.org", call.Summary);
        Assert.Contains("agent-a", call.Summary);
        Assert.Contains("SplitVote", call.Summary);
        Assert.Contains("https://example.org/terms", call.Summary);
    }

    [Fact]
    public void Summary_drops_user_info_query_and_fragment_from_the_terms_link()
    {
        var evidence = new ApprovalEvidence("example.org", "agent-a", "https://user:hunter2@example.org/terms?token=abc123#frag", null, []);
        var summary = OwnerQueueApprovalHandOff.BuildSummary(Request(evidence: evidence));

        Assert.Contains("https://example.org/terms", summary);
        Assert.DoesNotContain("hunter2", summary);
        Assert.DoesNotContain("abc123", summary);
        Assert.DoesNotContain("frag", summary);
    }

    [Fact]
    public void Summary_omits_a_terms_link_that_is_not_http()
    {
        var evidence = new ApprovalEvidence("example.org", "agent-a", "javascript:alert(1)", null, []);
        Assert.DoesNotContain("Terms:", OwnerQueueApprovalHandOff.BuildSummary(Request(evidence: evidence)));
    }

    [Fact]
    public void Summary_leaves_out_peer_model_ids_and_reasoning_hashes()
    {
        var votes = new[]
        {
            new PeerVote("req-1", "example.org", "peer-1", PeerVoteKind.Approve, "model-marker-xyz", "hash-marker-123", DateTimeOffset.UnixEpoch),
            new PeerVote("req-1", "example.org", "peer-2", PeerVoteKind.Refuse, "model-marker-xyz", "hash-marker-456", DateTimeOffset.UnixEpoch),
        };
        var vote = new SiteVoteResult(SiteVoteResult.SiteAllowlistMotion, "example.org", "vote-9", true, 3, 2, 1, 0);
        var summary = OwnerQueueApprovalHandOff.BuildSummary(
            Request(evidence: new ApprovalEvidence("example.org", "agent-a", null, vote, votes)));

        Assert.DoesNotContain("model-marker", summary);
        Assert.DoesNotContain("hash-marker", summary);
        Assert.Contains("Peer votes: 1 approve, 1 refuse.", summary);
        Assert.Contains("Site vote vote-9: 2 approve, 1 reject, 0 abstain of 3 eligible.", summary);
    }

    [Fact]
    public void Summary_replaces_control_characters_and_stays_within_the_cap()
    {
        var evidence = new ApprovalEvidence("exa\nmple.org" + new string('x', 5000), "agent-a", null, null, []);
        var summary = OwnerQueueApprovalHandOff.BuildSummary(Request(evidence: evidence));

        Assert.All(summary, c => Assert.False(char.IsControl(c)));
        Assert.True(summary.Length <= OwnerApprovalEnqueueRequest.MaxSummaryLength);
    }

    [Fact]
    public async Task Repeating_a_request_id_gives_one_approval_with_the_real_enqueuer()
    {
        var store = new InMemoryApprovalStore();
        var handOff = new OwnerQueueApprovalHandOff(new OwnerApprovalEnqueuer(store, new InMemoryRateBucketStore()));

        await handOff.RequestOwnerApprovalAsync(Request(), Ct);
        await handOff.RequestOwnerApprovalAsync(Request(), Ct);

        var id = OwnerApprovalEnqueuer.DeriveId("account.request", "account-approval:req-1");
        var row = await store.GetAsync(id, Ct);
        Assert.NotNull(row);
        Assert.Equal(ApprovalStatus.Pending, row.Status);
        Assert.Equal("account.request", row.Tool);
        Assert.Equal("agent-a", row.AgentId);
    }

    [Fact]
    public async Task Enqueue_failure_propagates()
    {
        var handOff = new OwnerQueueApprovalHandOff(new RecordingEnqueuer { Throw = new InvalidOperationException("store down") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => handOff.RequestOwnerApprovalAsync(Request(), Ct));
    }

    [Fact]
    public async Task Rate_limit_propagates()
    {
        var handOff = new OwnerQueueApprovalHandOff(new RecordingEnqueuer { Throw = new OwnerApprovalRateLimitedException("agent-a") });
        await Assert.ThrowsAsync<OwnerApprovalRateLimitedException>(() => handOff.RequestOwnerApprovalAsync(Request(), Ct));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var enqueuer = new RecordingEnqueuer();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OwnerQueueApprovalHandOff(enqueuer).RequestOwnerApprovalAsync(Request(), cts.Token));
        Assert.Empty(enqueuer.Calls);
    }

    [Fact]
    public async Task Blank_request_id_is_refused()
    {
        var handOff = new OwnerQueueApprovalHandOff(new RecordingEnqueuer());
        await Assert.ThrowsAsync<ArgumentException>(() => handOff.RequestOwnerApprovalAsync(Request(id: " "), Ct));
    }

    [Fact]
    public void Registration_resolves_with_an_enqueuer_and_throws_without_one()
    {
        var withStore = new ServiceCollection()
            .AddSingleton<IApprovalStore, InMemoryApprovalStore>()
            .AddSingleton<IRateBucketStore, InMemoryRateBucketStore>()
            .AddOwnerApprovalEnqueue()
            .AddBazaarAccountApprovalOwnerQueue()
            .BuildServiceProvider();
        Assert.IsType<OwnerQueueApprovalHandOff>(withStore.GetRequiredService<IOwnerApprovalHandOff>());

        var noStore = new ServiceCollection()
            .AddOwnerApprovalEnqueue()
            .AddBazaarAccountApprovalOwnerQueue()
            .BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => noStore.GetRequiredService<IOwnerApprovalHandOff>());
    }

    [Fact]
    public void Component_manifest_depends_on_account_approval_and_registers_the_hand_off()
    {
        var component = new OwnerQueueComponent();
        Assert.Equal("account-approval-owner-queue", component.Manifest.Id);
        Assert.Contains("account-approval", component.Manifest.DependsOn);

        var services = new ServiceCollection();
        component.AddServices(services, new ConfigurationBuilder().Build());
        Assert.Contains(services, d => d.ServiceType == typeof(IOwnerApprovalHandOff));
    }
}
