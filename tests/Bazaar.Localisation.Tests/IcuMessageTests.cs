using Bazaar.Localisation;

namespace Bazaar.Localisation.Tests;

public class IcuMessageTests
{
    private const string _files = "{n, plural, =0 {No files} one {# file} other {# files}}";

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    [Theory]
    [InlineData(0, "No files")]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    [InlineData(1000, "1,000 files")]
    public void English_plural_picks_exact_then_category(int n, string expected) =>
        Assert.Equal(expected, IcuMessage.Format(_files, "en", Args(("n", n))));

    [Fact]
    public void Plain_arguments_are_filled()
    {
        Assert.Equal("Hello Ada, you have 3 mentions.", IcuMessage.Format("Hello {name}, you have {count} mentions.", "en", Args(("name", "Ada"), ("count", 3))));
    }

    [Fact]
    public void Select_chooses_by_key_and_falls_back_to_other()
    {
        const string t = "{who, select, female {She replied} male {He replied} other {They replied}}";

        Assert.Equal("She replied", IcuMessage.Format(t, "en", Args(("who", "female"))));
        Assert.Equal("They replied", IcuMessage.Format(t, "en", Args(("who", "nonbinary"))));
    }

    [Fact]
    public void Plural_branches_can_hold_nested_arguments()
    {
        const string t = "{n, plural, one {{name} has # unread message} other {{name} has # unread messages}}";

        Assert.Equal("Ada has 1 unread message", IcuMessage.Format(t, "en", Args(("n", 1), ("name", "Ada"))));
        Assert.Equal("Ada has 5 unread messages", IcuMessage.Format(t, "en", Args(("n", 5), ("name", "Ada"))));
    }

    [Fact]
    public void Offset_is_subtracted_before_the_category_is_chosen()
    {
        const string t = "{n, plural, offset:1 =0 {Nobody} =1 {Just {first}} one {{first} and # other} other {{first} and # others}}";

        Assert.Equal("Just Ada", IcuMessage.Format(t, "en", Args(("n", 1), ("first", "Ada"))));
        Assert.Equal("Ada and 1 other", IcuMessage.Format(t, "en", Args(("n", 2), ("first", "Ada"))));
        Assert.Equal("Ada and 3 others", IcuMessage.Format(t, "en", Args(("n", 4), ("first", "Ada"))));
    }

    [Theory]
    [InlineData("ru", 1, PluralCategory.One)]
    [InlineData("ru", 21, PluralCategory.One)]
    [InlineData("ru", 3, PluralCategory.Few)]
    [InlineData("ru", 12, PluralCategory.Many)]
    [InlineData("ru", 11, PluralCategory.Many)]
    [InlineData("pl", 22, PluralCategory.Few)]
    [InlineData("pl", 5, PluralCategory.Many)]
    [InlineData("cs", 3, PluralCategory.Few)]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("fr", 2, PluralCategory.Other)]
    [InlineData("pt-BR", 0, PluralCategory.One)]
    [InlineData("ja", 1, PluralCategory.Other)]
    [InlineData("ar", 0, PluralCategory.Zero)]
    [InlineData("ar", 2, PluralCategory.Two)]
    [InlineData("ar", 7, PluralCategory.Few)]
    [InlineData("ar", 25, PluralCategory.Many)]
    [InlineData("ar", 100, PluralCategory.Other)]
    [InlineData("xx", 1, PluralCategory.One)]
    public void Plural_rules_follow_cldr(string locale, int n, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.Select(locale, n));

    [Fact]
    public void A_fraction_is_always_other() =>
        Assert.Equal(PluralCategory.Other, PluralRules.Select("en", 1.5m));

    [Fact]
    public void Russian_plural_uses_the_few_branch()
    {
        const string t = "{n, plural, one {# файл} few {# файла} many {# файлов} other {# файла}}";

        Assert.Equal("3 файла", IcuMessage.Format(t, "ru", Args(("n", 3))));
        Assert.Equal("5 файлов", IcuMessage.Format(t, "ru", Args(("n", 5))));
        Assert.Equal("21 файл", IcuMessage.Format(t, "ru", Args(("n", 21))));
    }

    [Fact]
    public void Apostrophes_follow_icu_defaults()
    {
        Assert.Equal("It's fine", IcuMessage.Format("It's fine", "en", Args()));
        Assert.Equal("It's", IcuMessage.Format("It''s", "en", Args()));
        Assert.Equal("Use {name} here", IcuMessage.Format("Use '{name}' here", "en", Args()));
    }

    [Theory]
    [InlineData("{n, plural, one {# file}}")]
    [InlineData("{missing}")]
    [InlineData("{n")]
    [InlineData("a } b")]
    [InlineData("{n, ordinal, other {x}}")]
    public void A_malformed_template_throws(string template) =>
        Assert.Throws<FormatException>(() => IcuMessage.Format(template, "en", Args(("n", 1))));

    [Fact]
    public void A_plural_over_a_non_number_throws() =>
        Assert.Throws<FormatException>(() => IcuMessage.Format(_files, "en", Args(("n", "many"))));

    [Fact]
    public void ResolveIcu_uses_the_locale_of_the_catalogue_that_has_the_key()
    {
        var text = new LocalisedText([
            MessageCatalog.Create("ru", new Dictionary<string, string> { ["files"] = "{n, plural, one {# файл} few {# файла} many {# файлов} other {# файла}}" }),
            MessageCatalog.Create("en", new Dictionary<string, string> { ["files"] = _files, ["only.en"] = "{n, plural, one {# item} other {# items}}" }),
        ]);

        Assert.Equal("3 файла", text.ResolveIcu("files", Args(("n", 3))));
        Assert.Equal("1 item", text.ResolveIcu("only.en", Args(("n", 1))));
    }

    [Fact]
    public void ResolveIcu_shows_a_broken_or_missing_message_instead_of_throwing()
    {
        var text = new LocalisedText([MessageCatalog.Create("en", new Dictionary<string, string> { ["bad"] = "{n, plural, one {x}}" })]);

        Assert.Equal("<bad: invalid>", text.ResolveIcu("bad", Args(("n", 1))));
        Assert.Equal("<gone>", text.ResolveIcu("gone", Args()));
    }
}
