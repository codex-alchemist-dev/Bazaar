using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community.Avalonia.Tests;

public class CommunityLogViewHeadlessTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class Source(Func<CommunityLogQueryDto, CommunityLogPageDto> respond) : ICommunityLogSource
    {
        public Task<CommunityLogPageDto> LoadAsync(CommunityLogQueryDto query, CancellationToken cancellationToken) =>
            Task.FromResult(respond(query));
    }

    private static CommunityLogRowDto Row(long id) =>
        new(id, "agent-1", "github", "Post", "owner/repo#1", "abcd1234", "done", _now.AddMinutes(-id), null, null);

    private static void Pump(Window window)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<Window> ShowAsync(CommunityLogViewModel vm)
    {
        var window = new Window { Width = 900, Height = 700, Content = new CommunityLogView(vm) };
        window.Show();
        await vm.RefreshAsync();
        Pump(window);
        return window;
    }

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task Rows_render_with_status_words_and_hash_but_no_body()
    {
        var vm = new CommunityLogViewModel(new Source(_ => new CommunityLogPageDto([Row(1), Row(2)], "c1")));
        var window = await ShowAsync(vm);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("done", texts);
        Assert.Contains("Status:", texts);
        Assert.Contains("abcd1234", texts);
        Assert.Equal(2, window.GetVisualDescendants().OfType<ListBoxItem>().Count());
        Assert.True(Find<Button>(window, "LoadMoreButton").IsVisible);
        Assert.False(Find<TextBlock>(window, "ErrorText").IsVisible);
    }

    [AvaloniaFact]
    public async Task Empty_log_shows_the_empty_message()
    {
        var window = await ShowAsync(new CommunityLogViewModel(new Source(_ => new CommunityLogPageDto([], null))));

        Assert.True(Find<TextBlock>(window, "EmptyText").IsVisible);
        Assert.False(Find<Button>(window, "LoadMoreButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task Failing_source_shows_an_error_in_words()
    {
        var window = await ShowAsync(new CommunityLogViewModel(new Source(_ => throw new InvalidOperationException("server down"))));

        var error = Find<TextBlock>(window, "ErrorText");
        Assert.True(error.IsVisible);
        Assert.Equal("Error: server down", error.Text);
        Assert.False(Find<TextBlock>(window, "EmptyText").IsVisible);
    }

    [AvaloniaFact]
    public async Task Load_more_button_appends_older_rows()
    {
        var vm = new CommunityLogViewModel(new Source(q => q.Cursor is null
            ? new CommunityLogPageDto([Row(1)], "c1")
            : new CommunityLogPageDto([Row(2)], null)));
        var window = await ShowAsync(vm);

        Find<Button>(window, "LoadMoreButton").Command!.Execute(null);
        await vm.LoadMoreCommand.Last;
        Pump(window);

        Assert.Equal(2, window.GetVisualDescendants().OfType<ListBoxItem>().Count());
        Assert.False(Find<Button>(window, "LoadMoreButton").IsVisible);
    }
}
