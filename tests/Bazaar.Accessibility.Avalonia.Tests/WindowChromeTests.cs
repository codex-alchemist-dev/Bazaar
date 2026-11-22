using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Localisation;

namespace Bazaar.Accessibility.Avalonia.Tests;

public class WindowChromeTests
{
    private sealed class FakeLocale(string tag) : ILocaleSource
    {
        public string Locale { get; private set; } = tag;

        public event EventHandler? LocaleChanged;

        public void Set(string tag)
        {
            Locale = tag;
            LocaleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static AnnouncementGate NewGate() => new(TimeProvider.System);

    [AvaloniaFact]
    public void Direction_follows_the_locale_and_updates_on_change()
    {
        var window = new Window();
        var locale = new FakeLocale("en");
        WindowChrome.Attach(window, locale, NewGate());
        Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);

        locale.Set("ar-EG");
        Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);

        locale.Set("fr");
        Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
    }

    [AvaloniaFact]
    public void Starts_right_to_left_for_an_rtl_locale()
    {
        var window = new Window();
        WindowChrome.Attach(window, new FakeLocale("he"), NewGate());
        Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
    }

    [AvaloniaFact]
    public void Wrap_hosts_content_and_the_live_region()
    {
        var chrome = WindowChrome.Attach(new Window(), new FakeLocale("en"), NewGate());
        var content = new TextBlock { Text = "hi" };

        var host = Assert.IsType<Grid>(chrome.Wrap(content));
        Assert.Contains(content, host.Children);
        Assert.Contains(chrome.Region.Control, host.Children);
        Assert.True(chrome.Region.Announce(AnnouncementCategory.Mention, "you were mentioned"));
        Assert.Equal("you were mentioned", chrome.Region.Control.Text);
    }
}
