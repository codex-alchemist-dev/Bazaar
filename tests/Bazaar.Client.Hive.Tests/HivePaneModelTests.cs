using Bazaar.Client.Core.Panes;
using Bazaar.Client.Hive.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Hive.Tests;

public class HivePaneModelTests
{
    [Fact]
    public void Parses_review_window_grants_and_copies()
    {
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model));

        Assert.Equal(PaneKinds.Hive, pane.Kind);
        Assert.Equal("Hive Mind", pane.Title);
        Assert.Equal(30, pane.ReviewDays);
        Assert.Equal(3, pane.Copies.Count);
        Assert.Equal(HiveCopyState.InReview, pane.Copies[1].State);
        Assert.Equal("2026-12-18", pane.Copies[1].ReviewUntil);
    }

    [Fact]
    public void Copy_labels_show_origin_project_kind_trust_and_state()
    {
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model));

        Assert.Equal("hive/procedure/retry.md · origin alpha · procedural · trust T2 · active", pane.Copies[0].Line);
        Assert.Equal("hive/failure/timeout.md · origin alpha · failure · trust T2 · in review until 2026-12-18", pane.Copies[1].Line);
    }

    [Fact]
    public void Deprecated_copies_stay_in_the_list_and_carry_a_marker()
    {
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model));

        var deprecated = pane.Copies[2];
        Assert.True(deprecated.IsDeprecated);
        Assert.StartsWith("[deprecated] ", deprecated.Line, StringComparison.Ordinal);
        Assert.Contains(pane.Copies, copy => copy.Path == "hive/procedure/old.md");
    }

    [Theory]
    [InlineData("""{"reviewDays":30,"grants":[],"copies":[{"path":"p","originProject":"a","originKind":"k","trust":"T2","state":"archived"}]}""")]
    [InlineData("""{"reviewDays":30,"grants":[],"copies":[{"path":"p","originProject":"a","originKind":"k","trust":"T2","state":"ACTIVE"}]}""")]
    public void Unknown_copy_state_raises_a_model_exception(string json)
    {
        Assert.Throws<PaneModelException>(() => new HivePaneViewModel(HiveTestData.Descriptor(json)));
    }

    [Fact]
    public void In_review_copy_without_review_until_raises_a_model_exception()
    {
        const string json = """{"reviewDays":30,"grants":[],"copies":[{"path":"p","originProject":"a","originKind":"k","trust":"T2","state":"inReview"}]}""";

        Assert.Throws<PaneModelException>(() => new HivePaneViewModel(HiveTestData.Descriptor(json)));
    }

    [Theory]
    [InlineData("""{"grants":[],"copies":[]}""")]
    [InlineData("""{"reviewDays":"30","grants":[],"copies":[]}""")]
    [InlineData("""{"reviewDays":30,"copies":[]}""")]
    [InlineData("""{"reviewDays":30,"grants":[{"fromProject":"a","toProject":"b","kind":"k"}],"copies":[]}""")]
    [InlineData("""{"reviewDays":30,"grants":[],"copies":[{"path":"p","originProject":"a","originKind":"k","state":"active"}]}""")]
    [InlineData("""{"reviewDays":30,"grants":["oops"],"copies":[]}""")]
    public void Missing_or_mistyped_fields_raise_a_model_exception(string json)
    {
        Assert.Throws<PaneModelException>(() => new HivePaneViewModel(HiveTestData.Descriptor(json)));
    }

    [Fact]
    public void Registry_falls_back_to_the_generic_view_on_a_bad_state()
    {
        var registry = HiveTestData.CreateRegistry();
        var bad = registry.Create(HiveTestData.Descriptor("""{"reviewDays":30,"grants":[],"copies":[{"path":"p","originProject":"a","originKind":"k","trust":"T2","state":"maybe"}]}"""));

        Assert.IsType<GenericPaneViewModel>(bad);
        Assert.IsType<HivePaneViewModel>(registry.Create(HiveTestData.Descriptor(HiveTestData.Model)));
    }
}
