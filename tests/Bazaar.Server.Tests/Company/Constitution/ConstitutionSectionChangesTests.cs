using Bazaar.Server.Company.Constitution;
using Bazaar.Server.Company.Constitution.Propagation;

namespace Bazaar.Server.Tests.Company.Constitution;

public sealed class ConstitutionSectionChangesTests
{
    private static ConstitutionDocument Document(params (string Heading, string Body)[] sections) =>
        new(sections.Select(section => new ConstitutionSection(section.Heading, section.Body)).ToArray());

    [Fact]
    public void First_activation_reports_all_sections_in_document_order()
    {
        var current = Document(("Mission", "M"), ("Vision", "V"));

        Assert.Equal(["Mission", "Vision"], ConstitutionSectionChanges.Diff(null, current));
    }

    [Fact]
    public void Added_and_edited_sections_follow_the_new_document_order()
    {
        var previous = Document(("Mission", "old"), ("Culture", "same"));
        var current = Document(("Culture", "same"), ("Mission", "new"), ("Vision", "added"));

        Assert.Equal(["Mission", "Vision"], ConstitutionSectionChanges.Diff(previous, current));
    }

    [Fact]
    public void Removed_sections_follow_the_previous_document_order()
    {
        var previous = Document(("Mission", "M"), ("Vision", "V"), ("Culture", "C"));
        var current = Document(("Mission", "M"));

        Assert.Equal(["Vision", "Culture"], ConstitutionSectionChanges.Diff(previous, current));
    }

    [Fact]
    public void Unchanged_documents_have_no_changed_sections()
    {
        var document = Document(("Mission", "M"), ("Vision", "V"));

        Assert.Empty(ConstitutionSectionChanges.Diff(document, Document(("Mission", "M"), ("Vision", "V"))));
    }

    [Fact]
    public void FromDocuments_builds_the_propagation_event()
    {
        var change = ConstitutionChanged.FromDocuments(
            "1111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222",
            Document(("Mission", "old")),
            Document(("Mission", "new"), ("Vision", "added")));

        Assert.Equal(["Mission", "Vision"], change.Sections);
    }

    [Fact]
    public void Duplicate_headings_are_rejected()
    {
        var duplicate = Document(("Mission", "one"), ("Mission", "two"));

        Assert.Throws<ArgumentException>(() => ConstitutionSectionChanges.Diff(null, duplicate));
    }
}
