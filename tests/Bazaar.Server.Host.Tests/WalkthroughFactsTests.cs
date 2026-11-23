using Bazaar.Server.Company;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Http.Auth;

namespace Bazaar.Server.Host.Tests;

public class WalkthroughFactsTests
{
    private sealed class Company(CompanyProfile? profile) : ICompanyProfileReader
    {
        public Task<CompanyProfile?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(profile);
    }

    private sealed class Seed(FirstRunSeedState state, bool posted) : IFirstRunSeedReader
    {
        public (Guid Chat, Guid Person)? Asked { get; private set; }

        public Task<FirstRunSeedState> ReadSeedAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);

        public Task<bool> HasPostedAsync(Guid chatId, Guid personId, CancellationToken cancellationToken = default)
        {
            Asked = (chatId, personId);
            return Task.FromResult(posted);
        }
    }

    private static readonly CompanyProfile _profile = new("Example Studio", "example-studio", "en", "UTC", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Before_setup_there_is_no_company_agent_or_task()
    {
        var facts = await new SeededWalkthroughFactsProvider(new Company(null), new Seed(new(null, null, null), false))
            .ReadAsync(Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        Assert.False(facts.CompanyCreated);
        Assert.Null(facts.FirstAgentName);
        Assert.False(facts.FirstTaskGiven);
        Assert.Null(facts.FirstAgentId);
        Assert.Null(facts.FirstChatId);
    }

    [Fact]
    public async Task A_seeded_company_reports_the_first_agent_and_chat_and_asks_whether_the_person_posted()
    {
        var agent = Guid.NewGuid();
        var chat = Guid.NewGuid();
        var person = Guid.NewGuid();
        var seed = new Seed(new(agent, "Claude", chat), true);

        var facts = await new SeededWalkthroughFactsProvider(new Company(_profile), seed).ReadAsync(person.ToString(), TestContext.Current.CancellationToken);

        Assert.True(facts.CompanyCreated);
        Assert.Equal("Claude", facts.FirstAgentName);
        Assert.Equal(agent, facts.FirstAgentId);
        Assert.Equal(chat, facts.FirstChatId);
        Assert.True(facts.FirstTaskGiven);
        Assert.Equal((chat, person), seed.Asked);
    }

    [Fact]
    public async Task No_chat_or_a_person_id_that_is_not_a_human_id_means_no_task_without_asking()
    {
        var noChat = new Seed(new(Guid.NewGuid(), "Claude", null), true);
        Assert.False((await new SeededWalkthroughFactsProvider(new Company(_profile), noChat)
            .ReadAsync(Guid.NewGuid().ToString(), TestContext.Current.CancellationToken)).FirstTaskGiven);
        Assert.Null(noChat.Asked);

        var badId = new Seed(new(Guid.NewGuid(), "Claude", Guid.NewGuid()), true);
        Assert.False((await new SeededWalkthroughFactsProvider(new Company(_profile), badId)
            .ReadAsync("not-a-guid", TestContext.Current.CancellationToken)).FirstTaskGiven);
        Assert.Null(badId.Asked);
    }

    private sealed class Verifier(AuthenticatedCaller? caller) : IAccessTokenVerifier
    {
        public bool TryVerify(string bearerToken, out AuthenticatedCaller verified)
        {
            verified = caller!;
            return caller is not null;
        }
    }

    private static (WhoAmIBearerPersonLookup Lookup, Guid Human, Guid Device) Lookup(string status, bool revoked, bool validToken = true)
    {
        var human = Guid.NewGuid();
        var device = Guid.NewGuid();
        var store = new InMemoryRefreshCredentialStore();
        store.SetHuman(new HumanRecord(human, "Mira", "coworker", status));
        store.CreateDeviceAsync(
            new DeviceRecord(device, human, "laptop", "windows", 1, null), Guid.NewGuid(), [1, 2, 3], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1))
            .GetAwaiter().GetResult();
        if (revoked) store.RevokeDeviceAsync(device, DateTimeOffset.UnixEpoch).GetAwaiter().GetResult();
        return (new WhoAmIBearerPersonLookup(new Verifier(validToken ? new AuthenticatedCaller(device, human) : null), new SignedInPersonLookup(store)), human, device);
    }

    [Fact]
    public async Task A_valid_token_of_a_live_device_and_an_active_human_gives_the_human()
    {
        var (lookup, human, _) = Lookup("active", revoked: false);
        Assert.Equal(human, await lookup.FindHumanAsync("t", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("suspended", false, true)]
    [InlineData("removed", false, true)]
    [InlineData("active", true, true)]
    [InlineData("active", false, false)]
    public async Task Every_refusal_is_null(string status, bool revoked, bool validToken)
    {
        var (lookup, _, _) = Lookup(status, revoked, validToken);
        Assert.Null(await lookup.FindHumanAsync("t", TestContext.Current.CancellationToken));
    }
}
