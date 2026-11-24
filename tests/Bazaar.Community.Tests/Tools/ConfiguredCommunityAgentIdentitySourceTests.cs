using Bazaar.Community.Tools;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Bazaar.Community.Tests.Tools;

public sealed class ConfiguredCommunityAgentIdentitySourceTests
{
    private static readonly Guid _agent = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CommunityProfileEntry Entry() => new()
    {
        AgentId = _agent.ToString("D"), Platform = "GitHub", SponsorId = "sponsor-1", DisplayName = "Sage",
        SponsorName = "Dom", Contact = "dom@example.org", DisclosureUrl = "https://example.org/bots",
    };

    [Fact]
    public async Task Finds_a_listed_agent_ignoring_platform_case()
    {
        var source = new ConfiguredCommunityAgentIdentitySource([Entry()]);

        var found = await source.FindAsync(_agent, "github", TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal("sponsor-1", found.SponsorId);
        Assert.Equal(_agent.ToString("D"), found.Profile.AgentId);
        Assert.Equal("github", found.Profile.Platform);
    }

    [Fact]
    public async Task An_unlisted_agent_or_platform_is_null()
    {
        var source = new ConfiguredCommunityAgentIdentitySource([Entry()]);

        Assert.Null(await source.FindAsync(Guid.NewGuid(), "github", TestContext.Current.CancellationToken));
        Assert.Null(await source.FindAsync(_agent, "reddit", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("sponsor", "")]
    [InlineData("disclosure", "not a url")]
    [InlineData("disclosure", "http://example.org/bots")]
    [InlineData("contact", "")]
    public async Task A_bad_entry_fails_closed(string field, string value)
    {
        var e = Entry();
        if (field == "sponsor") e.SponsorId = value;
        else if (field == "disclosure") e.DisclosureUrl = value;
        else e.Contact = value;

        Assert.Null(await new ConfiguredCommunityAgentIdentitySource([e]).FindAsync(_agent, "github", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Binds_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Community:Profiles:0:AgentId"] = _agent.ToString("D"),
            ["Bazaar:Community:Profiles:0:Platform"] = "github",
            ["Bazaar:Community:Profiles:0:SponsorId"] = "s",
            ["Bazaar:Community:Profiles:0:DisplayName"] = "Sage",
            ["Bazaar:Community:Profiles:0:SponsorName"] = "Dom",
            ["Bazaar:Community:Profiles:0:Contact"] = "dom@example.org",
            ["Bazaar:Community:Profiles:0:DisclosureUrl"] = "https://example.org/bots",
        }).Build();

        Assert.NotNull(await ConfiguredCommunityAgentIdentitySource.From(config).FindAsync(_agent, "github", TestContext.Current.CancellationToken));
        Assert.Null(await ConfiguredCommunityAgentIdentitySource.From(new ConfigurationBuilder().Build()).FindAsync(_agent, "github", TestContext.Current.CancellationToken));
    }
}
