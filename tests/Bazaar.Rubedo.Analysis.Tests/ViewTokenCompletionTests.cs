using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Analysis.Tests;

public class ViewTokenCompletionTests
{
    private static readonly StaticViewTokenCatalog _catalog = new(new Dictionary<string, TokenKind>
    {
        ["color.hue.3"] = TokenKind.Colour,
        ["color.text.primary"] = TokenKind.Colour,
        ["motion.duration.short"] = TokenKind.Duration,
        ["motion.interaction.send-button"] = TokenKind.Interaction,
        ["layout.strip.height"] = TokenKind.Number,
    });

    private static string[] Labels(string withCursor)
    {
        int offset = withCursor.IndexOf('|');
        string json = withCursor.Remove(offset, 1);
        return RubedoAnalyzer.CompleteView(json, offset, _catalog).Items.Select(i => i.Label).ToArray();
    }

    private static string View(string prop) =>
        "{ \"dslVersion\": 1, \"name\": \"bazaar.test.view\", \"root\": { \"widget\": \"text\", \"props\": { " + prop + " } } }";

    [Fact]
    public void A_colour_prop_offers_colour_tokens_only()
    {
        Assert.Equal(["color.hue.3", "color.text.primary"], Labels(View("\"color\": { \"token\": \"|\" }")));
    }

    [Fact]
    public void The_typed_prefix_narrows_the_list()
    {
        Assert.Equal(["color.hue.3"], Labels(View("\"color\": { \"token\": \"color.h|\" }")));
    }

    [Fact]
    public void The_motion_prop_offers_interaction_presets_only()
    {
        Assert.Equal(["motion.interaction.send-button"], Labels(View("\"motion\": { \"token\": \"|\" }")));
    }

    [Fact]
    public void Any_other_prop_offers_every_value_token_but_no_preset()
    {
        var labels = Labels(View("\"width\": { \"token\": \"|\" }"));

        Assert.Contains("layout.strip.height", labels);
        Assert.DoesNotContain("motion.interaction.send-button", labels);
    }

    [Fact]
    public void Without_a_catalog_there_are_no_token_completions()
    {
        string json = View("\"color\": { \"token\": \"\" }");
        int offset = json.IndexOf("\"\"", StringComparison.Ordinal) + 1;

        Assert.Empty(RubedoAnalyzer.CompleteView(json, offset).Items);
    }

    [Fact]
    public void Diagnostics_check_token_names_when_a_catalog_is_given()
    {
        string json = View("\"color\": { \"token\": \"color.hue.99\" }");

        Assert.Contains(RubedoAnalyzer.DiagnoseView(json, _catalog), d => d.Code == "view.unknown_token");
        Assert.DoesNotContain(RubedoAnalyzer.DiagnoseView(json), d => d.Code == "view.unknown_token");
    }
}
