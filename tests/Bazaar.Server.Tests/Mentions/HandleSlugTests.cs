using Bazaar.Server.Mentions;

namespace Bazaar.Server.Tests.Mentions;

/// <summary>The pure handle rules: shape, slug from a display name, and numeric suffixes.</summary>
public sealed class HandleSlugTests
{
    [Theory]
    [InlineData("Ana", "ana")]
    [InlineData("ana", "ana")]
    [InlineData("Charles", "charles")]
    [InlineData("Ada Lovelace", "ada-lovelace")]
    [InlineData("  Ada   Lovelace  ", "ada-lovelace")]
    [InlineData("Dr. Ada O'Neil", "dr-ada-o-neil")]
    [InlineData("build_bot", "build_bot")]
    [InlineData("Build-Bot 9000", "build-bot-9000")]
    [InlineData("a--b", "a-b")]
    [InlineData("a - b", "a-b")]
    [InlineData("--ana--", "ana")]
    [InlineData("__ana__", "ana")]
    [InlineData("_-ana-_", "ana")]
    [InlineData("a__b", "a__b")]
    [InlineData("007", "007")]
    [InlineData("R2D2", "r2d2")]
    [InlineData("@ana", "ana")]
    [InlineData("ana@example.test", "ana-example-test")]
    [InlineData("tab\tand\nnewline", "tab-and-newline")]
    [InlineData("Zoë Quinn", "zo-quinn")]
    [InlineData("Ünal", "nal")]
    [InlineData("日本 taro", "taro")]
    [InlineData("bot 🤖 one", "bot-one")]
    [InlineData("All", "all")]
    [InlineData("HERE", "here")]
    public void A_name_becomes_its_slug(string name, string expected)
    {
        Assert.Equal(expected, HandleSlug.FromName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    [InlineData("___")]
    [InlineData("-_-")]
    [InlineData("x")]
    [InlineData(" x ")]
    [InlineData("日本語")]
    [InlineData("é")]
    [InlineData("🤖")]
    public void A_name_with_nothing_usable_gives_no_slug(string? name)
    {
        Assert.Null(HandleSlug.FromName(name));
    }

    [Fact]
    public void Only_ascii_letters_are_lowercased()
    {
        // U+212A KELVIN SIGN lowercases to "k" under Unicode rules. It is not an ASCII letter, so it is a separator here.
        Assert.Equal("a-b", HandleSlug.FromName("aKb"));
        // U+0130 (dotted capital I) likewise never becomes "i".
        Assert.Equal("a-b", HandleSlug.FromName("aİb"));
    }

    [Fact]
    public void A_long_name_is_cut_to_the_maximum_length()
    {
        var slug = HandleSlug.FromName(new string('a', 50));

        Assert.Equal(new string('a', HandleSlug.MaxLength), slug);
    }

    [Fact]
    public void A_cut_that_ends_on_a_separator_is_trimmed()
    {
        // 31 letters, a space, more letters: the cut at 32 would end on "-".
        var slug = HandleSlug.FromName(new string('a', 31) + " tail");

        Assert.Equal(new string('a', 31), slug);
    }

    [Fact]
    public void A_cut_that_leaves_one_character_gives_no_slug()
    {
        Assert.Null(HandleSlug.FromName("a" + new string('_', 40) + "b"));
    }

    [Theory]
    [InlineData("Ana")]
    [InlineData("Ada Lovelace")]
    [InlineData("Dr. Ada O'Neil")]
    [InlineData("Build-Bot 9000")]
    [InlineData("a__b")]
    [InlineData("日本 taro")]
    public void Every_slug_is_a_valid_handle(string name)
    {
        Assert.True(HandleSlug.IsValid(HandleSlug.FromName(name)));
    }

    [Theory]
    [InlineData("ana")]
    [InlineData("ab")]
    [InlineData("a1")]
    [InlineData("1a")]
    [InlineData("007")]
    [InlineData("build_bot")]
    [InlineData("build-bot")]
    [InlineData("a-")]
    [InlineData("a_")]
    [InlineData("hereafter")]
    [InlineData("all-2")]
    public void A_well_formed_handle_is_valid(string handle)
    {
        Assert.True(HandleSlug.IsValid(handle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Ana")]
    [InlineData("aNa")]
    [InlineData("-ana")]
    [InlineData("_ana")]
    [InlineData("an a")]
    [InlineData("ana.b")]
    [InlineData("@ana")]
    [InlineData("zoë")]
    [InlineData("here")]
    [InlineData("all")]
    public void A_malformed_or_reserved_handle_is_not_valid(string? handle)
    {
        Assert.False(HandleSlug.IsValid(handle));
    }

    [Fact]
    public void The_length_limits_are_2_and_32()
    {
        Assert.Equal(2, HandleSlug.MinLength);
        Assert.Equal(32, HandleSlug.MaxLength);
        Assert.True(HandleSlug.IsValid(new string('a', 32)));
        Assert.False(HandleSlug.IsValid(new string('a', 33)));
    }

    [Theory]
    [InlineData("here", true)]
    [InlineData("all", true)]
    [InlineData("ana", false)]
    [InlineData("All", false)]
    public void Only_the_broadcast_words_are_reserved(string handle, bool expected)
    {
        Assert.Equal(expected, HandleSlug.IsReserved(handle));
    }

    [Theory]
    [InlineData("Ana", "ana")]
    [InlineData("  ANA ", "ana")]
    [InlineData("Build_Bot-9", "build_bot-9")]
    [InlineData("ZOË", "zoË")]
    public void Normalising_trims_and_lowercases_ascii(string input, string expected)
    {
        Assert.Equal(expected, HandleSlug.Normalize(input));
    }

    [Theory]
    [InlineData("ana", 1, "ana")]
    [InlineData("ana", 2, "ana-2")]
    [InlineData("ana", 10, "ana-10")]
    [InlineData("ada-lovelace", 3, "ada-lovelace-3")]
    public void A_suffix_is_appended_after_the_first_candidate(string slug, int n, string expected)
    {
        Assert.Equal(expected, HandleSlug.WithSuffix(slug, n));
    }

    [Fact]
    public void A_suffix_on_a_full_length_slug_cuts_the_slug()
    {
        var full = new string('a', 32);

        Assert.Equal(new string('a', 30) + "-2", HandleSlug.WithSuffix(full, 2));
        Assert.Equal(new string('a', 28) + "-100", HandleSlug.WithSuffix(full, 100));
        Assert.True(HandleSlug.IsValid(HandleSlug.WithSuffix(full, 999)));
    }

    [Fact]
    public void A_suffix_never_follows_a_separator_left_by_the_cut()
    {
        // The cut to 30 characters ends on "-"; it is trimmed before the suffix goes on.
        var slug = new string('a', 29) + "-bb";

        Assert.Equal(new string('a', 29) + "-2", HandleSlug.WithSuffix(slug, 2));
    }

    [Fact]
    public void A_suffix_below_1_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HandleSlug.WithSuffix("ana", 0));
    }

    [Fact]
    public void Handles_with_separators_are_found_by_the_mention_parser()
    {
        // The parser and the handle shape must agree: "_" and "-" inside a handle do not end it early.
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var members = new[]
        {
            new Bazaar.Server.Agents.Turns.Posting.GcMentionMember(other, "ada"),
            new Bazaar.Server.Agents.Turns.Posting.GcMentionMember(id, "ada-lovelace_2"),
        };

        var scan = Bazaar.Server.Agents.Turns.Posting.AgentMentionParser.Scan("ping @Ada-Lovelace_2, please", members);

        var span = Assert.Single(scan.Spans);
        Assert.Equal(id, span.PrincipalId);
    }
}
