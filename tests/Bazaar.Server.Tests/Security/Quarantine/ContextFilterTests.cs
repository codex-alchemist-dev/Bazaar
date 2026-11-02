using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Quarantine;

public class ContextFilterTests
{
    private readonly ContextFilter _filter = new();

    [Fact]
    public void Quarantined_items_are_dropped_and_clean_items_keep_their_order()
    {
        var candidates = new[]
        {
            new ContextCandidate<string>("first", ContentTrust.Owner, null),
            new ContextCandidate<string>("hidden", ContentTrust.Agent, QuarantineStatus.Quarantined),
            new ContextCandidate<string>("second", ContentTrust.System, null),
            new ContextCandidate<string>("third", ContentTrust.Agent, QuarantineStatus.Released),
        };

        var result = _filter.Filter(candidates);

        Assert.Equal(["first", "second", "third"], result.Select(c => c.Item));
    }

    [Fact]
    public void Quarantined_trust_tag_is_dropped_even_without_a_quarantine_record()
    {
        var candidates = new[] { new ContextCandidate<string>("flagged", ContentTrust.Quarantined, null) };

        Assert.Empty(_filter.Filter(candidates));
    }

    [Fact]
    public void A_released_external_item_is_delivered_with_its_trust_tag_intact()
    {
        var candidates = new[] { new ContextCandidate<string>("mail", ContentTrust.External, QuarantineStatus.Released) };

        var result = _filter.Filter(candidates);

        var item = Assert.Single(result);
        Assert.Equal(ContentTrust.External, item.Trust);
        Assert.Equal(TaintState.Hard, TaintRules.Contribution(new TaintInput(Guid.Empty, item.Trust), TaintPolicy.Default));
    }

    [Fact]
    public void Filtering_an_empty_list_returns_an_empty_list()
    {
        Assert.Empty(_filter.Filter(Array.Empty<ContextCandidate<string>>()));
    }

    [Theory]
    [InlineData(ContentTrust.Owner, null, true)]
    [InlineData(ContentTrust.External, QuarantineStatus.Released, true)]
    [InlineData(ContentTrust.Agent, QuarantineStatus.Quarantined, false)]
    [InlineData(ContentTrust.Quarantined, null, false)]
    public void Promotion_is_refused_for_quarantined_or_flagged_items(ContentTrust trust, QuarantineStatus? status, bool expected)
    {
        Assert.Equal(expected, _filter.CanPromote(trust, status));
    }
}
