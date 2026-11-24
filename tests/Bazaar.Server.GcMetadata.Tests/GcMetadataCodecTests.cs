namespace Bazaar.Server.GcMetadata.Tests;

public class GcMetadataCodecTests
{
    private static readonly GcMetadataCodec _codec = new();

    [Fact]
    public void Read_returns_typed_keys_and_leaves_other_keys()
    {
        var view = _codec.Read(new Dictionary<string, string> { ["project"] = "bazaar", ["kind"] = "design", ["status"] = "active", ["import_job"] = "j1" });

        Assert.Equal("bazaar", view.Project);
        Assert.Equal("design", view.Kind);
        Assert.Equal("active", view.Status);
        Assert.Equal("j1", view.Other["import_job"]);
        Assert.Empty(view.Issues);
    }

    [Fact]
    public void Read_reports_an_invalid_status_and_reads_it_as_null()
    {
        var view = _codec.Read(new Dictionary<string, string> { ["status"] = "maybe" });

        Assert.Null(view.Status);
        Assert.Single(view.Issues, i => i.Key == GcMetadataKeys.Status);
    }

    [Fact]
    public void Read_of_null_is_empty()
    {
        var view = _codec.Read(null);

        Assert.Null(view.Project);
        Assert.Empty(view.Other);
        Assert.Empty(view.Issues);
    }

    [Fact]
    public void Apply_sets_changes_and_keeps_untouched_keys()
    {
        var current = new Dictionary<string, string> { ["project"] = "bazaar", ["import_job"] = "j1" };

        var result = _codec.Apply(current, new GcMetadataPatch(Status: "blocked", Kind: "pr"));

        Assert.True(result.Succeeded);
        Assert.Equal("bazaar", result.Metadata!["project"]);
        Assert.Equal("blocked", result.Metadata["status"]);
        Assert.Equal("pr", result.Metadata["kind"]);
        Assert.Equal("j1", result.Metadata["import_job"]);
        Assert.False(current.ContainsKey("status"));
    }

    [Fact]
    public void Apply_with_an_empty_value_removes_the_key()
    {
        var result = _codec.Apply(new Dictionary<string, string> { ["status"] = "open" }, new GcMetadataPatch(Status: ""));

        Assert.True(result.Succeeded);
        Assert.False(result.Metadata!.ContainsKey("status"));
    }

    [Theory]
    [InlineData("Bazaar")]
    [InlineData("has space")]
    [InlineData("-lead")]
    public void Apply_refuses_a_bad_project_and_changes_nothing(string project)
    {
        var result = _codec.Apply(new Dictionary<string, string>(), new GcMetadataPatch(Project: project, Status: "open"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Metadata);
        Assert.Single(result.Issues);
    }

    [Fact]
    public void Apply_refuses_a_status_outside_the_operator_list()
    {
        var codec = new GcMetadataCodec(new GcMetadataSchema(["draft", "shipped"]));

        Assert.False(codec.Apply(null, new GcMetadataPatch(Status: "open")).Succeeded);
        Assert.True(codec.Apply(null, new GcMetadataPatch(Status: "shipped")).Succeeded);
    }

    [Fact]
    public void Schema_with_no_statuses_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new GcMetadataCodec(new GcMetadataSchema([])));
    }
}
