using System.Text.Json;
using Bazaar.Community.Drafts;
using Bazaar.Community.Profile;
using Bazaar.Community.Tools;
using Bazaar.Contracts.Community;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Community;
using Xunit;

namespace Bazaar.Community.Tests.Tools;

public sealed class CommunityProposePostToolTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class FakeCreator : ICommunityDraftCreator
    {
        public CommunityDraftProposal? Seen { get; private set; }

        public CommunityDraftCreateResult Result { get; set; } = new(false, "x");

        public Task<CommunityDraftCreateResult> CreateAsync(CommunityDraftProposal proposal, CancellationToken ct = default)
        {
            Seen = proposal;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeIdentities(CommunityAgentIdentity? identity) : ICommunityAgentIdentitySource
    {
        public Task<CommunityAgentIdentity?> FindAsync(Guid agentId, string platform, CancellationToken ct = default) =>
            Task.FromResult(identity);
    }

    private static CommunityAgentIdentity Identity() => new("sponsor-1",
        new BotProfile(_agent.ToString("D"), "github", "Sage", "Dom", "dom@example.org", new Uri("https://example.org/bots")));

    private static ToolCallContext Context() => new(_agent, Guid.NewGuid(), Guid.NewGuid());

    private static JsonElement Args(string json) => JsonElement.Parse(json);

    private const string _good = """
        {"platform":"github","action":"issue","kind":"issue","target":"o/r","title":"T","body":"Text",
         "duplicateSearchQueries":["q"],"isBugReport":false}
        """;

    private static DraftReviewCardDto Card(Guid id, params QualityGateFindingDto[] gates) =>
        new(id, "github", "issue", "o/r", "T", "Text", "h", true, gates, null!);

    [Fact]
    public async Task Stores_a_draft_for_the_calling_agent_and_reports_failed_gates()
    {
        var id = Guid.NewGuid();
        var creator = new FakeCreator { Result = new(true, "ok", Card(id, new("duplicate_search", false, "none"), new("secrets", true, ""))) };
        var tool = new CommunityProposePostTool(creator, new FakeIdentities(Identity()));

        var json = JsonElement.Parse(await tool.ExecuteAsync(Args(_good), Context(), TestContext.Current.CancellationToken));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(id.ToString("D"), json.GetProperty("draftId").GetString());
        Assert.Equal(["duplicate_search"], json.GetProperty("gateFailures").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(_agent.ToString("D"), creator.Seen!.AgentId);
        Assert.Equal("sponsor-1", creator.Seen.SponsorId);
        Assert.Equal(CommunityAction.NewIssue, creator.Seen.Action);
        Assert.Equal("Text", creator.Seen.Gate.Body);
    }

    [Fact]
    public async Task Refuses_when_the_agent_has_no_profile()
    {
        var creator = new FakeCreator();
        var tool = new CommunityProposePostTool(creator, new FakeIdentities(null));

        var json = JsonElement.Parse(await tool.ExecuteAsync(Args(_good), Context(), TestContext.Current.CancellationToken));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("no_profile", json.GetProperty("code").GetString());
        Assert.Null(creator.Seen);
    }

    [Theory]
    [InlineData("""{"platform":"github","action":"issue","kind":"issue","target":"o/r"}""")]
    [InlineData("""{"platform":"github","action":"issue","kind":"issue","target":"o/r","body":5}""")]
    [InlineData("""{"platform":"github","action":"issue","kind":"issue","target":"o/r","body":"x","relatedLinks":[1]}""")]
    [InlineData("""[]""")]
    public async Task Refuses_malformed_arguments(string args)
    {
        var tool = new CommunityProposePostTool(new FakeCreator(), new FakeIdentities(Identity()));
        var json = JsonElement.Parse(await tool.ExecuteAsync(Args(args), Context(), TestContext.Current.CancellationToken));
        Assert.Equal("bad_request", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Refuses_an_action_outside_the_list()
    {
        var tool = new CommunityProposePostTool(new FakeCreator(), new FakeIdentities(Identity()));
        var json = JsonElement.Parse(await tool.ExecuteAsync(
            Args(_good.Replace("\"issue\",\"kind\"", "\"delete\",\"kind\"")), Context(), TestContext.Current.CancellationToken));
        Assert.Equal("unsupported_action", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Passes_the_creator_refusal_code_through()
    {
        var creator = new FakeCreator { Result = new(false, "bad_profile") };
        var tool = new CommunityProposePostTool(creator, new FakeIdentities(Identity()));
        var json = JsonElement.Parse(await tool.ExecuteAsync(Args(_good), Context(), TestContext.Current.CancellationToken));
        Assert.Equal("bad_profile", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_plain_overload_refuses_a_call_without_a_caller()
    {
#pragma warning disable CA1859 // The interface is the point: a caller going through ITool must be refused.
        ITool tool = new CommunityProposePostTool(new FakeCreator(), new FakeIdentities(Identity()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(Args(_good), TestContext.Current.CancellationToken));
#pragma warning restore CA1859
    }

    [Fact]
    public void The_definition_schema_is_valid_json_and_names_the_tool()
    {
        var doc = JsonElement.Parse(CommunityProposePostTool.Definition.ParametersJsonSchema);
        Assert.Equal("community.propose_post", CommunityProposePostTool.Definition.Name);
        Assert.Equal(5, doc.GetProperty("required").GetArrayLength());
    }
}
