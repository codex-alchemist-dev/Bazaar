using System.Text.Json.Nodes;
using Bazaar.RyuseiFuda.Roadmap;

namespace Bazaar.Server.Tests.Roadmap;

/// <summary>The objective roadmap front-matter contract and dependency cycle check (doc 38, Layer 3). Pure: no database.</summary>
public sealed class RoadmapFrontMatterTests
{
    private static JsonObject Objective(params (string Key, JsonNode? Value)[] fields)
    {
        var node = new JsonObject { ["kind"] = "objective", ["phase"] = "phase-one", ["status"] = "planned" };
        foreach (var (key, value) in fields)
            node[key] = value;
        return node;
    }

    private static JsonObject Phase(params (string Key, JsonNode? Value)[] fields)
    {
        var node = new JsonObject { ["kind"] = "phase", ["status"] = "active" };
        foreach (var (key, value) in fields)
            node[key] = value;
        return node;
    }

    private static JsonArray Deps(params string[] items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(item);
        return array;
    }

    private static string[] FieldsOf(RoadmapParseResult result) => [.. result.Errors.Select(e => e.Field)];

    [Fact]
    public void A_valid_objective_parses_to_an_objective_record()
    {
        var result = RoadmapFrontMatter.Parse(Objective(
            ("owner", "codex-alchemist"),
            ("status", "active"),
            ("depends_on", Deps("[[ship-planner]]", "[[gate-views]]"))));

        Assert.True(result.IsValid);
        var objective = Assert.IsType<RoadmapObjective>(result.Document);
        Assert.Equal("phase-one", objective.Phase);
        Assert.Equal(RoadmapStatus.Active, objective.Status);
        Assert.Equal("codex-alchemist", objective.Owner);
        Assert.Equal(["ship-planner", "gate-views"], objective.DependsOn);
    }

    [Fact]
    public void A_valid_phase_parses_to_a_phase_record()
    {
        var result = RoadmapFrontMatter.Parse(Phase());

        Assert.True(result.IsValid);
        Assert.Equal(new RoadmapPhase(RoadmapStatus.Active), result.Document);
    }

    [Fact]
    public void Optional_fields_may_be_absent_on_an_objective()
    {
        var result = RoadmapFrontMatter.Parse(Objective());

        var objective = Assert.IsType<RoadmapObjective>(result.Document);
        Assert.Null(objective.Owner);
        Assert.Empty(objective.DependsOn);
    }

    [Theory]
    [InlineData("planned", RoadmapStatus.Planned)]
    [InlineData("active", RoadmapStatus.Active)]
    [InlineData("done", RoadmapStatus.Done)]
    [InlineData("dropped", RoadmapStatus.Dropped)]
    public void Each_of_the_four_statuses_is_accepted(string text, RoadmapStatus expected)
    {
        var result = RoadmapFrontMatter.Parse(Objective(("status", text)));

        Assert.Equal(expected, Assert.IsType<RoadmapObjective>(result.Document).Status);
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("complete")]
    [InlineData("")]
    public void An_unknown_status_is_refused(string text)
    {
        var result = RoadmapFrontMatter.Parse(Objective(("status", text)));

        Assert.False(result.IsValid);
        Assert.Contains("status", FieldsOf(result));
    }

    [Fact]
    public void A_missing_kind_is_refused()
    {
        var node = new JsonObject { ["status"] = "planned", ["phase"] = "phase-one" };

        var result = RoadmapFrontMatter.Parse(node);

        Assert.False(result.IsValid);
        Assert.Equal(["kind"], FieldsOf(result));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("Objective")]
    public void A_kind_other_than_objective_or_phase_is_refused(string kind)
    {
        var node = Objective(("kind", kind));

        var result = RoadmapFrontMatter.Parse(node);

        Assert.False(result.IsValid);
        Assert.Contains("kind", FieldsOf(result));
    }

    [Fact]
    public void An_objective_without_phase_is_refused()
    {
        var node = new JsonObject { ["kind"] = "objective", ["status"] = "planned" };

        var result = RoadmapFrontMatter.Parse(node);

        Assert.False(result.IsValid);
        Assert.Equal(["phase"], FieldsOf(result));
    }

    [Theory]
    [InlineData("[[phase-one]]")]
    [InlineData("Phase One")]
    [InlineData("phase_one")]
    public void A_phase_that_is_not_a_plain_slug_is_refused(string phase)
    {
        var result = RoadmapFrontMatter.Parse(Objective(("phase", phase)));

        Assert.False(result.IsValid);
        Assert.Contains("phase", FieldsOf(result));
    }

