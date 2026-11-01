// TODO(component): move to the Company component project when PR #125 merges.
using Bazaar.Server.Company.Constitution;

namespace Bazaar.Server.Tests.Company.Constitution;

public class ConstitutionParserTests
{
    private static readonly string _sha = new('a', 40);

    /// <summary>Builds a Constitution with one short body per heading, in the given order.</summary>
    private static string Markdown(params string[] headings) =>
        "# Codex Alchemist Constitution\n\n"
        + string.Concat(headings.Select(heading => $"## {heading}\nBody of {heading}.\n\n"));

    private static string ValidMarkdown() => Markdown(ConstitutionDocument.RequiredHeadings.ToArray());

    private static ConstitutionParseResult ValidParse() => ConstitutionParser.Parse(ValidMarkdown());

    [Fact]
    public void Valid_full_document_parses_all_required_sections_in_order()
    {
        var result = ConstitutionParser.Parse(ValidMarkdown());

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        var document = Assert.IsType<ConstitutionDocument>(result.Document);
        Assert.Equal(ConstitutionDocument.RequiredHeadings, document.Sections.Select(s => s.Heading).ToList());
        Assert.Equal("Body of Mission.", document.FindSection("Mission")?.Body);
        Assert.Equal("Body of Decision index.", document.FindSection("Decision index")?.Body);
    }

    [Fact]
    public void Missing_required_heading_is_refused()
    {
        var headings = ConstitutionDocument.RequiredHeadings.Where(h => h != "Culture").ToArray();

        var result = ConstitutionParser.Parse(Markdown(headings));

        Assert.False(result.Success);
        Assert.Null(result.Document);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ConstitutionParseErrorKind.MissingRequiredHeading, error.Kind);
        Assert.Contains("Culture", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_heading_is_refused()
    {
        var headings = ConstitutionDocument.RequiredHeadings.Append("Mission").ToArray();

        var result = ConstitutionParser.Parse(Markdown(headings));

        Assert.False(result.Success);
        Assert.Null(result.Document);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ConstitutionParseErrorKind.DuplicateHeading, error.Kind);
        Assert.Contains("Mission", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    [InlineData(null)]
    public void Empty_input_is_refused(string? markdown)
    {
        var result = ConstitutionParser.Parse(markdown);

        Assert.False(result.Success);
        Assert.Null(result.Document);
        Assert.Equal(ConstitutionParseErrorKind.EmptyInput, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void Heading_inside_fenced_code_is_not_a_section()
    {
        var markdown = ValidMarkdown().Replace(
            "## Culture\nBody of Culture.\n",
            "## Culture\nBody of Culture.\n```\n## Mission\n```\n",
            StringComparison.Ordinal);

        var result = ConstitutionParser.Parse(markdown);

        Assert.True(result.Success);
        Assert.Contains("## Mission", result.Document!.FindSection("Culture")!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Decision_links_are_extracted_from_the_decision_index_only()
    {
        var markdown = Markdown(ConstitutionDocument.RequiredHeadings.Where(h => h != "Decision index").ToArray())
            + "## Decision index\n"
            + "- [Use Postgres](decisions/0001-use-postgres.md)\n"
            + "- [Adopt Avalonia](decisions/0002-adopt-avalonia.md)\n"
            + "- [Again](decisions/0001-use-postgres.md)\n"
            + "- Bad number: decisions/1-short.md\n"
            + "- Bad slug: decisions/0003-Bad_Slug.md\n"
            + "- Other folder: notes/0004-other.md\n"
            + "- Prefixed: xdecisions/0005-prefixed.md\n";
        var document = ConstitutionParser.Parse(markdown).Document!;
        var mentionedElsewhere = ValidMarkdown().Replace(
            "## Mission\nBody of Mission.\n",
            "## Mission\nSee decisions/0009-elsewhere.md\n",
            StringComparison.Ordinal);

        var links = ConstitutionDecisionLinks.Extract(document);

        Assert.Equal(
            ["decisions/0001-use-postgres.md", "decisions/0002-adopt-avalonia.md"],
            links);

        var outsideIndex = ConstitutionParser.Parse(mentionedElsewhere).Document!;
        Assert.Empty(ConstitutionDecisionLinks.Extract(outsideIndex));
    }

    [Fact]
    public void Activation_is_refused_without_an_accepted_commit()
    {
        var decision = ConstitutionAcceptance.Decide(ValidParse(), commitAccepted: false, _sha);

        Assert.False(decision.Activated);
        Assert.Null(decision.CommitSha);
        Assert.Equal(ConstitutionRefusalReason.CommitNotAccepted, decision.Reason);
        Assert.True(decision.PreviousCommitRemainsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData(null)]
    public void Activation_is_refused_on_a_bad_sha(string? sha)
    {
        var decision = ConstitutionAcceptance.Decide(ValidParse(), commitAccepted: true, sha);

        Assert.False(decision.Activated);
        Assert.Equal(ConstitutionRefusalReason.InvalidCommitSha, decision.Reason);
        Assert.True(decision.PreviousCommitRemainsActive);
    }

    [Fact]
    public void Activation_is_refused_when_the_text_failed_to_parse()
    {
        var failed = ConstitutionParser.Parse("## Mission\nOnly one section.\n");

        var decision = ConstitutionAcceptance.Decide(failed, commitAccepted: true, _sha);

        Assert.False(decision.Activated);
        Assert.Equal(ConstitutionRefusalReason.ParseFailed, decision.Reason);
        Assert.True(decision.PreviousCommitRemainsActive);
    }

    [Fact]
    public void Activation_with_accepted_commit_and_valid_document_activates_the_sha()
    {
        var decision = ConstitutionAcceptance.Decide(ValidParse(), commitAccepted: true, _sha);

        Assert.True(decision.Activated);
        Assert.Equal(_sha, decision.CommitSha);
        Assert.Null(decision.Reason);
        Assert.False(decision.PreviousCommitRemainsActive);
    }
}
