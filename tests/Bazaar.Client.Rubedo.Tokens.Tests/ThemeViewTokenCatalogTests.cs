using Bazaar.Client.Rubedo.Tokens;
using Bazaar.Client.Theme;
using Bazaar.Rubedo.Views;
using RubedoKind = Bazaar.Rubedo.Views.TokenKind;

namespace Bazaar.Client.Rubedo.Tokens.Tests;

public class ThemeViewTokenCatalogTests
{
    [Fact]
    public void Every_required_theme_token_is_listed_with_the_matching_kind()
    {
        var catalog = new ThemeViewTokenCatalog();

        foreach (var spec in ThemeTokens.Required)
        {
            Assert.True(catalog.Contains(spec.Name), spec.Name);
            Assert.Equal(spec.Kind.ToString(), catalog.KindOf(spec.Name)!.Value.ToString());
        }
    }

    [Fact]
    public void Extra_tokens_are_added_and_win_over_a_theme_token_of_the_same_name()
    {
        var catalog = new ThemeViewTokenCatalog(new Dictionary<string, RubedoKind>
        {
            ["layout.strip.height"] = RubedoKind.Number,
            ["motion.interaction.send-button"] = RubedoKind.Interaction,
        });

        Assert.Equal(RubedoKind.Number, catalog.KindOf("layout.strip.height"));
        Assert.Equal(RubedoKind.Interaction, catalog.KindOf("motion.interaction.send-button"));
        Assert.Null(catalog.KindOf("color.hue.99"));
        Assert.False(catalog.Contains("color.hue.99"));
    }

    [Fact]
    public void Names_are_sorted_so_completion_and_docs_are_stable()
    {
        var names = new ThemeViewTokenCatalog().Names.ToArray();

        Assert.Equal(names.Order(StringComparer.Ordinal).ToArray(), names);
    }

    [Fact]
    public void A_view_naming_a_theme_token_validates_against_the_catalog()
    {
        var json = "{ \"dslVersion\": 1, \"name\": \"bazaar.test.tokens\", \"root\": { \"widget\": \"text\", \"props\": { \"label\": \"x\", \"color\": { \"token\": \"color.text.primary\" } } } }";

        Assert.Empty(ViewValidator.Validate(ViewDocumentReader.Read(json), new ThemeViewTokenCatalog()));
    }
}
