using System.Text.Json;

using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

public sealed class GoldenImageRecipeTests
{
    [Fact]
    public void Valid_recipe_exposes_its_fields()
    {
        var recipe = ImageTestData.LinuxRecipe(version: 4);

        Assert.Equal(GoldenImageOs.Linux, recipe.Os);
        Assert.Equal(4, recipe.Version);
        Assert.Equal("1.4.0", recipe.GuestAgentVersion);
        Assert.Equal(2, recipe.PinnedTools.Count);
        Assert.Equal(new PinnedTool("git", "2.45.2"), recipe.PinnedTools[0]);
    }

    [Fact]
    public void Recipe_keeps_its_own_copy_of_the_tool_list()
    {
        var tools = new List<PinnedTool> { new("git", "2.45.2") };
        var recipe = new GoldenImageRecipe(GoldenImageOs.Windows, 1, "1.0.0", tools);

        tools.Add(new PinnedTool("node", "22.11.0"));

        Assert.Single(recipe.PinnedTools);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Version_must_be_positive(int version)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoldenImageRecipe(GoldenImageOs.Linux, version, "1.0.0", [new PinnedTool("git", "2.45.2")]));

        Assert.Equal("version", ex.ParamName);
    }

    [Fact]
    public void Unknown_operating_system_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new GoldenImageRecipe((GoldenImageOs)99, 1, "1.0.0", [new PinnedTool("git", "2.45.2")]));

        Assert.Equal("os", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Guest_agent_version_is_required(string guestAgentVersion)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new GoldenImageRecipe(GoldenImageOs.Linux, 1, guestAgentVersion, [new PinnedTool("git", "2.45.2")]));

        Assert.Equal("guestAgentVersion", ex.ParamName);
    }

    [Fact]
    public void Recipe_must_pin_at_least_one_tool()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new GoldenImageRecipe(GoldenImageOs.Linux, 1, "1.0.0", []));

        Assert.Equal("pinnedTools", ex.ParamName);
        Assert.Contains("at least one tool", ex.Message);
    }

    [Fact]
    public void Tool_names_must_be_unique_ignoring_case()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new GoldenImageRecipe(GoldenImageOs.Linux, 1, "1.0.0", [new PinnedTool("git", "2.45.2"), new PinnedTool("Git", "2.46.0")]));

        Assert.Equal("pinnedTools", ex.ParamName);
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Null_tool_entry_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GoldenImageRecipe(GoldenImageOs.Linux, 1, "1.0.0", [null!]));
    }

    [Theory]
    [InlineData("", "2.45.2")]
    [InlineData("  ", "2.45.2")]
    [InlineData("git", "")]
    [InlineData("git", "\t")]
    public void Pinned_tool_needs_a_name_and_a_version(string name, string version)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PinnedTool(name, version));
    }

    [Fact]
    public void Load_reads_a_valid_recipe_from_json()
    {
        const string json = """
            {
              "os": "Linux",
              "version": 3,
              "guestAgentVersion": "1.4.0",
              "pinnedTools": [
                { "name": "git", "version": "2.45.2" },
                { "name": "dotnet-sdk", "version": "11.0.100" }
              ]
            }
            """;

        var recipe = GoldenImageRecipeJson.Load(json);

        Assert.Equal(GoldenImageOs.Linux, recipe.Os);
        Assert.Equal(3, recipe.Version);
        Assert.Equal("1.4.0", recipe.GuestAgentVersion);
        Assert.Equal(new PinnedTool("dotnet-sdk", "11.0.100"), recipe.PinnedTools[1]);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var original = new GoldenImageRecipe(
            GoldenImageOs.Windows,
            7,
            "2.0.1",
            [new PinnedTool("powershell", "7.5.1"), new PinnedTool("git", "2.46.0")]);

        var json = GoldenImageRecipeJson.Save(original);
        var loaded = GoldenImageRecipeJson.Load(json);

        Assert.Equal(original.Os, loaded.Os);
        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(original.GuestAgentVersion, loaded.GuestAgentVersion);
        Assert.Equal(original.PinnedTools, loaded.PinnedTools);
    }

    [Fact]
    public void Load_refuses_malformed_json()
    {
        Assert.Throws<JsonException>(() => GoldenImageRecipeJson.Load("{ \"os\": "));
    }

    [Fact]
    public void Load_refuses_unknown_fields()
    {
        const string json = """
            {
              "os": "Linux",
              "version": 3,
              "guestAgentVersion": "1.4.0",
              "pinnedTools": [ { "name": "git", "version": "2.45.2" } ],
              "bogus": true
            }
            """;

        Assert.Throws<JsonException>(() => GoldenImageRecipeJson.Load(json));
    }

    [Fact]
    public void Load_refuses_an_invalid_recipe_value()
    {
        const string json = """
            {
              "os": "Linux",
              "version": 0,
              "guestAgentVersion": "1.4.0",
              "pinnedTools": [ { "name": "git", "version": "2.45.2" } ]
            }
            """;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => GoldenImageRecipeJson.Load(json));

        Assert.Equal("version", ex.ParamName);
    }
}
