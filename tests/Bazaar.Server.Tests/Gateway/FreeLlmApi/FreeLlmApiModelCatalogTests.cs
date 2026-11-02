using Bazaar.Server.Gateway.FreeLlmApi;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

public class FreeLlmApiModelCatalogTests
{
    private static readonly string[] _expectedIds = ["llama-3.3-70b", "gemini-2.5-flash", "mistral-large-2411", "kimi-k2"];

    private static readonly string[] _expectedStatuses = ["ready", "ready", "exhausted", "needsKey"];

    [Fact]
    public void A_full_catalogue_parses_every_row_with_its_status()
    {
        var parsed = FreeLlmApiModelCatalog.TryParse(FreeLlmApiTestRig.Fixture("models.json"), out var models);

        Assert.True(parsed);
        Assert.Equal(4, models.Count);
        Assert.Equal(_expectedIds, models.Select(m => m.Id).ToArray());
        Assert.Equal(_expectedStatuses, models.Select(m => m.ExecutionStatus).ToArray());
    }

    [Fact]
    public void A_list_with_no_ready_model_still_parses()
    {
        var parsed = FreeLlmApiModelCatalog.TryParse(FreeLlmApiTestRig.Fixture("models_none_ready.json"), out var models);

        Assert.True(parsed);
        var model = Assert.Single(models);
        Assert.Equal("mistral-large-2411", model.Id);
        Assert.Equal("exhausted", model.ExecutionStatus);
    }

    [Fact]
    public void Text_that_is_not_json_is_rejected_with_an_empty_list()
    {
        var parsed = FreeLlmApiModelCatalog.TryParse("not json", out var models);

        Assert.False(parsed);
        Assert.Empty(models);
    }

    [Fact]
    public void A_body_without_a_data_array_is_rejected()
    {
        var parsed = FreeLlmApiModelCatalog.TryParse("""{ "object": "list" }""", out var models);

        Assert.False(parsed);
        Assert.Empty(models);
    }

    [Fact]
    public void A_row_without_an_id_is_rejected()
    {
        var parsed = FreeLlmApiModelCatalog.TryParse("""{ "data": [ { "execution_status": "ready" } ] }""", out var models);

        Assert.False(parsed);
        Assert.Empty(models);
    }

    [Fact]
    public void A_row_without_an_execution_status_has_a_null_status()
    {
        var parsed = FreeLlmApiModelCatalog.TryParse("""{ "data": [ { "id": "model-a", "owner": "x" } ] }""", out var models);

        Assert.True(parsed);
        var model = Assert.Single(models);
        Assert.Equal("model-a", model.Id);
        Assert.Null(model.ExecutionStatus);
    }
}
