using System.Text.Json;
using Bazaar.Client.CompanyMemory.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.CompanyMemory.Tests;

public class CompanyMemoryPaneTests
{
    private const string _decisionA = "decisions/0001-use-polling.md";
    private const string _decisionB = "decisions/0002-use-notify.md";

    private static PaneDescriptor Descriptor(string json) =>
        new(PaneKinds.CompanyMemory, "Title", string.Empty, JsonElement.Parse(json));

    private static CompanyMemoryPaneViewModel Create(string json) => new(Descriptor(json));

    private static string Decision(string path, string title, string status, string? decidedIn) =>
        $$"""{"path":"{{path}}","title":"{{title}}","status":"{{status}}","decidedIn":{{(decidedIn is null ? "null" : $"\"{decidedIn}\"")}}}""";

    private static string Link(string from, string to, string relation, string? status, bool fromMissing, bool toMissing) =>
        $$"""{"fromDoc":"{{from}}","toDoc":"{{to}}","relation":"{{relation}}","status":{{(status is null ? "null" : $"\"{status}\"")}},"fromMissing":{{Json(fromMissing)}},"toMissing":{{Json(toMissing)}}}""";

    private static string Json(bool value) => value ? "true" : "false";

    private static string Model(string decisions, string links) => $$"""{"decisions":[{{decisions}}],"links":[{{links}}]}""";

    [Fact]
    public void Parses_decisions_and_links_into_rows()
    {
        var pane = Create(Model(
            Decision(_decisionA, "Use polling", "superseded", "gc-msg-1") + "," + Decision(_decisionB, "Use NOTIFY", "accepted", "gc-msg-2"),
            Link(_decisionB, _decisionA, "supersedes", "accepted", false, false)));

        Assert.Equal(PaneKinds.CompanyMemory, pane.Kind);
        Assert.Equal("Company memory · Decision chain", pane.Title);
        Assert.Equal(2, pane.Decisions.Count);

        var old = pane.Decisions[0];
        Assert.Equal(_decisionA, old.Path);
        Assert.Equal("superseded", old.Status);
        Assert.Equal("gc-msg-1", old.DecidedIn);
        Assert.Equal("decided in gc-msg-1", old.DecidedText);

        var link = Assert.Single(pane.Links);
        Assert.Equal(_decisionB, link.FromDoc);
        Assert.Equal(_decisionA, link.ToDoc);
        Assert.Equal($"{_decisionB} supersedes {_decisionA}", link.Line);
        Assert.Empty(link.MissingSources);
    }

    [Fact]
    public void Null_decided_in_is_shown_as_no_message_recorded()
    {
        var pane = Create(Model(Decision(_decisionA, "Use polling", "proposed", null), string.Empty));

        var row = Assert.Single(pane.Decisions);
        Assert.Null(row.DecidedIn);
        Assert.Equal("no decision message recorded", row.DecidedText);
    }

    [Fact]
    public void Empty_model_has_no_rows()
    {
        var pane = Create(Model(string.Empty, string.Empty));

        Assert.Empty(pane.Decisions);
        Assert.Empty(pane.Links);
    }

    [Theory]
    [InlineData("""{"links":[]}""")]
    [InlineData("""{"decisions":[]}""")]
    public void Missing_top_level_array_raises(string json) =>
        Assert.Throws<PaneModelException>(() => Create(json));

    [Fact]
    public void Decision_missing_title_raises()
    {
        var json = Model($$"""{"path":"{{_decisionA}}","status":"accepted","decidedIn":null}""", string.Empty);

        Assert.Throws<PaneModelException>(() => Create(json));
    }

    [Fact]
    public void Decision_missing_decided_in_raises()
    {
        var json = Model($$"""{"path":"{{_decisionA}}","title":"T","status":"accepted"}""", string.Empty);

        var error = Assert.Throws<PaneModelException>(() => Create(json));
        Assert.Contains("decidedIn", error.Message);
    }

    [Fact]
    public void Link_missing_to_missing_flag_raises()
    {
        var json = Model(string.Empty, $$"""{"fromDoc":"a","toDoc":"b","relation":"supersedes","status":null,"fromMissing":false}""");

        Assert.Throws<PaneModelException>(() => Create(json));
    }

    [Fact]
    public void Non_object_decision_raises()
    {
        Assert.Throws<PaneModelException>(() => Create(Model("\"not an object\"", string.Empty)));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("Accepted")]
    public void Unknown_decision_status_raises(string status)
    {
        var json = Model(Decision(_decisionA, "T", status, null), string.Empty);

        var error = Assert.Throws<PaneModelException>(() => Create(json));
        Assert.Contains(status, error.Message);
    }

    [Fact]
    public void Unknown_link_status_raises()
    {
        var json = Model(string.Empty, Link("a", "b", "supersedes", "draft", false, false));

        Assert.Throws<PaneModelException>(() => Create(json));
    }

