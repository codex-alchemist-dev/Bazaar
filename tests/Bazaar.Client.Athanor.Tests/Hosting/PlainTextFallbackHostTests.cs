using Bazaar.Client.Athanor.Hosting;

namespace Bazaar.Client.Athanor.Tests.Hosting;

public class PlainTextFallbackHostTests
{
    private static readonly EditorDocumentId _notes = new("ws", "notes.md");

    [Fact]
    public async Task Open_raises_ready_once_and_the_content_is_readable_in_the_handler()
    {
        await using var host = new PlainTextFallbackHost();
        var readyContent = new List<string>();
        int readyCount = 0;
        host.Ready += (_, _) =>
        {
            readyCount++;
            readyContent.Add(host.GetContent(_notes));
        };

        await host.OpenAsync(HostingFixtures.Document("notes.md", "first"), CancellationToken.None);
        await host.OpenAsync(HostingFixtures.Document("other.md", "second"), CancellationToken.None);

        Assert.Equal(1, readyCount);
        Assert.Equal(["first"], readyContent);
    }

    [Fact]
    public async Task Edit_updates_the_buffer_and_raises_changed_with_a_rising_version_and_dirty_flag()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "base"), CancellationToken.None);
        var changes = new List<EditorChange>();
        host.Changed += (_, change) => changes.Add(change);

        host.Edit(_notes, "base plus");
        host.Edit(_notes, "base plus more");

        Assert.Equal("base plus more", host.GetContent(_notes));
        Assert.Equal([1L, 2L], changes.Select(c => c.Version));
        Assert.All(changes, c => Assert.True(c.Dirty));
        Assert.Equal(["base plus", "base plus more"], changes.Select(c => c.Content));
        Assert.True(host.IsDirty(_notes));
    }

    [Fact]
    public async Task Editing_back_to_the_opened_content_is_clean_again()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "base"), CancellationToken.None);
        EditorChange? last = null;
        host.Changed += (_, change) => last = change;

        host.Edit(_notes, "changed");
        host.Edit(_notes, "base");

        Assert.False(last!.Dirty);
        Assert.False(host.IsDirty(_notes));
    }

    [Fact]
    public async Task Edit_on_a_read_only_document_throws_and_changes_nothing()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "locked", readOnly: true), CancellationToken.None);
        int changed = 0;
        host.Changed += (_, _) => changed++;

        Assert.Throws<InvalidOperationException>(() => host.Edit(_notes, "nope"));

        Assert.Equal("locked", host.GetContent(_notes));
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Turning_read_only_off_allows_edits_again()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "locked", readOnly: true), CancellationToken.None);

        await host.SetReadOnlyAsync(_notes, false, CancellationToken.None);
        host.Edit(_notes, "now writable");

        Assert.Equal("now writable", host.GetContent(_notes));
    }

    [Fact]
    public async Task Edit_and_content_reads_on_a_document_that_is_not_open_throw()
    {
        await using var host = new PlainTextFallbackHost();

        Assert.Throws<InvalidOperationException>(() => host.Edit(_notes, "x"));
        Assert.Throws<InvalidOperationException>(() => host.GetContent(_notes));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetReadOnlyAsync(_notes, true, CancellationToken.None));
    }

    [Fact]
    public async Task Version_keeps_rising_across_close_and_reopen()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "a"), CancellationToken.None);
        var versions = new List<long>();
        host.Changed += (_, change) => versions.Add(change.Version);

        host.Edit(_notes, "b");
        await host.CloseAsync(_notes, CancellationToken.None);
        await host.OpenAsync(HostingFixtures.Document("notes.md", "a"), CancellationToken.None);
        host.Edit(_notes, "c");

        Assert.Equal([1L, 2L], versions);
    }

    [Fact]
    public async Task Save_request_raises_save_requested_and_leaves_the_content_alone()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "draft"), CancellationToken.None);
        host.Edit(_notes, "draft two");
        var requested = new List<EditorDocumentId>();
        host.SaveRequested += (_, id) => requested.Add(id);

        host.RequestSave(_notes);

        Assert.Equal([_notes], requested);
        Assert.Equal("draft two", host.GetContent(_notes));
        Assert.True(host.IsDirty(_notes));
    }

    [Fact]
    public async Task Crash_raises_crashed_then_ready_comes_back_in_that_order()
    {
        await using var host = new PlainTextFallbackHost();
        var order = new List<string>();
        host.Ready += (_, _) => order.Add("ready");
        host.Crashed += (_, reason) => order.Add($"crashed:{reason}");
        await host.OpenAsync(HostingFixtures.Document("notes.md"), CancellationToken.None);

        host.SimulateCrash();
        host.SimulateReady();

        Assert.Equal(["ready", "crashed:RendererExited", "ready"], order);
        Assert.False(host.IsCrashed);
    }

    [Fact]
    public async Task Surface_operations_throw_while_crashed_but_buffers_stay_readable()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "kept"), CancellationToken.None);

        host.SimulateCrash(EditorCrashReason.BridgeFault);

        Assert.True(host.IsCrashed);
        Assert.Throws<InvalidOperationException>(() => host.Edit(_notes, "x"));
        Assert.Throws<InvalidOperationException>(() => host.RequestSave(_notes));
        Assert.Throws<InvalidOperationException>(() => host.PressKey(HostingFixtures.Key("j")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.FocusAsync(CancellationToken.None));
        Assert.Equal("kept", host.GetContent(_notes));
    }

    [Fact]
    public async Task Theme_and_diff_are_recorded_on_the_host()
    {
        await using var host = new PlainTextFallbackHost();

        await host.SetThemeAsync("{\"base\":\"vs-dark\"}\n", CancellationToken.None);
        await host.ShowDiffAsync("old", "new", "csharp", CancellationToken.None);

        Assert.Equal("{\"base\":\"vs-dark\"}\n", host.ThemeJson);
        Assert.Equal(new PlainTextDiffRequest("old", "new", "csharp"), host.LastDiffRequest);
    }

    [Fact]
    public async Task Key_the_router_does_not_claim_is_raised_to_listeners()
    {
        var handoff = new RouterKeyHandoff(_ => false);
        await using var host = new PlainTextFallbackHost(handoff);
        var keys = new List<string>();
        host.KeyPressed += (_, key) => keys.Add(key.Key);

        bool delivered = host.PressKey(HostingFixtures.Key("j"));

        Assert.True(delivered);
        Assert.Equal(["j"], keys);
    }

    [Fact]
    public async Task Key_the_router_claims_never_reaches_the_editor_listeners()
    {
        var handoff = new RouterKeyHandoff(key => key.Key == ":");
        await using var host = new PlainTextFallbackHost(handoff);
        int raised = 0;
        host.KeyPressed += (_, _) => raised++;

        bool delivered = host.PressKey(HostingFixtures.Key(":"));

        Assert.False(delivered);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Ctrl_chords_always_reach_the_editor_even_with_a_claiming_router()
    {
        var handoff = new RouterKeyHandoff(_ => true);
        await using var host = new PlainTextFallbackHost(handoff);
        EditorKeyEvent? seen = null;
        host.KeyPressed += (_, key) => seen = key;

        bool delivered = host.PressKey(HostingFixtures.Key("s", ctrl: true));

        Assert.True(delivered);
        Assert.True(seen!.Ctrl);
    }

    [Fact]
    public async Task Keys_reach_the_editor_when_there_is_no_handoff()
    {
        await using var host = new PlainTextFallbackHost();
        int raised = 0;
        host.KeyPressed += (_, _) => raised++;

        Assert.True(host.PressKey(HostingFixtures.Key("Escape")));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Dispose_blocks_later_use_and_is_idempotent()
    {
        var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "x"), CancellationToken.None);

        await host.DisposeAsync();
        await host.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.OpenAsync(HostingFixtures.Document("b.md"), CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => host.GetContent(_notes));
        Assert.Throws<ObjectDisposedException>(() => host.Edit(_notes, "y"));
    }

    [Fact]
    public async Task Cancelled_open_does_not_open_the_document()
    {
        await using var host = new PlainTextFallbackHost();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.OpenAsync(HostingFixtures.Document("notes.md"), cancellation.Token));

        Assert.Throws<InvalidOperationException>(() => host.GetContent(_notes));
    }

    [Fact]
    public async Task Close_removes_the_buffer()
    {
        await using var host = new PlainTextFallbackHost();
        await host.OpenAsync(HostingFixtures.Document("notes.md", "x"), CancellationToken.None);

        await host.CloseAsync(_notes, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => host.GetContent(_notes));
    }
}
