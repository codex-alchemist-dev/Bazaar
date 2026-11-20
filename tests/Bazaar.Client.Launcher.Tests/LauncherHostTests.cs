using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Components.Launcher;

namespace Bazaar.Client.Launcher.Tests;

public class LauncherHostTests
{
    [AvaloniaFact]
    public void HideWhenPreloading_hides_the_window_once_it_opens()
    {
        var window = LauncherHost.HideWhenPreloading(new Window(), preload: true);

        window.Show();

        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void HideWhenPreloading_leaves_the_window_alone_without_the_flag()
    {
        var window = LauncherHost.HideWhenPreloading(new Window(), preload: false);

        window.Show();

        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ActivateMainWindow_without_a_desktop_lifetime_does_nothing()
    {
        LauncherHost.ActivateMainWindow();
    }

    [Fact]
    public void Start_rejects_a_blank_pipe_name_and_a_missing_log()
    {
        Assert.Throws<ArgumentException>(() => LauncherHost.Start(" ", _ => { }));
        Assert.Throws<ArgumentNullException>(() => LauncherHost.Start("bazaar-test", null!));
    }

    [AvaloniaFact]
    public async Task A_started_host_accepts_a_forwarded_launch_and_stops_on_dispose()
    {
        var pipe = $"bazaar-launcher-test-{Guid.NewGuid():N}";
        using var host = LauncherHost.Start(pipe, _ => { });
        var request = LaunchArgs.Parse([]);
        var forwarded = false;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!forwarded && DateTime.UtcNow < deadline)
        {
            forwarded = await LaunchPipe.TryForwardAsync(pipe, request, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        Assert.True(forwarded);
    }
}