    [Fact]
    public void A_phase_document_cannot_carry_phase_or_depends_on()
    {
        var result = RoadmapFrontMatter.Parse(Phase(("phase", "phase-one"), ("depends_on", Deps("[[x]]"))));

        Assert.False(result.IsValid);
        Assert.Equal(["phase", "depends_on"], FieldsOf(result));
    }

    [Theory]
    [InlineData("ship-planner")]
    [InlineData("[[Ship-Planner]]")]
    [InlineData("[[ship_planner]]")]
    [InlineData("[[ship planner]]")]
    [InlineData("[[]]")]
    [InlineData("[[ship-planner|alias]]")]
    [InlineData("[ship-planner]")]
    [InlineData("[[ship-planner]] ")]
    public void A_depends_on_entry_must_be_a_wiki_link_to_a_slug(string entry)
    {
        var result = RoadmapFrontMatter.Parse(Objective(("depends_on", Deps(entry))));

        Assert.False(result.IsValid);
        Assert.Equal(["depends_on"], FieldsOf(result));
    }

    [Fact]
    public void Lowercase_digit_and_hyphen_slugs_are_accepted_in_depends_on()
    {
        var result = RoadmapFrontMatter.Parse(Objective(("depends_on", Deps("[[phase-2-gate]]", "[[0001]]"))));

        Assert.Equal(["phase-2-gate", "0001"], Assert.IsType<RoadmapObjective>(result.Document).DependsOn);
    }

    [Fact]
    public void A_depends_on_that_is_not_a_list_is_refused()
    {
        var result = RoadmapFrontMatter.Parse(Objective(("depends_on", "[[ship-planner]]")));

        Assert.False(result.IsValid);
        Assert.Equal(["depends_on"], FieldsOf(result));
    }

    [Fact]
    public void A_non_string_owner_or_blank_owner_is_refused()
    {
        var numeric = RoadmapFrontMatter.Parse(Objective(("owner", JsonValue.Create(7))));
        var blank = RoadmapFrontMatter.Parse(Objective(("owner", "  ")));

        Assert.Contains("owner", FieldsOf(numeric));
        Assert.Contains("owner", FieldsOf(blank));
    }

    [Fact]
    public void Every_violation_is_reported_together()
    {
        var node = new JsonObject { ["kind"] = "objective", ["status"] = "nope", ["depends_on"] = Deps("bad") };

        var result = RoadmapFrontMatter.Parse(node);

        Assert.False(result.IsValid);
        Assert.Null(result.Document);
        Assert.Equal(["status", "phase", "depends_on"], FieldsOf(result));
    }

    [Fact]
    public void A_null_status_counts_as_missing()
    {
        var node = Objective(("status", null));

        var result = RoadmapFrontMatter.Parse(node);

        Assert.False(result.IsValid);
        Assert.Equal(["status"], FieldsOf(result));
    }

    [Fact]
    public void FindDependencyCycle_returns_empty_for_an_acyclic_graph()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = ["b", "c"],
            ["b"] = ["c"],
            ["c"] = [],
        };

        Assert.Empty(RoadmapFrontMatter.FindDependencyCycle(graph));
    }

    [Fact]
    public void FindDependencyCycle_returns_the_members_of_a_three_cycle_in_order()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = ["b"],
            ["b"] = ["c"],
            ["c"] = ["a"],
            ["d"] = ["a"],
        };

        Assert.Equal(["a", "b", "c"], RoadmapFrontMatter.FindDependencyCycle(graph));
    }

    [Fact]
    public void FindDependencyCycle_finds_a_cycle_that_is_not_reachable_from_the_first_key()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = [],
            ["b"] = ["c"],
            ["c"] = ["b"],
        };

        Assert.Equal(["b", "c"], RoadmapFrontMatter.FindDependencyCycle(graph));
    }

    [Fact]
    public void A_self_dependency_is_a_cycle_of_one()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["a"] };

        Assert.Equal(["a"], RoadmapFrontMatter.FindDependencyCycle(graph));
    }

    [Fact]
    public void Dependencies_on_unknown_objectives_add_no_edge()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = ["missing"],
            ["b"] = ["missing", "a"],
        };

        Assert.Empty(RoadmapFrontMatter.FindDependencyCycle(graph));
    }

    [Fact]
    public void Parse_and_cycle_check_together_on_a_small_roadmap()
    {
        var first = Assert.IsType<RoadmapObjective>(RoadmapFrontMatter.Parse(Objective(("depends_on", Deps("[[second]]")))).Document);
        var second = Assert.IsType<RoadmapObjective>(RoadmapFrontMatter.Parse(Objective(("depends_on", Deps("[[first]]")))).Document);
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["first"] = first.DependsOn.ToArray(),
            ["second"] = second.DependsOn.ToArray(),
        };

        Assert.Equal(["first", "second"], RoadmapFrontMatter.FindDependencyCycle(graph));
    }
}
