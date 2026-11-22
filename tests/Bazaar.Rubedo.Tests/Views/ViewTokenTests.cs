using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Tests.Views;

public class ViewTokenTests
{
    private static readonly StaticViewTokenCatalog _catalog = new(new Dictionary<string, TokenKind>
    {
        ["color.hue.3"] = TokenKind.Colour,
        ["motion.duration.short"] = TokenKind.Duration,
        ["motion.interaction.send-button"] = TokenKind.Interaction,
        ["layout.strip.height"] = TokenKind.Number,
    });

    private static string[] Codes(string json) =>
        ViewValidator.Validate(ViewDocumentReader.Read(json), _catalog).Select(d => d.Code).ToArray();

    private static string View(string props) =>
        "{ \"dslVersion\": 1, \"name\": \"bazaar.test.tokens\", \"root\": { \"widget\": \"text\", \"props\": { \"label\": \"x\", " + props + " } } }";

    [Fact]
    public void A_listed_token_of_the_right_kind_is_accepted()
    {
        Assert.Empty(Codes(View("\"color\": { \"token\": \"color.hue.3\" }, \"duration\": { \"token\": \"motion.duration.short\" }, \"motion\": { \"token\": \"motion.interaction.send-button\" }")));
    }

    [Fact]
    public void An_unlisted_token_is_reported()
    {
        Assert.Equal(["view.unknown_token"], Codes(View("\"color\": { \"token\": \"color.hue.99\" }")));
    }

    [Fact]
    public void A_token_of_the_wrong_kind_is_reported()
    {
        Assert.Equal(["view.token_kind_mismatch"], Codes(View("\"color\": { \"token\": \"motion.duration.short\" }")));
        Assert.Equal(["view.token_kind_mismatch"], Codes(View("\"motion\": { \"token\": \"color.hue.3\" }")));
    }

    [Fact]
    public void An_interaction_token_outside_the_motion_prop_is_reported()
    {
        Assert.Equal(["view.token_kind_mismatch"], Codes(View("\"width\": { \"token\": \"motion.interaction.send-button\" }")));
    }

    [Fact]
    public void Raw_colours_and_durations_are_reported()
    {
        Assert.Equal(["view.literal_style_value"], Codes(View("\"color\": \"#ff8800\"")));
        Assert.Equal(["view.literal_style_value"], Codes(View("\"duration\": \"200ms\"")));
        Assert.Equal(["view.literal_style_value"], Codes(View("\"background\": \"rgb(1, 2, 3)\"")));
    }

    [Fact]
    public void Motion_takes_none_or_a_token_but_not_a_name()
    {
        Assert.Empty(Codes(View("\"motion\": \"none\"")));
        Assert.Equal(["view.literal_style_value"], Codes(View("\"motion\": \"send-button\"")));
    }

    [Fact]
    public void Without_a_catalog_names_are_not_checked_but_literals_still_are()
    {
        var document = ViewDocumentReader.Read(View("\"color\": { \"token\": \"anything\" }, \"duration\": \"1s\""));

        Assert.Equal(["view.literal_style_value"], ViewValidator.Validate(document).Select(d => d.Code).ToArray());
    }

    [Fact]
    public void A_token_object_with_a_non_string_name_is_a_read_error()
    {
        Assert.Throws<FormatException>(() => ViewDocumentReader.Read(View("\"color\": { \"token\": 3 }")));
    }
}
