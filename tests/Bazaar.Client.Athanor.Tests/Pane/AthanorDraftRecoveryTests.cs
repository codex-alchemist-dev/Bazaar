using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;

namespace Bazaar.Client.Athanor.Tests.Pane;

public sealed class AthanorDraftRecoveryTests
{
    private static readonly EditorDocumentId _a = new("ws", "src/a.cs");
    private static readonly DateTimeOffset _savedAt = new FixedClock().GetUtcNow();

    private static async Task<(AthanorPaneViewModel Pane, PlainTextFallbackHost Host, InMemoryEditorFileSource Source, InMemoryEditorDraftStore Drafts)> BuildAsync(bool withDraft = true)
    {
        var source = new InMemoryEditorFileSource();
        source.Seed(_a, "server", "csharp");
        var open = await source.OpenAsync(_a, CancellationToken.None);
        var drafts = new InMemoryEditorDraftStore(new FixedClock());
        if (withDraft)
        {
            await drafts.WriteAsync(new EditorDraft(_a, "my draft", open!.Revision, _savedAt), CancellationToken.None);
        }

        var host = new PlainTextFallbackHost();
        return (new AthanorPaneViewModel("Athanor", host, source, drafts), host, source, drafts);
    }

    [Fact]
    public async Task Opening_with_a_stored_draft_offers_it_and_stays_clean()
    {
        var (pane, _, _, _) = await BuildAsync();

        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);

        Assert.Equal("my draft", pane.PendingDraft?.Content);
        Assert.Equal(_savedAt, pane.PendingDraft?.SavedAt);
        Assert.Equal("server", pane.ActiveContent);
        Assert.False(pane.ActiveTab?.Dirty);
    }

    [Fact]
    public async Task Opening_without_a_draft_offers_nothing()
    {
        var (pane, _, _, _) = await BuildAsync(withDraft: false);

        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);

        Assert.Null(pane.PendingDraft);
    }

    [Fact]
    public async Task Restore_makes_the_document_dirty_with_the_draft_content_and_clears_the_offer()
    {
        var (pane, host, _, _) = await BuildAsync();
        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);

        Assert.True(await pane.RestoreDraftAsync(TestContext.Current.CancellationToken));

        Assert.Null(pane.PendingDraft);
        Assert.Equal("my draft", pane.ActiveContent);
        Assert.True(pane.ActiveTab?.Dirty);
        Assert.Equal("my draft", host.GetContent(_a));
        Assert.False(await pane.RestoreDraftAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restore_against_a_newer_server_revision_conflicts_instead_of_overwriting()
    {
        var (pane, _, source, _) = await BuildAsync();
        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);
        source.Seed(_a, "someone else", "csharp");
        await pane.RestoreDraftAsync(TestContext.Current.CancellationToken);

        var outcome = await pane.SaveActiveAsync(TestContext.Current.CancellationToken);

        Assert.IsType<SaveOutcome.Conflict>(outcome);
        Assert.Equal("someone else", pane.ActiveConflict?.ServerContent);
        Assert.True(pane.ActiveTab?.Dirty);
    }

    [Fact]
    public async Task Discard_deletes_the_draft_and_keeps_the_server_copy()
    {
        var (pane, _, _, drafts) = await BuildAsync();
        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);

        Assert.True(await pane.DiscardDraftAsync(TestContext.Current.CancellationToken));

        Assert.Null(pane.PendingDraft);
        Assert.Null(await drafts.ReadAsync(_a, TestContext.Current.CancellationToken));
        Assert.Equal("server", pane.ActiveContent);
        Assert.False(pane.ActiveTab?.Dirty);
    }

    [AvaloniaFact]
    public async Task The_banner_shows_restore_and_discard_and_hides_after_restore()
    {
        var (pane, _, _, _) = await BuildAsync();
        var window = new Window { Content = Bazaar.Client.Panes.Abstractions.PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, [new AthanorPaneModule()], new Bazaar.Client.Panes.Abstractions.PaneModuleContext()).CreateView(pane) };
        window.Show();
        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
        var restore = buttons.Single(b => (string?)b.Content == "Restore");
        var discard = buttons.Single(b => (string?)b.Content == "Discard");
        Assert.True(restore.IsEffectivelyVisible);
        Assert.True(discard.IsEffectivelyVisible);

        await pane.RestoreDraftAsync(TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.False(restore.IsEffectivelyVisible);
        Assert.Equal("my draft", window.GetVisualDescendants().OfType<TextBox>().Single().Text);
    }
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
}
