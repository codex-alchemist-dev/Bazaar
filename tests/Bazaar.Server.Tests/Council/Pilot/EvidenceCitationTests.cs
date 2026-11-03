using Bazaar.Server.Council.Pilot;

namespace Bazaar.Server.Tests.Council.Pilot;

public class EvidenceCitationTests
{
    [Fact]
    public void Extracts_words_of_four_or_more_characters_and_drops_stopwords()
    {
        var tokens = EvidenceCitation.Extract("This is about the Rollback plan.");

        Assert.Contains("rollback", tokens);
        Assert.Contains("plan", tokens);
        Assert.DoesNotContain("this", tokens);
        Assert.DoesNotContain("about", tokens);
        Assert.DoesNotContain("the", tokens);
    }

    [Fact]
    public void Extracts_issue_references_commit_hashes_urls_and_wiki_links()
    {
        var tokens = EvidenceCitation.Extract(
            "See PR #123, commit 3fa9c2e1, https://example.com/x. Also [[Decision Log]].");

        Assert.Contains("#123", tokens);
        Assert.Contains("3fa9c2e1", tokens);
        Assert.Contains("https://example.com/x", tokens);
        Assert.Contains("[[decision log]]", tokens);
    }

    [Fact]
    public void Blank_text_yields_no_tokens()
    {
        Assert.Empty(EvidenceCitation.Extract(null));
        Assert.Empty(EvidenceCitation.Extract("   "));
    }

    [Fact]
    public void IsEvidenceBased_requires_overlap_with_the_citable_set()
    {
        var citable = EvidenceCitation.Extract("The migration plan is missing rollback steps.");

        Assert.True(EvidenceCitation.IsEvidenceBased("Rollback is the problem.", citable));
        Assert.False(EvidenceCitation.IsEvidenceBased("Everyone agrees with the group.", citable));
        Assert.False(EvidenceCitation.IsEvidenceBased(null, citable));
    }
}
