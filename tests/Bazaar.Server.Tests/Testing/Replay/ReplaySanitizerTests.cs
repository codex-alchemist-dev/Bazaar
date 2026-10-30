using Bazaar.Testing.Replay;

namespace Bazaar.Server.Tests.Testing.Replay;

public class ReplaySanitizerTests
{
    [Fact]
    public void Email_address_in_a_string_field_is_rejected()
    {
        var json = Document("""{ "seq": 1, "kind": "gc_message", "text": "write to dev@example.org", "keys": { "speaker": "agent-alpha" } }""");

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "email"));

        Assert.Contains("records[0].text", error.Message);
        Assert.Contains("email", error.Message);
        Assert.DoesNotContain("example.org", error.Message);
    }

    [Theory]
    [InlineData("sk-synthetic")]
    [InlineData("ghp_synthetic")]
    [InlineData("xoxb-synthetic")]
    public void Key_like_prefix_in_a_string_field_is_rejected(string value)
    {
        var json = Document($$"""{ "seq": 1, "kind": "gc_message", "text": "token {{value}}", "keys": { "speaker": "agent-alpha" } }""");

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "key"));

        Assert.Contains("records[0].text", error.Message);
        Assert.Contains("key-like", error.Message);
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public void Key_like_prefix_inside_a_structural_key_is_rejected()
    {
        var json = Document("""{ "seq": 1, "kind": "tool_call", "keys": { "tool": "sk-synthetic" } }""");

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "key-in-keys"));

        Assert.Contains("records[0].keys.tool", error.Message);
    }

    [Fact]
    public void Words_that_only_contain_a_prefix_are_accepted()
    {
        var json = Document("""{ "seq": 1, "kind": "gc_message", "text": "task-list and risk-report and ask-me", "keys": { "speaker": "agent-alpha" } }""");

        var fixture = ReplayFixtureLoader.Parse(json, "benign");

        Assert.Single(fixture.Records);
    }

    [Fact]
    public void Synthetic_repository_fixtures_are_clean()
    {
        Assert.Equal(6, ReplayFixtureFiles.Load("gc-thread-expected.json").Records.Count);
        Assert.Equal(6, ReplayFixtureFiles.Load("gc-thread-actual.json").Records.Count);
    }

    private static string Document(string record) =>
        $$"""{ "format": 1, "name": "inline", "records": [ {{record}} ] }""";
}
