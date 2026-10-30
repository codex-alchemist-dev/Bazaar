using Bazaar.Testing.Replay;

namespace Bazaar.Server.Tests.Testing.Replay;

public class ReplayFixtureLoaderTests
{
    [Fact]
    public void Loads_the_synthetic_fixture_with_its_kinds_and_keys_in_order()
    {
        var fixture = ReplayFixtureFiles.Load("gc-thread-expected.json");

        Assert.Equal(ReplayFixture.CurrentFormat, fixture.Format);
        Assert.Equal(
            [ReplayKind.GcMessage, ReplayKind.Wake, ReplayKind.ModelRequest, ReplayKind.ModelResponse, ReplayKind.ToolCall, ReplayKind.GcMessage],
            fixture.Records.Select(r => r.Kind).ToList());
        Assert.Equal("agent-beta", fixture.Records[1].Keys["agent"]);
        Assert.Equal("Synthetic kickoff message.", fixture.Records[0].Text);
    }

    [Fact]
    public void Unknown_record_kind_is_rejected()
    {
        var json = Document("""{ "seq": 1, "kind": "gc_edit", "keys": { "speaker": "agent-alpha" } }""");

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "kind"));

        Assert.Contains("does not match the fixture schema", error.Message);
    }

    [Fact]
    public void Unknown_top_level_property_is_rejected()
    {
        var json = """{ "format": 1, "name": "x", "owner": "someone", "records": [] }""";

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "unknown"));

        Assert.Contains("does not match the fixture schema", error.Message);
    }

    [Fact]
    public void Sequence_numbers_that_do_not_rise_are_rejected()
    {
        var json = Document(
            """{ "seq": 2, "kind": "wake", "keys": { "agent": "agent-beta" } }""",
            """{ "seq": 2, "kind": "wake", "keys": { "agent": "agent-beta" } }""");

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "seq"));

        Assert.Contains("records[1].seq", error.Message);
    }

    [Fact]
    public void Missing_structural_key_is_rejected()
    {
        var json = Document("""{ "seq": 1, "kind": "tool_call", "keys": { } }""");

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "missing"));

        Assert.Contains("lacks structural key(s): tool", error.Message);
    }

    [Fact]
    public void Non_structural_key_is_rejected()
    {
        var json = Document("""{ "seq": 1, "kind": "wake", "keys": { "agent": "agent-beta", "nickname": "b" } }""");

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "extra-key"));

        Assert.Contains("nickname", error.Message);
    }

    [Fact]
    public void Unsupported_format_is_rejected()
    {
        var json = """{ "format": 2, "name": "x", "records": [] }""";

        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse(json, "format"));

        Assert.Contains("format 2 is not supported", error.Message);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        var error = Assert.Throws<ReplayFixtureException>(() => ReplayFixtureLoader.Parse("{ \"format\": ", "broken"));

        Assert.Contains("not well-formed JSON", error.Message);
    }

    private static string Document(params string[] records) =>
        $$"""{ "format": 1, "name": "inline", "records": [ {{string.Join(", ", records)}} ] }""";
}
