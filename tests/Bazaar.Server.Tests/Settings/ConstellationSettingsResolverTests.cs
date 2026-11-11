using System.Text.Json;
using Bazaar.Server.Constellation;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Settings;

/// <summary>Maps stored constellation.orbit_radius and constellation.git_layout values to layout choices. Pure: no database.</summary>
public sealed class ConstellationSettingsResolverTests
{
    private static ConstellationLayoutChoices Resolve(params (string Key, string Json)[] stored)
    {
        var documents = new List<JsonDocument>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            foreach (var (key, json) in stored)
            {
                var doc = JsonDocument.Parse(json);
                documents.Add(doc);
                values[key] = doc.RootElement;
            }

            return ConstellationSettingsResolver.Resolve(values);
        }
        finally
        {
            documents.ForEach(d => d.Dispose());
        }
    }

    [Fact]
    public void Unset_keys_take_the_shipped_defaults()
    {
        var choices = Resolve();

        Assert.Equal(ConstellationOrbitRadius.Age, choices.OrbitRadius);
        Assert.Equal(ConstellationGitLayout.Deterministic, choices.GitLayout);
    }

    [Fact]
    public void Unrelated_stored_keys_do_not_change_the_defaults()
    {
        var choices = Resolve(("constellation.node_cap", "5000"), ("constellation.ring_depth", "2"));

        Assert.Equal(ConstellationOrbitRadius.Age, choices.OrbitRadius);
        Assert.Equal(ConstellationGitLayout.Deterministic, choices.GitLayout);
    }

    [Fact]
    public void Configured_values_are_used()
    {
        var choices = Resolve(("constellation.orbit_radius", "\"equal\""), ("constellation.git_layout", "\"force\""));

        Assert.Equal(ConstellationOrbitRadius.Equal, choices.OrbitRadius);
        Assert.Equal(ConstellationGitLayout.Force, choices.GitLayout);
    }

    [Fact]
    public void One_configured_key_leaves_the_other_at_its_default()
    {
        var choices = Resolve(("constellation.git_layout", "\"force\""));

        Assert.Equal(ConstellationOrbitRadius.Age, choices.OrbitRadius);
        Assert.Equal(ConstellationGitLayout.Force, choices.GitLayout);
    }

    [Fact]
    public void Unknown_git_layout_value_is_rejected_with_the_setting_read_error()
    {
        var error = Assert.Throws<InvalidSettingValueException>(() => Resolve(("constellation.git_layout", "\"auto\"")));

        Assert.Equal("constellation.git_layout", error.Key);
        Assert.Contains("constellation.git_layout", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("[\"force\"]")]
    public void Git_layout_of_the_wrong_json_type_is_rejected(string json)
    {
        var error = Assert.Throws<InvalidSettingValueException>(() => Resolve(("constellation.git_layout", json)));

        Assert.Equal("constellation.git_layout", error.Key);
    }

    [Fact]
    public void Unknown_orbit_radius_value_is_rejected_with_the_setting_read_error()
    {
        var error = Assert.Throws<InvalidSettingValueException>(() => Resolve(("constellation.orbit_radius", "\"size\"")));

        Assert.Equal("constellation.orbit_radius", error.Key);
    }

    [Fact]
    public void Null_dictionary_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ConstellationSettingsResolver.Resolve(null!));
    }
}
