using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Setup.Constitution.Tests;

public class DefaultConstitutionTests
{
    [Fact]
    public void The_front_matter_is_a_valid_constitution_document()
    {
        var parsed = VaultMarkdown.Parse(DefaultConstitution.Markdown);

        Assert.NotNull(parsed.FrontMatter);
        Assert.Equal("constitution", parsed.FrontMatter.Type);
        Assert.Equal("Company Constitution", parsed.FrontMatter.Title);
    }

    [Fact]
    public void Every_required_section_has_a_heading_in_order()
    {
        var text = DefaultConstitution.Markdown;
        var last = -1;
        foreach (var section in DefaultConstitution.Sections)
        {
            var at = text.IndexOf("\n## " + section + "\n", StringComparison.Ordinal);
            Assert.True(at > last, section);
            last = at;
        }
    }

    [Fact]
    public void The_text_uses_unix_line_endings_and_ends_with_one_newline()
    {
        var text = DefaultConstitution.Markdown;

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void The_text_holds_no_company_domain_or_secret_shaped_value()
    {
        var text = DefaultConstitution.Markdown;

        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", text);
    }

    [Fact]
    public void A_document_id_is_a_ulid_that_changes_each_time_and_sorts_by_time()
    {
        var a = DefaultConstitution.NewDocumentId(DateTimeOffset.UnixEpoch.AddDays(1));
        var b = DefaultConstitution.NewDocumentId(DateTimeOffset.UnixEpoch.AddDays(2));
        var c = DefaultConstitution.NewDocumentId(DateTimeOffset.UnixEpoch.AddDays(2));

        Assert.Equal(26, a.Length);
        Assert.Matches("^[0-7][0-9A-HJKMNP-TV-Z]{25}$", a);
        Assert.True(string.CompareOrdinal(a, b) < 0);
        Assert.NotEqual(b, c);
    }
}
