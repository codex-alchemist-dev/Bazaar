using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Bazaar.Client.Linux.Updates;

namespace Bazaar.Client.Linux.Tests;

public class UpdateNoticeBannerHeadlessTests
{
    private static readonly UpdateNotice _staged = new(UpdateNoticeKind.ReadyAfterRestart, "Update 0.2.0 is ready.", "0.2.0");

    [AvaloniaFact]
    public void Hidden_until_a_notice_arrives_and_hidden_for_null_or_disabled()
    {
        var banner = new UpdateNoticeBanner();
        Assert.False(banner.IsVisible);

        banner.Show(new UpdateNotice(UpdateNoticeKind.Disabled, "Updates are off."));
        Assert.False(banner.IsVisible);

        banner.Show(_staged);
        Assert.True(banner.IsVisible);

        banner.Show(null);
        Assert.False(banner.IsVisible);
    }

    [AvaloniaFact]
    public void Shows_each_state_as_text()
    {
        var banner = new UpdateNoticeBanner();
        var window = new Window { Content = banner };
        window.Show();

        foreach (var notice in new[]
        {
            new UpdateNotice(UpdateNoticeKind.Checking, "Checking for updates..."),
            new UpdateNotice(UpdateNoticeKind.UpToDate, "Bazaar is up to date."),
            new UpdateNotice(UpdateNoticeKind.Failed, "Update not installed: SignatureInvalid."),
            _staged,
        })
        {
            banner.Show(notice);
            Assert.True(banner.IsVisible);
            Assert.Equal(notice.Text, banner.DisplayedText);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Restart_is_offered_only_for_a_staged_update_and_runs_the_callback()
    {
        var pressed = 0;
        var banner = new UpdateNoticeBanner(() => pressed++);
        var window = new Window { Content = banner };
        window.Show();

        banner.Show(new UpdateNotice(UpdateNoticeKind.UpToDate, "Bazaar is up to date."));
        Assert.False(banner.RestartOffered);

        banner.Show(_staged);
        Assert.True(banner.RestartOffered);
        Assert.Equal("Restart to apply update 0.2.0", AutomationProperties.GetName(banner.RestartButton));
        banner.RestartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, pressed);

        window.Close();
    }

    [AvaloniaFact]
    public void No_restart_button_without_a_callback()
    {
        var banner = new UpdateNoticeBanner();

        banner.Show(_staged);

        Assert.False(banner.RestartOffered);
    }

    [AvaloniaFact]
    public void Dismiss_hides_until_a_different_notice_arrives()
    {
        var banner = new UpdateNoticeBanner();
        var window = new Window { Content = banner };
        window.Show();
        banner.Show(_staged);

        banner.DismissButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(banner.IsVisible);

        banner.Show(_staged);
        Assert.False(banner.IsVisible);

        banner.Show(new UpdateNotice(UpdateNoticeKind.Checking, "Checking for updates..."));
        Assert.True(banner.IsVisible);
        banner.Show(_staged);
        Assert.True(banner.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void Is_accessible_with_names_and_a_polite_live_region()
    {
        var banner = new UpdateNoticeBanner();
        var text = Assert.IsType<TextBlock>(((DockPanel)banner.Child!).Children[1]);

        Assert.Equal("Update notice", AutomationProperties.GetName(banner));
        Assert.Equal("Dismiss update notice", AutomationProperties.GetName(banner.DismissButton));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(text));
    }
}
