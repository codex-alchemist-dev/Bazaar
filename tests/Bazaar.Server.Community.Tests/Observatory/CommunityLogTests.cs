using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Community.Actions;
using Bazaar.Server.Community.Observatory;

namespace Bazaar.Server.Community.Tests.Observatory;

/// <summary>Checks the Observatory projection: no body in the output, correct body hash, and the page-size clamp.</summary>
public class CommunityLogTests
{
    private const string _sentinelBody = "SENTINEL-BODY-7f3c9a1e-do-not-project";

    [Fact]
    public void Projected_row_never_contains_the_body_text()
    {
        var record = Record(body: _sentinelBody);

        var row = CommunityLog.Project(record);
        var json = JsonSerializer.Serialize(row);

        Assert.DoesNotContain(_sentinelBody, json, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_type_has_no_body_property()
    {
        var names = typeof(CommunityLogRow).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain("Body", names);
        Assert.DoesNotContain(names, name => name.Contains("Body", StringComparison.OrdinalIgnoreCase) && name != "BodySha256Hex");
    }

    [Fact]
    public void Body_hash_is_projected_as_lowercase_hex()
    {
        // SHA-256 of "abc" (FIPS 180-2 test vector).
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("abc"));

        var row = CommunityLog.Project(Record(body: "abc", bodySha256: hash));

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", row.BodySha256Hex);
    }

    [Fact]
    public void Body_hash_is_null_when_the_action_had_no_body()
    {
        var row = CommunityLog.Project(Record(body: null, bodySha256: null));

        Assert.Null(row.BodySha256Hex);
    }

    [Fact]
    public void Projection_keeps_the_row_columns()
    {
        var created = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var record = Record(body: null) with
        {
            Id = 42,
            AgentId = "agent-a",
            Platform = "github",
            Action = CommunityAction.NewIssue,
            TargetUrl = "https://example.test/issue",
            Status = "done",
            CreatedAt = created,
            ApprovalId = "appr-1",
            ExternalId = "ext-9",
        };

        var row = CommunityLog.Project(record);

        Assert.Equal(42, row.Id);
        Assert.Equal("agent-a", row.AgentId);
        Assert.Equal("github", row.Platform);
        Assert.Equal(CommunityAction.NewIssue, row.Action);
        Assert.Equal("https://example.test/issue", row.TargetUrl);
        Assert.Equal("done", row.Status);
        Assert.Equal(created, row.CreatedAt);
        Assert.Equal("appr-1", row.ApprovalId);
        Assert.Equal("ext-9", row.ExternalId);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    [InlineData(10_000, 200)]
    public void Filter_limit_is_clamped_to_one_to_two_hundred(int requested, int expected)
    {
        var filter = new CommunityLogFilter(Limit: requested);

        Assert.Equal(expected, filter.Limit);
    }

    [Fact]
    public void Filter_default_limit_is_fifty_and_filters_default_to_all()
    {
        var filter = new CommunityLogFilter();

        Assert.Equal(50, filter.Limit);
        Assert.Null(filter.AgentId);
        Assert.Null(filter.Platform);
    }

    private static CommunityActionRecord Record(string? body, byte[]? bodySha256 = null) => new(
        Id: 1,
        AgentId: "agent-a",
        SponsorId: "sponsor-1",
        Platform: "github",
        Action: CommunityAction.NewPost,
        TargetUrl: null,
        Body: body,
        BodySha256: bodySha256,
        ApprovalId: null,
        TaintSourcesJson: "[]",
        ExternalId: null,
        Status: "done",
        CreatedAt: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
}
