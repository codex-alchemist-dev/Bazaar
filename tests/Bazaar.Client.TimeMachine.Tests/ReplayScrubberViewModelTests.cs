using System.Text.Json;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.TimeMachine.Tests;

public class ReplayScrubberViewModelTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000aa");

    private static ReplayScrubberViewModel Build(
        InMemoryReplayClient client,
        ReplayViewMode mode = ReplayViewMode.Current,
        long head = 10,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(_gc, head, mode, client, delay ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public async Task Opens_at_the_head_without_a_banner_in_current_mode()
    {
        var client = new InMemoryReplayClient(10);
        var vm = Build(client);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, vm.CursorSeq);
        Assert.False(vm.IsReplaying);
        Assert.Null(vm.BannerText);
        Assert.Equal([(_gc, 10L)], client.FrameRequests);
    }

    [Fact]
    public async Task Scrubbing_back_shows_the_read_only_banner_with_the_viewed_seq()
    {
        var vm = Build(new InMemoryReplayClient(10));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.SeekAsync(4, TestContext.Current.CancellationToken);

        Assert.Equal(4, vm.CursorSeq);
        Assert.True(vm.IsReplaying);
        Assert.Contains("seq 4 of 10", vm.BannerText);
        Assert.Contains("Read-only", vm.BannerText);
    }

    [Fact]
    public async Task History_default_shows_the_banner_even_at_the_head()
    {
        var vm = Build(new InMemoryReplayClient(10), ReplayViewMode.History);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.IsReplaying);
        Assert.Contains("seq 10 of 10", vm.BannerText);
    }

    [Fact]
    public async Task Viewer_override_beats_the_install_default_and_can_be_cleared()
    {
        var vm = Build(new InMemoryReplayClient(10), ReplayViewMode.Current);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.SetViewerOverride(ReplayViewMode.History);
        Assert.Equal(ReplayViewMode.History, vm.Mode);
        Assert.NotNull(vm.BannerText);

        vm.SetViewerOverride(null);
        Assert.Equal(ReplayViewMode.Current, vm.Mode);
        Assert.Null(vm.BannerText);
    }

    [Fact]
    public async Task Seek_is_clamped_to_the_range_and_the_server_clamp_wins()
    {
        var client = new InMemoryReplayClient(10);
        var vm = Build(client);

        await vm.SeekAsync(-5, TestContext.Current.CancellationToken);
        Assert.Equal(0, vm.CursorSeq);

        await vm.SeekAsync(99, TestContext.Current.CancellationToken);
        Assert.Equal(10, vm.CursorSeq);
        Assert.Equal(10L, client.FrameRequests[^1].Seq);
    }

    [Fact]
    public async Task Server_clamp_below_the_ask_moves_the_cursor_to_what_was_rendered()
    {
        // The viewer's grant is lower than the head the pane was opened with.
        var client = new InMemoryReplayClient(6);
        var vm = Build(client, head: 10);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, vm.CursorSeq);
        Assert.Equal(6, vm.HeadSeq);
        Assert.True(vm.Frame!.Clamped);
    }

    [Fact]
    public async Task Step_moves_one_seq_and_stops_at_both_ends()
    {
        var vm = Build(new InMemoryReplayClient(2), head: 2);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.StepAsync(-1, TestContext.Current.CancellationToken);
        await vm.StepAsync(-1, TestContext.Current.CancellationToken);
        await vm.StepAsync(-1, TestContext.Current.CancellationToken);

        Assert.Equal(0, vm.CursorSeq);
        Assert.False(vm.CanStepBack);
        Assert.True(vm.CanStepForward);

        await vm.StepAsync(1, TestContext.Current.CancellationToken);
        await vm.StepAsync(1, TestContext.Current.CancellationToken);
        await vm.StepAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.CursorSeq);
        Assert.False(vm.CanStepForward);
    }

    [Fact]
    public async Task Play_from_the_head_restarts_at_zero_and_runs_to_the_head()
    {
        var client = new InMemoryReplayClient(3);
        var vm = Build(client, head: 3);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        client.FrameRequests.Clear();

        await vm.PlayAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal([0L, 1L, 2L, 3L], client.FrameRequests.Select(r => r.Seq));
        Assert.Equal(3, vm.CursorSeq);
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public async Task Pause_stops_play_where_it_got_to()
    {
        var client = new InMemoryReplayClient(10);
        ReplayScrubberViewModel? vm = null;
        var ticks = 0;
        vm = Build(client, delay: (_, _) =>
        {
            if (++ticks == 3)
            {
                vm!.Pause();
            }

            return Task.CompletedTask;
        });
        await vm.SeekAsync(2, TestContext.Current.CancellationToken);

        await vm.PlayAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(vm.IsPlaying);
        Assert.InRange(vm.CursorSeq, 3, 5);
        Assert.True(vm.CursorSeq < 10);
    }

    [Fact]
    public async Task Unavailable_parts_are_shown_as_unavailable_with_the_reason()
    {
        var client = new InMemoryReplayClient(5, (gc, seq) => new ReplayFrameView(
            gc,
            seq,
            5,
            false,
            [],
            [
                ReplayPartView.Available("Kanban", "3 tasks"),
                ReplayPartView.Unavailable("Vault", "Vault is not installed"),
            ]));
        var vm = Build(client, head: 5);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Kanban: 3 tasks", "Vault: unavailable (Vault is not installed)"], vm.Frame!.Parts.Select(p => p.Line));
    }

    [Fact]
    public async Task Fork_calls_the_seam_at_the_viewed_seq_and_reports_it()
    {
        var client = new InMemoryReplayClient(10);
        var vm = Build(client);
        ReplayForkOutcome? raised = null;
        vm.Forked += outcome => raised = outcome;
        await vm.SeekAsync(7, TestContext.Current.CancellationToken);

        await vm.ForkAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(client.ForkRequests);
        Assert.Equal(_gc, request.SourceGcId);
        Assert.Equal(7, request.SourceSeq);
        Assert.Equal(vm.LastFork, raised);
        Assert.Contains("seq 7", vm.ForkNotice);
        Assert.Contains("No messages were copied", vm.ForkNotice);
    }

    [Fact]
    public async Task Fork_retry_at_the_same_seq_reuses_the_request_id_so_it_is_idempotent()
    {
        var client = new InMemoryReplayClient(10);
        var vm = Build(client);
        await vm.SeekAsync(7, TestContext.Current.CancellationToken);

        await vm.ForkAsync(TestContext.Current.CancellationToken);
        await vm.ForkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, client.ForkRequests.Count);
        Assert.Equal(client.ForkRequests[0].RequestId, client.ForkRequests[1].RequestId);
        Assert.Contains("already forked", vm.ForkNotice);
    }

    [Fact]
    public async Task Fork_at_another_seq_uses_a_new_request_id()
    {
        var client = new InMemoryReplayClient(10);
        var vm = Build(client);
        await vm.SeekAsync(7, TestContext.Current.CancellationToken);
        await vm.ForkAsync(TestContext.Current.CancellationToken);
        await vm.SeekAsync(8, TestContext.Current.CancellationToken);

        await vm.ForkAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(client.ForkRequests[0].RequestId, client.ForkRequests[1].RequestId);
    }

    [Fact]
    public async Task Server_refusal_is_shown_and_leaves_the_cursor_alone()
    {
        var client = new InMemoryReplayClient(10);
        var vm = Build(client);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        client.Failure = new ReplayClientException("You may not read this group chat.");

        await vm.SeekAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal("You may not read this group chat.", vm.ErrorText);
        Assert.Equal(10, vm.CursorSeq);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Fork_refusal_is_shown_and_raises_nothing()
    {
        var client = new InMemoryReplayClient(10);
        var vm = Build(client);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        client.Failure = new ReplayClientException("You may not fork this group chat.");
        var raised = false;
        vm.Forked += _ => raised = true;

        await vm.ForkAsync(TestContext.Current.CancellationToken);

        Assert.Equal("You may not fork this group chat.", vm.ErrorText);
        Assert.False(raised);
        Assert.Null(vm.LastFork);
    }

    [Fact]
    public async Task Without_a_client_the_pane_is_inert()
    {
        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.Current, client: null);

        await vm.LoadAsync(TestContext.Current.CancellationToken);
        await vm.ForkAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.HasClient);
        Assert.False(vm.CanFork);
        Assert.False(vm.CanPlay);
        Assert.Null(vm.Frame);
    }

    [Fact]
    public async Task A_stale_slow_frame_does_not_overwrite_a_newer_seek()
    {
        var gate = new TaskCompletionSource();
        var slow = new SlowClient(gate.Task);
        var vm = new ReplayScrubberViewModel(_gc, 10, ReplayViewMode.Current, slow);

        var first = vm.SeekAsync(2, TestContext.Current.CancellationToken); // held at the gate
        await vm.SeekAsync(8, TestContext.Current.CancellationToken); // newer, answers at once
        gate.SetResult();
        await first;

        Assert.Equal(8, vm.CursorSeq);
    }

    [Fact]
    public void Model_parses_gc_head_and_default_view()
    {
        var model = JsonElement.Parse($$"""{"gcId":"{{_gc}}","headSeq":42,"defaultView":"history"}""");

        var vm = ReplayScrubberViewModel.FromModel(model, null);

        Assert.Equal(_gc, vm.GcId);
        Assert.Equal(42, vm.HeadSeq);
        Assert.Equal(ReplayViewMode.History, vm.InstallDefaultView);
    }

    [Theory]
    [InlineData("""{"headSeq":1}""")]
    [InlineData("""{"gcId":"nope","headSeq":1}""")]
    [InlineData("""{"gcId":"0196a6f0-0000-7000-8000-0000000000aa","headSeq":-1}""")]
    [InlineData("""{"gcId":"0196a6f0-0000-7000-8000-0000000000aa"}""")]
    [InlineData("""{"gcId":"0196a6f0-0000-7000-8000-0000000000aa","headSeq":1,"defaultView":"sideways"}""")]
    [InlineData("[]")]
    public void A_bad_model_raises_the_pane_model_error_so_the_registry_falls_back(string json)
    {
        var model = JsonElement.Parse(json);

        Assert.Throws<PaneModelException>(() => ReplayScrubberViewModel.FromModel(model, null));
    }

    private sealed class SlowClient(Task gate) : IReplayClient
    {
        public async Task<ReplayFrameView> GetFrameAsync(Guid gcId, long seq, CancellationToken ct)
        {
            if (seq == 2)
            {
                await gate;
            }

            return new ReplayFrameView(gcId, seq, 10, false, [], []);
        }

        public Task<ReplayForkOutcome> ForkAsync(ReplayForkRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
