namespace Bazaar.Server.Community.Tests;

public class SmokeTests
{
    [Fact]
    public void Quota_values_match_doc_36_and_15()
    {
        Assert.Equal(5, CommunityQuotas.PublicItemsPerPlatformDay.Limit);
        Assert.Equal(1, CommunityQuotas.PublicIssuePrPerRepoDay.Limit);
        Assert.Equal(3, CommunityQuotas.NewReposPerWeek.Limit);
    }
}
