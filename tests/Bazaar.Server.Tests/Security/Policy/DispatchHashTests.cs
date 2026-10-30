using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Policy;

public class DispatchHashTests
{
    private static readonly Guid _source = Guid.Parse("0196a6f0-0000-7000-8000-000000000401");

    private static readonly TaintSource _hardSource = new(_source, ContentTrust.External, TaintState.Hard);

    private static byte[] Hash(string tool = "repo.push", string? target = "codex-alchemist-dev/Bazaar", string args = """{"branch":"agent/x","force":false}""", IEnumerable<TaintSource>? sources = null) =>
        DispatchHash.Compute(tool, target, PolicyFixtures.Args(args), sources ?? []);

    [Fact]
    public void Property_order_and_whitespace_do_not_change_the_hash()
    {
        var compact = Hash(args: """{"branch":"agent/x","force":false}""");
        var reordered = Hash(args: """{ "force" : false,   "branch": "agent/x" }""");

        Assert.Equal(compact, reordered);
    }

    [Fact]
    public void Nested_property_order_does_not_change_the_hash()
    {
        var first = Hash(args: """{"a":{"x":1,"y":[{"p":"q","r":2}]}}""");
        var second = Hash(args: """{"a":{"y":[{"r":2,"p":"q"}],"x":1}}""");

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_changed_argument_value_changes_the_hash()
    {
        Assert.NotEqual(
            Hash(args: """{"branch":"agent/x","force":false}"""),
            Hash(args: """{"branch":"agent/x","force":true}"""));
    }

    [Fact]
    public void A_changed_target_changes_the_hash()
    {
        Assert.NotEqual(Hash(target: "codex-alchemist-dev/Bazaar"), Hash(target: "codex-alchemist-dev/Other"));
    }

    [Fact]
    public void A_null_target_differs_from_an_empty_one()
    {
        Assert.NotEqual(Hash(target: null), Hash(target: string.Empty));
    }

    [Fact]
    public void A_changed_tool_name_changes_the_hash()
    {
        Assert.NotEqual(Hash(tool: "repo.push"), Hash(tool: "repo.force"));
    }

    [Fact]
    public void A_changed_taint_source_list_changes_the_hash()
    {
        Assert.NotEqual(Hash(sources: []), Hash(sources: [_hardSource]));
    }

    [Fact]
    public void A_changed_taint_source_id_changes_the_hash()
    {
        var other = _hardSource with { SourceId = Guid.Parse("0196a6f0-0000-7000-8000-000000000402") };

        Assert.NotEqual(Hash(sources: [_hardSource]), Hash(sources: [other]));
    }

    [Fact]
    public void Hash_length_is_the_documented_length()
    {
        var hash = Hash();

        Assert.Equal(DispatchHash.Length, hash.Length);
        Assert.Equal(32, DispatchHash.Length);
    }

    [Fact]
    public void Repeated_property_at_the_top_level_throws()
    {
        Assert.Throws<ArgumentException>(() => Hash(args: """{"path":"a","path":"b"}"""));
    }

    [Fact]
    public void Repeated_property_in_a_nested_object_throws()
    {
        Assert.Throws<ArgumentException>(() => Hash(args: """{"outer":{"k":1,"k":2}}"""));
    }

    [Fact]
    public void Repeated_property_inside_an_array_element_throws()
    {
        Assert.Throws<ArgumentException>(() => Hash(args: """{"items":[{"k":1,"k":2}]}"""));
    }

    [Fact]
    public void Numbers_keep_their_source_text_so_1_point_0_and_1_hash_differently()
    {
        // Intended and conservative: the canonical form does not normalise numbers.
        Assert.NotEqual(Hash(args: """{"n":1.0}"""), Hash(args: """{"n":1}"""));
    }
}