    [Fact]
    public void Link_status_may_be_null_when_the_from_record_is_missing()
    {
        var pane = Create(Model(string.Empty, Link("decisions/9999-gone.md", _decisionA, "supersedes", null, true, false)));

        Assert.Null(Assert.Single(pane.Links).Status);
    }

    [Fact]
    public void Decisions_are_ordered_by_path_regardless_of_input_order()
    {
        var pane = Create(Model(
            Decision("decisions/0003-c.md", "C", "accepted", null) + ","
            + Decision("decisions/0001-a.md", "A", "superseded", null) + ","
            + Decision("decisions/0002-b.md", "B", "accepted", null),
            string.Empty));

        Assert.Equal(
            ["decisions/0001-a.md", "decisions/0002-b.md", "decisions/0003-c.md"],
            pane.Decisions.Select(d => d.Path).ToArray());
    }

    [Fact]
    public void Links_are_ordered_by_from_then_to_then_relation_regardless_of_input_order()
    {
        var links = new[]
        {
            Link("decisions/0003-c.md", "decisions/0001-a.md", "supersedes", "accepted", false, false),
            Link("decisions/0002-b.md", "decisions/0001-a.md", "supersedes", "accepted", false, false),
            Link("decisions/0002-b.md", "decisions/0000-x.md", "supersedes", "accepted", false, false),
            Link("decisions/0002-b.md", "decisions/0000-x.md", "cites", "accepted", false, false),
        };
        var forward = Create(Model(string.Empty, string.Join(",", links)));
        var reversed = Create(Model(string.Empty, string.Join(",", links.Reverse())));

        var expected = new[]
        {
            "decisions/0002-b.md cites decisions/0000-x.md",
            "decisions/0002-b.md supersedes decisions/0000-x.md",
            "decisions/0002-b.md supersedes decisions/0001-a.md",
            "decisions/0003-c.md supersedes decisions/0001-a.md",
        };
        Assert.Equal(expected, forward.Links.Select(l => l.Line).ToArray());
        Assert.Equal(expected, reversed.Links.Select(l => l.Line).ToArray());
    }

    [Fact]
    public void Missing_from_end_is_a_missing_source_row_and_the_edge_stays()
    {
        var pane = Create(Model(
            Decision(_decisionA, "Use polling", "superseded", null),
            Link("decisions/9999-gone.md", _decisionA, "supersedes", null, true, false)));

        var link = Assert.Single(pane.Links);
        Assert.Equal("decisions/9999-gone.md supersedes " + _decisionA, link.Line);
        Assert.Equal(["missing source: decisions/9999-gone.md"], link.MissingSources);
    }

    [Fact]
    public void Missing_to_end_is_a_missing_source_row_and_the_edge_stays()
    {
        var pane = Create(Model(
            Decision(_decisionB, "Use NOTIFY", "accepted", null),
            Link(_decisionB, "decisions/0000-gone.md", "supersedes", "accepted", false, true)));

        var link = Assert.Single(pane.Links);
        Assert.Equal(_decisionB + " supersedes decisions/0000-gone.md", link.Line);
        Assert.Equal(["missing source: decisions/0000-gone.md"], link.MissingSources);
    }

    [Fact]
    public void Both_ends_missing_give_two_missing_source_rows()
    {
        var pane = Create(Model(string.Empty, Link("decisions/a.md", "decisions/b.md", "supersedes", null, true, true)));

        Assert.Equal(
            ["missing source: decisions/a.md", "missing source: decisions/b.md"],
            Assert.Single(pane.Links).MissingSources);
    }

    [Fact]
    public void Superseded_decision_is_marked_and_accepted_one_is_not()
    {
        var pane = Create(Model(
            Decision(_decisionA, "Use polling", "superseded", null) + "," + Decision(_decisionB, "Use NOTIFY", "accepted", null),
            string.Empty));

        var old = pane.Decisions[0];
        var current = pane.Decisions[1];
        Assert.True(old.IsSuperseded);
        Assert.Contains("superseded", old.Line);
        Assert.Contains("replaced by a newer decision", old.Line);
        Assert.False(current.IsSuperseded);
        Assert.DoesNotContain("replaced", current.Line);
    }

    [Fact]
    public void Registry_builds_the_pane_from_the_module()
    {
        var registry = PaneRegistry.Compose(CompanyMemoryPanes.Modules);

        Assert.True(registry.Contains(PaneKinds.CompanyMemory));
        var pane = Assert.IsType<CompanyMemoryPaneViewModel>(registry.Create(Descriptor(Model(string.Empty, string.Empty))));
        Assert.Empty(pane.Decisions);
    }

    [Fact]
    public void Registry_falls_back_to_the_generic_view_on_a_bad_model()
    {
        var registry = PaneRegistry.Compose(CompanyMemoryPanes.Modules);

        var pane = registry.Create(Descriptor("""{"decisions":[{"path":"a","title":"t","status":"draft","decidedIn":null}],"links":[]}"""));

        Assert.IsNotType<CompanyMemoryPaneViewModel>(pane);
    }
}
