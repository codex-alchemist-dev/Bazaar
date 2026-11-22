using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Skimmer.Digest;

namespace Bazaar.Server.Tests.Skimmer.Digest;

/// <summary>
/// The Skimmer module cannot reference Bazaar.Server, so <see cref="DigestPlanner"/> repeats the two inbox kind strings.
/// These tests fail if either copy drifts from the owning <see cref="InboxKind"/> value.
/// </summary>
public class InboxKindDriftTests
{
    [Fact]
    public void Gc_digest_kind_matches_the_server_inbox_kind()
    {
        Assert.Equal(InboxKind.GcDigest, DigestPlanner.GcDigestKind);
    }

    [Fact]
    public void Skimmer_promoted_kind_matches_the_server_inbox_kind()
    {
        Assert.Equal(InboxKind.SkimmerPromoted, DigestPlanner.SkimmerPromotedKind);
    }
}
