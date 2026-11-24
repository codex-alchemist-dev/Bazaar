using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Hive.Grants;
using Bazaar.Client.Hive.Panes;
using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Hive.Tests;

public class WireHiveGrantActionsTests
{
    private static readonly HiveGrantKey _key = new("alpha", "beta", "procedural");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRequester : IWireRequester
    {
        public List<(string Type, object Payload)> Sent { get; } = [];
        public HiveGrantChangeReply Reply { get; set; } = new(true, null, false);
        public Exception? Failure { get; set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload!));
            return Failure is not null ? throw Failure : Task.FromResult((TResponse)(object)Reply);
        }
    }

    [Fact]
    public async Task Grant_sends_the_set_type_with_the_key()
    {
        var requests = new FakeRequester();

        var result = await new WireHiveGrantActions(requests).GrantAsync(_key, Ct);

        var sent = Assert.Single(requests.Sent);
        Assert.Equal(WireTypes.HiveGrantSet, sent.Type);
        Assert.Equal(new HiveGrantChangeRequest("alpha", "beta", "procedural"), sent.Payload);
        Assert.True(result.Accepted);
        Assert.False(result.ApprovalRequired);
        Assert.Equal(string.Empty, result.Reason);
    }

    [Fact]
    public async Task Revoke_sends_the_revoke_type()
    {
        var requests = new FakeRequester();

        await new WireHiveGrantActions(requests).RevokeAsync(_key, Ct);

        Assert.Equal(WireTypes.HiveGrantRevoke, Assert.Single(requests.Sent).Type);
    }

    [Fact]
    public async Task A_refusal_reason_is_carried_into_the_result()
    {
        var requests = new FakeRequester { Reply = new HiveGrantChangeReply(false, "not the owner", false) };

        var result = await new WireHiveGrantActions(requests).GrantAsync(_key, Ct);

        Assert.False(result.Accepted);
        Assert.Equal("not the owner", result.Reason);
    }

    [Fact]
    public async Task A_lost_connection_reaches_the_caller()
    {
        var requests = new FakeRequester { Failure = new InvalidOperationException("lost") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WireHiveGrantActions(requests).GrantAsync(_key, Ct));
    }

    [Fact]
    public async Task The_pane_shows_waiting_for_approval_and_never_granted()
    {
        var requests = new FakeRequester { Reply = new HiveGrantChangeReply(true, null, true) };
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model), new WireHiveGrantActions(requests));
        var before = pane.Matrix;

        await pane.RequestAsync(_key, grant: true, Ct);

        Assert.Equal("Waiting for owner approval. Nothing changes until it is approved.", pane.LastStatus);
        Assert.Same(before, pane.Matrix);
    }
}
