using Bazaar.Server.Community.Profile;

namespace Bazaar.Server.Community.Tests.Profile;

public class BotProfileBuilderTests
{
    private static readonly DisclosureOptions Defaults = new();

    private static BotProfile Sample(string displayName = "Codex Bot") => new(
        AgentId: "agent-42",
        Platform: "github",
        DisplayName: displayName,
        SponsorName: "Ada Lovelace",
        Contact: "ada@example.org",
        DisclosureUrl: new Uri("https://example.org/bots"));

    [Fact]
    public void Footer_uses_default_template_text_exactly()
    {
        var footer = BotProfileBuilder.Footer(Sample(), Defaults);

        Assert.Equal("Posted by an AI agent (Bazaar), sponsor Ada Lovelace, contact ada@example.org.", footer);
    }

    [Fact]
    public void AppendFooter_adds_footer_after_a_blank_line()
    {
        var text = BotProfileBuilder.AppendFooter("Fixed the parser.", Sample(), Defaults);

        Assert.Equal(
            "Fixed the parser.\n\nPosted by an AI agent (Bazaar), sponsor Ada Lovelace, contact ada@example.org.",
            text);
    }

    [Fact]
    public void AppendFooter_is_idempotent()
    {
        var once = BotProfileBuilder.AppendFooter("Fixed the parser.\n", Sample(), Defaults);
        var twice = BotProfileBuilder.AppendFooter(once, Sample(), Defaults);

        Assert.Equal(once, twice);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "Posted by an AI agent"));
    }

    [Fact]
    public void Footer_is_one_line_with_no_trailing_newline()
    {
        var profile = Sample() with { SponsorName = "Ada\nLovelace" };
        var footer = BotProfileBuilder.Footer(profile, Defaults);

        Assert.DoesNotContain('\n', footer);
        Assert.DoesNotContain('\r', footer);
        Assert.Equal(footer.TrimEnd(), footer);
    }

    [Fact]
    public void Footer_falls_back_to_default_when_template_is_null()
    {
        var footer = BotProfileBuilder.Footer(Sample(), new DisclosureOptions(null!));

        Assert.Equal("Posted by an AI agent (Bazaar), sponsor Ada Lovelace, contact ada@example.org.", footer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("No placeholders here.")]
    public void Footer_falls_back_to_default_when_template_is_blank_or_has_no_placeholders(string template)
    {
        var footer = BotProfileBuilder.Footer(Sample(), new DisclosureOptions(template));

        Assert.Equal("Posted by an AI agent (Bazaar), sponsor Ada Lovelace, contact ada@example.org.", footer);
    }

    [Fact]
    public void Footer_substitutes_a_custom_template()
    {
        var options = new DisclosureOptions("Bot run by {sponsor}; write {contact}.");

        Assert.Equal("Bot run by Ada Lovelace; write ada@example.org.", BotProfileBuilder.Footer(Sample(), options));
    }

    [Fact]
    public void Footer_does_not_expand_placeholder_text_inside_values()
    {
        var profile = Sample() with { SponsorName = "{contact}" };

        var footer = BotProfileBuilder.Footer(profile, Defaults);

        Assert.Equal("Posted by an AI agent (Bazaar), sponsor {contact}, contact ada@example.org.", footer);
    }

    [Fact]
    public void DisplayName_gets_the_suffix_once()
    {
        Assert.Equal("Codex Bot (AI agent)", BotProfileBuilder.DisplayName(Sample("Codex Bot"), Defaults));
        Assert.Equal("Codex Bot (AI agent)", BotProfileBuilder.DisplayName(Sample("Codex Bot (AI agent)"), Defaults));
    }

    [Fact]
    public void DisplayName_falls_back_to_default_suffix_when_blank()
    {
        Assert.Equal("Codex Bot (AI agent)", BotProfileBuilder.DisplayName(Sample("Codex Bot"), new DisclosureOptions(DisplayNameSuffix: " ")));
    }

    [Fact]
    public void Validate_returns_no_problems_for_a_complete_profile()
    {
        Assert.Empty(BotProfileBuilder.Validate(Sample()));
    }

    [Fact]
    public void Validate_catches_empty_required_fields()
    {
        var profile = new BotProfile(
            AgentId: " ",
            Platform: "",
            DisplayName: "Codex Bot",
            SponsorName: "",
            Contact: "",
            DisclosureUrl: new Uri("https://example.org/bots"));

        var problems = BotProfileBuilder.Validate(profile);

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("AgentId", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Platform", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("SponsorName", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Contact", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_a_http_disclosure_url()
    {
        var profile = Sample() with { DisclosureUrl = new Uri("http://example.org/bots") };

        var problems = BotProfileBuilder.Validate(profile);

        Assert.Single(problems);
        Assert.StartsWith("DisclosureUrl", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Output_never_contains_signed_off_by()
    {
        var profile = Sample();
        var outputs = new[]
        {
            BotProfileBuilder.DisplayName(profile, Defaults),
            BotProfileBuilder.Footer(profile, Defaults),
            BotProfileBuilder.AppendFooter("Assisted-by: Codex Alchemist", profile, Defaults),
            BotProfileBuilder.AppendFooter("", profile, Defaults),
        };

        foreach (var output in outputs)
            Assert.DoesNotContain("Signed-off-by", output, StringComparison.OrdinalIgnoreCase);
    }
}
