using Bazaar.Server.Community.Policy;

namespace Bazaar.Server.Community.Tests.Policy;

public class PlatformPolicySeedTests
{
    private static readonly string[] _matrixPlatforms = ["Discord", "GitHub", "Godot", "Hacker News", "Reddit", "Roblox DevForum", "Stack Overflow"];

    private static readonly CommunityAction[] _readOnly = [CommunityAction.Read];

    [Fact]
    public void Seed_contains_exactly_the_matrix_platforms()
    {
        var platforms = PlatformPolicySeed.All.Select(p => p.Platform).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        Assert.Equal(_matrixPlatforms, platforms);
    }

    [Fact]
    public void Seed_statuses_match_doc_36()
    {
        var status = PlatformPolicySeed.All.ToDictionary(p => p.Platform, p => p.Status, StringComparer.Ordinal);

        Assert.Equal(PolicyStatus.Banned, status["Hacker News"]);
        Assert.Equal(PolicyStatus.Verified, status["GitHub"]);
        Assert.Equal(PolicyStatus.Unverified, status["Stack Overflow"]);
        Assert.Equal(PolicyStatus.Unverified, status["Discord"]);
        Assert.Equal(PolicyStatus.Unverified, status["Roblox DevForum"]);
        Assert.Equal(PolicyStatus.Secondary, status["Reddit"]);
        Assert.Equal(PolicyStatus.Unverified, status["Godot"]);
    }

    [Fact]
    public void Hacker_news_allows_nothing()
    {
        var hn = PlatformPolicySeed.All.Single(p => p.Platform == "Hacker News");

        Assert.Empty(hn.Allowed);
    }

    [Fact]
    public void Github_allows_the_doc_36_actions_and_nothing_else()
    {
        var github = PlatformPolicySeed.All.Single(p => p.Platform == "GitHub");

        // Enum order: Read, ReplyApprovedThread, NewIssue, NewPullRequest, EditOwn, WithdrawOwn.
        var expected = new[]
        {
            CommunityAction.Read,
            CommunityAction.ReplyApprovedThread,
            CommunityAction.NewIssue,
            CommunityAction.NewPullRequest,
            CommunityAction.EditOwn,
            CommunityAction.WithdrawOwn,
        };
        Assert.Equal(expected, github.Allowed.OrderBy(a => a));
        Assert.Equal("App identity", github.DisclosureRule);
    }

    [Fact]
    public void Read_only_platforms_allow_only_read()
    {
        foreach (var platform in new[] { "Stack Overflow", "Reddit", "Discord", "Roblox DevForum", "Godot" })
        {
            var policy = PlatformPolicySeed.All.Single(p => p.Platform == platform);

            Assert.Equal(_readOnly, policy.Allowed);
        }
    }

    [Fact]
    public void Every_seeded_record_has_an_absolute_source_and_the_seed_date()
    {
        foreach (var policy in PlatformPolicySeed.All)
        {
            Assert.True(policy.SourceUrl.IsAbsoluteUri, policy.Platform);
            Assert.Equal(new DateOnly(2026, 10, 24), policy.CheckedOn);
            Assert.False(string.IsNullOrWhiteSpace(policy.DisclosureRule), policy.Platform);
        }
    }

    [Fact]
    public void Loader_select_is_parameter_free_and_reads_platform_policy()
    {
        var sql = PlatformPolicyLoader.SelectAllSql;

        Assert.Contains("FROM platform_policy", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(";", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_is_embedded_and_defines_the_check_constraint()
    {
        using var stream = typeof(PlatformPolicyLoader).Assembly.GetManifestResourceStream(
            "Bazaar.Server.Community.Data.Sql.community.0001_platform_policy.sql");
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        Assert.Contains("CREATE TABLE platform_policy", sql, StringComparison.Ordinal);
        Assert.Contains("status IN ('Verified', 'Secondary', 'Unverified', 'Banned')", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb NOT NULL CHECK (jsonb_typeof(allowed_actions) = 'array')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_status_accepts_each_stored_value_and_rejects_others()
    {
        Assert.Equal(PolicyStatus.Verified, PlatformPolicyLoader.ParseStatus("Verified"));
        Assert.Equal(PolicyStatus.Secondary, PlatformPolicyLoader.ParseStatus("Secondary"));
        Assert.Equal(PolicyStatus.Unverified, PlatformPolicyLoader.ParseStatus("Unverified"));
        Assert.Equal(PolicyStatus.Banned, PlatformPolicyLoader.ParseStatus("Banned"));
        Assert.Throws<InvalidDataException>(() => PlatformPolicyLoader.ParseStatus("verified"));
    }

    [Fact]
    public void Parse_allowed_actions_reads_the_jsonb_array()
    {
        var actions = PlatformPolicyLoader.ParseAllowedActions("[\"Read\",\"NewIssue\"]");

        Assert.Equal(new[] { CommunityAction.Read, CommunityAction.NewIssue }, actions.OrderBy(a => a));
        Assert.Empty(PlatformPolicyLoader.ParseAllowedActions("[]"));
    }

    [Theory]
    [InlineData("[\"Launch\"]")]
    [InlineData("[\"read\"]")]
    [InlineData("[1]")]
    [InlineData("{\"Read\":true}")]
    [InlineData("not json")]
    public void Parse_allowed_actions_fails_closed_on_anything_unknown(string json)
    {
        Assert.Throws<InvalidDataException>(() => PlatformPolicyLoader.ParseAllowedActions(json));
    }
}
