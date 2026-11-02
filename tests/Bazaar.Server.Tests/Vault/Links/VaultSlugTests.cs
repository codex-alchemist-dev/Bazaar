using Bazaar.Server.Vault.Links;

namespace Bazaar.Server.Tests.Vault.Links;

public sealed class VaultSlugTests
{
    [Theory]
    [InlineData("Token flow", "token-flow")]
    [InlineData("Token flow!", "token-flow")]
    [InlineData("C# notes", "c-notes")]
    [InlineData("  A  B ", "a-b")]
    [InlineData("snake_case-Name", "snake_case-name")]
    [InlineData("Tab\tand\nnewline", "tab-and-newline")]
    [InlineData("Déjà vu", "déjà-vu")]
    [InlineData("Version 2", "version-2")]
    [InlineData("a ! b", "a-b")]
    [InlineData("!Leading", "leading")]
    [InlineData("Trailing !", "trailing")]
    [InlineData("a - b", "a---b")]
    public void Heading_slugs_text_per_rule(string text, string expected)
    {
        Assert.Equal(expected, VaultSlug.Heading(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Heading_gives_empty_for_null_blank_or_all_dropped_input(string? text)
    {
        Assert.Equal(string.Empty, VaultSlug.Heading(text));
    }

    [Fact]
    public void Heading_lowercases_with_invariant_rules()
    {
        Assert.Equal("istanbul", VaultSlug.Heading("ISTANBUL"));
    }
}
