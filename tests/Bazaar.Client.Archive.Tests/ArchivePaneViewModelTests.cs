using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Archive.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Archive.Tests;

public class ArchivePaneViewModelTests
{
    private const string _book = """
        {
          "chapters": [
            {
              "id": "ch-1", "title": "Launch", "version": 1, "predecessorId": null,
              "sentences": [
                { "text": "The team shipped the first build.", "sourceRef": "gc_message:m-1" },
                { "text": "A review followed.", "sourceRef": "decision:decisions/0001-review.md" }
              ]
            },
            {
              "id": "ch-2", "title": "Second look", "version": 2, "predecessorId": "ch-1",
              "sentences": [ { "text": "The build was re-tested.", "sourceRef": "gc_event:e-9" } ]
            }
          ]
        }
        """;

    internal static PaneDescriptor Descriptor(string model) =>
        new(PaneKinds.Archive, "Archive", string.Empty, JsonElement.Parse(model));

    internal static ArchivePaneViewModel Book(string model) => new(Descriptor(model));

    private static string Chapter(string extra = "") =>
        """{ "id": "ch-1", "title": "Launch", "version": 1, "sentences": [] """ + extra + " }";

    private static string Model(string chapters) => """{ "chapters": [ """ + chapters + " ] }";

    [Fact]
    public void Parses_chapters_and_sentences_in_server_order()
    {
        var archive = Book(_book);

        Assert.Equal(PaneKinds.Archive, archive.Kind);
        Assert.Equal("Archive", archive.Title);
        Assert.Equal(["ch-1", "ch-2"], archive.Chapters.Select(c => c.Id));
        var first = archive.Chapters[0];
        Assert.Equal("Launch", first.Title);
        Assert.Equal("1", first.Version);
        Assert.Null(first.PredecessorId);
        Assert.Equal("The team shipped the first build.", first.Sentences[0].Text);
        Assert.Equal("gc_message:m-1", first.Sentences[0].SourceRef);
        Assert.Equal("decision:decisions/0001-review.md", first.Sentences[1].SourceRef);
        Assert.Equal("2", archive.Chapters[1].Version);
    }

    [Fact]
    public void Opens_on_the_first_chapter()
    {
        var archive = Book(_book);

        Assert.Equal("ch-1", archive.SelectedChapter?.Id);
        Assert.Equal(0, archive.SelectedIndex);
    }

    [Fact]
    public void Empty_book_has_no_selected_chapter_and_no_navigation()
    {
        var archive = Book("""{ "chapters": [] }""");

        Assert.Empty(archive.Chapters);
        Assert.Null(archive.SelectedChapter);
        Assert.Equal(-1, archive.SelectedIndex);
        Assert.False(archive.SelectNext());
        Assert.False(archive.SelectPrevious());
    }

    [Fact]
    public void Missing_chapters_array_raises_a_model_exception()
    {
        Assert.Throws<PaneModelException>(() => Book("""{ "pages": [] }"""));
    }

    [Theory]
    [InlineData("""{ "id": "ch-1", "version": 1, "sentences": [] }""")]
    [InlineData("""{ "title": "Launch", "version": 1, "sentences": [] }""")]
    [InlineData("""{ "id": "ch-1", "title": "Launch", "sentences": [] }""")]
    [InlineData("""{ "id": "ch-1", "title": "Launch", "version": 1 }""")]
    [InlineData("""{ "id": "ch-1", "title": "Launch", "version": 1, "sentences": [ { "sourceRef": "gc_message:m-1" } ] }""")]
    public void A_missing_required_field_raises_a_model_exception(string chapter)
    {
        Assert.Throws<PaneModelException>(() => Book(Model(chapter)));
    }

    [Fact]
    public void A_chapter_that_is_not_an_object_raises_a_model_exception()
    {
        Assert.Throws<PaneModelException>(() => Book(Model("\"ch-1\"")));
    }

    [Fact]
    public void Select_chapter_by_id_moves_the_selection()
    {
        var archive = Book(_book);

        Assert.True(archive.SelectChapter("ch-2"));

        Assert.Equal("ch-2", archive.SelectedChapter?.Id);
        Assert.Equal(1, archive.SelectedIndex);
    }

    [Fact]
    public void Selecting_an_unknown_id_keeps_the_selection()
    {
        var archive = Book(_book);

        Assert.False(archive.SelectChapter("ch-99"));

        Assert.Equal("ch-1", archive.SelectedChapter?.Id);
    }

