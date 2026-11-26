using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.GeneticMemory.Avalonia;

namespace Bazaar.Client.GeneticMemory.Avalonia.Tests;

public class ReviewQueueTests
{
    private sealed class FakeClient(params GmtQueueItem[] items) : IGmtQueueClient
    {
        private readonly List<GmtQueueItem> _items = [.. items];

        public List<(Guid Id, string? Title, string? Body)> Accepted { get; } = [];

        public List<Guid> Rejected { get; } = [];

        public GmtDecisionResult? NextResult { get; set; }

        public bool Throw { get; set; }

        public GmtReviewerRole? LastRole { get; private set; }

        public Task<IReadOnlyList<GmtQueueItem>> ListAsync(GmtReviewerRole role, CancellationToken ct)
        {
            LastRole = role;
            return Throw ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyList<GmtQueueItem>>([.. _items]);
        }

        public Task<GmtDecisionResult> AcceptAsync(Guid id, GmtReviewerRole role, string? editedTitle, string? editedBody, CancellationToken ct)
        {
            Accepted.Add((id, editedTitle, editedBody));
            return Task.FromResult(Finish(id));
        }

        public Task<GmtDecisionResult> RejectAsync(Guid id, GmtReviewerRole role, CancellationToken ct)
        {
            Rejected.Add(id);
            return Task.FromResult(Finish(id));
        }

        private GmtDecisionResult Finish(Guid id)
        {
            var result = NextResult ?? new GmtDecisionResult(true);
            if (result.Succeeded)
            {
                _items.RemoveAll(i => i.Id == id);
            }

            return result;
        }
    }

    private static GmtQueueItem Item(string title, bool tainted = false, bool ownerOnly = false, string? trace = "trace/abc") => new(
        Guid.NewGuid(), "child-1", "agent-new", "Lesson", "Procedure", title, "Back off on retry.", trace, tainted, ownerOnly,
        new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Refresh_lists_the_queue_and_asks_for_the_viewers_role()
    {
        var client = new FakeClient(Item("Retry"), Item("Timeouts"));
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Architect);

        await vm.RefreshAsync();

        Assert.Equal(["Retry", "Timeouts"], vm.Rows.Select(r => r.Title));
        Assert.Equal(GmtReviewerRole.Architect, client.LastRole);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Selecting_a_row_loads_its_text_and_enables_the_buttons()
    {
        var vm = new ReviewQueueViewModel(new FakeClient(Item("Retry", trace: "trace/x")), GmtReviewerRole.Owner);
        await vm.RefreshAsync();
        Assert.False(vm.CanDecide);

        vm.Selected = vm.Rows[0];

        Assert.Equal("Retry", vm.EditedTitle);
        Assert.Equal("Back off on retry.", vm.EditedBody);
        Assert.Equal("Trace: trace/x", vm.TraceText);
        Assert.True(vm.AcceptCommand.CanExecute(null));
    }

    [Fact]
    public async Task Accepting_without_edits_sends_no_edits_and_removes_the_row()
    {
        var client = new FakeClient(Item("Retry"));
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Owner);
        await vm.RefreshAsync();
        vm.Selected = vm.Rows[0];

        await vm.AcceptAsync();

        var call = Assert.Single(client.Accepted);
        Assert.Null(call.Title);
        Assert.Null(call.Body);
        Assert.Empty(vm.Rows);
        Assert.Equal("Accepted.", vm.Notice);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public async Task Accepting_with_an_edited_body_sends_only_the_changed_field()
    {
        var client = new FakeClient(Item("Retry"));
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Owner);
        await vm.RefreshAsync();
        vm.Selected = vm.Rows[0];
        vm.EditedBody = "Cap the delay at 30 seconds.";

        await vm.AcceptAsync();

        var call = Assert.Single(client.Accepted);
        Assert.Null(call.Title);
        Assert.Equal("Cap the delay at 30 seconds.", call.Body);
    }

    [Fact]
    public async Task A_refused_accept_keeps_the_row_and_shows_the_servers_reason()
    {
        var client = new FakeClient(Item("Retry")) { NextResult = new GmtDecisionResult(false, "Your edit holds a secret.") };
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Owner);
        await vm.RefreshAsync();
        vm.Selected = vm.Rows[0];

        await vm.AcceptAsync();

        Assert.Single(vm.Rows);
        Assert.Equal("Your edit holds a secret.", vm.Notice);
        Assert.True(vm.CanDecide);
    }

    [Fact]
    public async Task Reject_removes_the_row()
    {
        var client = new FakeClient(Item("Retry"));
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Architect);
        await vm.RefreshAsync();
        vm.Selected = vm.Rows[0];

        await vm.RejectAsync();

        Assert.Single(client.Rejected);
        Assert.Empty(vm.Rows);
        Assert.Equal("Rejected.", vm.Notice);
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_selection()
    {
        var client = new FakeClient(Item("Retry"));
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Owner);
        await vm.RefreshAsync();

        await vm.AcceptAsync();
        await vm.RejectAsync();

        Assert.Empty(client.Accepted);
        Assert.Empty(client.Rejected);
    }

    [Fact]
    public async Task A_server_error_shows_a_notice_and_changes_nothing()
    {
        var client = new FakeClient(Item("Retry")) { Throw = true };
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Owner);

        await vm.RefreshAsync();

        Assert.Equal("Could not reach the server. Nothing changed.", vm.Notice);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Flags_show_tainted_and_owner_only()
    {
        var vm = new ReviewQueueViewModel(new FakeClient(Item("Grant", tainted: true, ownerOnly: true)), GmtReviewerRole.Owner);

        await vm.RefreshAsync();

        Assert.Equal("Tainted: stays T1  Owner only", vm.Rows[0].Flags);
    }

    [AvaloniaFact]
    public async Task The_view_shows_the_row_and_the_edit_box_after_selection_and_accept_removes_it()
    {
        var client = new FakeClient(Item("Retry", tainted: true));
        var vm = new ReviewQueueViewModel(client, GmtReviewerRole.Owner);
        await vm.RefreshAsync();
        var window = new Window { Width = 900, Height = 600, DataContext = vm, Content = new ReviewQueueView() };
        window.Show();
        Layout(window);

        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "QueueList");
        Assert.Single(list.Items);
        Assert.False(window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "TitleBox").IsEffectivelyVisible);

        vm.Selected = vm.Rows[0];
        Layout(window);

        Assert.Equal("Retry", window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TitleBox").Text);
        Assert.True(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AcceptButton").IsEffectivelyEnabled);

        await vm.AcceptAsync();
        Layout(window);

        Assert.Empty(list.Items);
        Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "EmptyText").IsEffectivelyVisible);
        Assert.Equal("Accepted.", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "NoticeText").Text);
    }

    private static void Layout(Window window)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
