using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Tests.Views;

/// <summary>
/// The GC feed pilot (doc 41c, section 10.4): the "what changed while away" feed as a view document. It must read and
/// validate with no diagnostics, and it must stay a document that every head can load from the shared file.
/// </summary>
public class GcFeedPilotTests
{
    private static string LoadPilot()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "pilots", "gc-feed.view.json");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_gc_feed_pilot_reads_and_validates_with_no_problems()
    {
        ViewDocument document = ViewDocumentReader.Read(LoadPilot());

        Assert.Equal("bazaar.gc.feed", document.Name);
        Assert.Empty(ViewValidator.Validate(document));
    }

    [Fact]
    public void The_pilot_declares_one_list_of_gc_rows_and_two_counts()
    {
        ViewDocument document = ViewDocumentReader.Read(LoadPilot());

        Assert.Equal(RubedoType.Number, document.Data["unreadGcs"].Type);
        Assert.Equal(RubedoType.Number, document.Data["mentions"].Type);
        Assert.NotNull(document.Data["gcs"].Fields);
        Assert.Equal(RubedoType.Bool, document.Data["gcs"].Fields!["mentioned"]);
    }

    [Fact]
    public void A_pilot_that_reads_a_missing_field_is_reported()
    {
        // Rename only the declared field, so the rows still refer to "mentioned", which is no longer in scope.
        string broken = LoadPilot().Replace("\"mentioned\": \"bool\"", "\"mentionedFlag\": \"bool\"", StringComparison.Ordinal);

        ViewDocument document = ViewDocumentReader.Read(broken);

        Assert.Contains(ViewValidator.Validate(document), d => d.Code == "unknown-identifier");
    }
}
