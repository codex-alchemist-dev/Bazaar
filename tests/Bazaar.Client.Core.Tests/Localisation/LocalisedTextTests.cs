using Bazaar.Client.Core.Localisation;

namespace Bazaar.Client.Core.Tests.Localisation;

public class LocalisedTextTests
{
    private static MessageCatalog Catalog(string locale, params (string Key, string Value)[] entries) =>
        MessageCatalog.Create(locale, entries.ToDictionary(e => e.Key, e => e.Value));

    [Fact]
    public void Resolves_from_the_first_locale_that_has_the_key()
    {
        var text = new LocalisedText([
            Catalog("pt-BR", ("tour.chat.title", "Conversas")),
            Catalog("en", ("tour.chat.title", "Chats"), ("tour.panes.title", "Panes")),
        ]);

        Assert.Equal("Conversas", text.Resolve("tour.chat.title"));
        Assert.Equal("Panes", text.Resolve("tour.panes.title"));
    }

    [Fact]
    public void Reports_fallback_only_when_a_later_locale_supplied_the_text()
    {
        var text = new LocalisedText([
            Catalog("pt-BR", ("a", "x")),
            Catalog("en", ("a", "y"), ("b", "z")),
        ]);

        Assert.False(text.UsesFallback("a"));
        Assert.True(text.UsesFallback("b"));
    }

    [Fact]
    public void A_missing_key_shows_its_id_so_the_gap_is_visible()
    {
        var text = new LocalisedText([Catalog("en", ("a", "x"))]);

        Assert.Equal("<missing.key>", text.Resolve("missing.key"));
    }

    [Fact]
    public void Fills_named_placeholders_and_leaves_unknown_ones_as_written()
    {
        var text = new LocalisedText([Catalog("en", ("hello", "Hi {name}, you have {count} unread {nope}"))]);

        string result = text.Resolve("hello", new Dictionary<string, string> { ["name"] = "Dom", ["count"] = "3" });

        Assert.Equal("Hi Dom, you have 3 unread {nope}", result);
    }

    [Fact]
    public void Leaves_a_brace_without_a_closing_pair_alone()
    {
        var text = new LocalisedText([Catalog("en", ("a", "open { only"))]);

        Assert.Equal("open { only", text.Resolve("a", new Dictionary<string, string> { ["x"] = "y" }));
    }

    [Fact]
    public void Empty_chain_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LocalisedText([]));
    }

    [Fact]
    public void Catalog_rejects_blank_locale_key_or_value()
    {
        Assert.Throws<ArgumentException>(() => MessageCatalog.Create(" ", new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => Catalog("en", ("", "x")));
        Assert.Throws<ArgumentException>(() => Catalog("en", ("a", " ")));
    }
}
