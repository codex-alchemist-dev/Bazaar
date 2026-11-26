using Bazaar.Server.Mirror;

namespace Bazaar.Server.Council.Tests.Service;

public class CouncilMirrorFeedTests
{
    private sealed class FakeFeed(bool throws = false) : IMirrorFeed
    {
        public List<(string VoteRef, string Topic, bool Approved, string? Reasoning)> TieBreaks { get; } = [];

        public Task<bool> CouncilTieBrokenAsync(string voteRef, string topic, bool approved, string? reasoning, CancellationToken cancellationToken)
        {
            if (throws)
            {
                throw new InvalidOperationException("mirror is down");
            }

            TieBreaks.Add((voteRef, topic, approved, reasoning));
            return Task.FromResult(true);
        }

        public Task<bool> CardDecidedAsync(string cardRef, bool accepted, string? reason, IReadOnlyList<string> topics, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> OwnerEditedAsync(string sourceRef, IReadOnlyList<string> removedTopics, IReadOnlyList<string> addedTopics, string? note, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> ReviewCommentedAsync(string sourceRef, string topic, MirrorDirection direction, string comment, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> PreferenceStatedAsync(string statementRef, string topic, MirrorDirection direction, string statement, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private static async Task<(CouncilHarness H, Guid Id)> EscalatedOptionVoteAsync(IMirrorFeed feed)
    {
        var h = new CouncilHarness { Mirror = feed };
        h.AddThreeVoters();
        var id = await h.CreateService().OpenAsync("ada", h.Motion(VoteKind.OptionChoice, options: ["alpha", "beta", "gamma"]), CancellationToken.None);
        var service = h.CreateService();
        foreach (var (voter, choice) in new[] { ("cal", "alpha"), ("dot", "beta"), ("eve", "gamma") })
        {
            await service.SubmitBallotAsync(id, voter, Ballots.Independent(voter, choice), CancellationToken.None);
        }

        foreach (var (voter, choice) in new[] { ("cal", "alpha"), ("dot", "beta"), ("eve", "gamma") })
        {
            await service.SubmitBallotAsync(id, voter, Ballots.FollowUp(voter, choice), CancellationToken.None);
        }

        return (h, id);
    }

    [Fact]
    public async Task Owner_resolution_feeds_mirror_the_chosen_option()
    {
        var feed = new FakeFeed();
        var (h, id) = await EscalatedOptionVoteAsync(feed);

        await h.CreateService().ResolveEscalationAsync(id, "beta", "beta is simpler", CancellationToken.None);

        var tie = Assert.Single(feed.TieBreaks);
        Assert.Equal($"council:{id:D}", tie.VoteRef);
        Assert.EndsWith(":beta", tie.Topic, StringComparison.Ordinal);
        Assert.True(tie.Approved);
        Assert.Equal("beta is simpler", tie.Reasoning);
    }

    [Fact]
    public async Task A_failing_mirror_never_fails_the_owner_decision()
    {
        var (h, id) = await EscalatedOptionVoteAsync(new FakeFeed(throws: true));

        await h.CreateService().ResolveEscalationAsync(id, "gamma", null, CancellationToken.None);

        Assert.Single(h.Events.OfKind("council.decision"));
    }

    [Fact]
    public async Task Without_a_mirror_resolution_works_as_before()
    {
        var (h, id) = await EscalatedOptionVoteAsync(new FakeFeed());
        h.Mirror = null;

        await h.CreateService().ResolveEscalationAsync(id, "alpha", null, CancellationToken.None);

        Assert.Single(h.Events.OfKind("council.decision"));
    }
}