    [Fact]
    public void Next_and_previous_move_within_the_book_and_stop_at_the_ends()
    {
        var archive = Book(_book);

        Assert.False(archive.SelectPrevious());
        Assert.Null(archive.PreviousChapter);
        Assert.Equal("ch-2", archive.NextChapter?.Id);

        Assert.True(archive.SelectNext());
        Assert.Equal("ch-2", archive.SelectedChapter?.Id);
        Assert.Null(archive.NextChapter);
        Assert.False(archive.SelectNext());
        Assert.Equal("ch-1", archive.PreviousChapter?.Id);

        Assert.True(archive.SelectPrevious());
        Assert.Equal("ch-1", archive.SelectedChapter?.Id);
    }

    [Fact]
    public void A_regenerated_chapter_links_its_predecessor_in_the_book()
    {
        var archive = Book(_book);
        archive.SelectChapter("ch-2");

        Assert.Equal("ch-1", archive.SelectedChapter?.PredecessorId);
        Assert.Equal("ch-1", archive.PredecessorChapter?.Id);
    }

    [Fact]
    public void A_predecessor_missing_from_the_book_is_kept_as_an_id_and_has_no_chapter()
    {
        var archive = Book(Model(Chapter().Replace("\"sentences\": []", "\"predecessorId\": \"ch-0\", \"sentences\": []")));

        Assert.Equal("ch-0", archive.SelectedChapter?.PredecessorId);
        Assert.Null(archive.PredecessorChapter);
    }

    [Fact]
    public void A_first_chapter_has_no_predecessor()
    {
        var archive = Book(_book);

        Assert.Null(archive.SelectedChapter?.PredecessorId);
        Assert.Null(archive.PredecessorChapter);
    }

    [Fact]
    public void A_predecessor_id_of_the_wrong_type_raises_a_model_exception()
    {
        Assert.Throws<PaneModelException>(() =>
            Book(Model("""{ "id": "ch-2", "title": "B", "version": 2, "predecessorId": 1, "sentences": [] }""")));
    }

    [Theory]
    [InlineData("""{ "text": "No source here." }""")]
    [InlineData("""{ "text": "No source here.", "sourceRef": null }""")]
    [InlineData("""{ "text": "No source here.", "sourceRef": "" }""")]
    [InlineData("""{ "text": "No source here.", "sourceRef": "   " }""")]
    public void A_null_blank_or_absent_source_ref_is_a_missing_source(string sentence)
    {
        var archive = Book(Model(Chapter().Replace("\"sentences\": []", "\"sentences\": [ " + sentence + " ]")));

        var row = archive.SelectedChapter!.Sentences.Single();
        Assert.Null(row.SourceRef);
        Assert.True(row.IsMissingSource);
        Assert.Equal("missing source", row.SourceLabel);
    }

    [Fact]
    public void A_source_ref_is_trimmed_and_shown_as_the_link_target()
    {
        var archive = Book(Model(Chapter().Replace("\"sentences\": []", "\"sentences\": [ { \"text\": \"Hi.\", \"sourceRef\": \"  gc_message:m-7 \" } ]")));

        var row = archive.SelectedChapter!.Sentences.Single();
        Assert.False(row.IsMissingSource);
        Assert.Equal("gc_message:m-7", row.SourceRef);
        Assert.Equal("gc_message:m-7", row.SourceLabel);
    }

    [Fact]
    public void A_source_ref_that_is_not_a_string_raises_a_model_exception()
    {
        Assert.Throws<PaneModelException>(() =>
            Book(Model(Chapter().Replace("\"sentences\": []", "\"sentences\": [ { \"text\": \"Hi.\", \"sourceRef\": 7 } ]"))));
    }

    [Fact]
    public void Registry_builds_the_archive_pane_from_the_module()
    {
        var registry = PaneRegistry.Compose(PaneViews.Instance, ArchivePanes.Modules);

        Assert.True(registry.Contains(PaneKinds.Archive));
        var pane = registry.Create(Descriptor(_book));
        Assert.IsType<ArchivePaneViewModel>(pane);
    }

    [Fact]
    public void Registry_falls_back_to_the_generic_view_on_a_model_mismatch()
    {
        var registry = PaneRegistry.Compose(PaneViews.Instance, ArchivePanes.Modules);

        var pane = registry.Create(Descriptor("""{ "chapters": "none" }"""));

        Assert.IsType<GenericPaneViewModel>(pane);
    }
}
