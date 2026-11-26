using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bazaar.Accessibility;
using Bazaar.Localisation;

namespace Bazaar.Client.Shell.Tests;

public class ShellHostTests
{
    private sealed class FakeLocale(string tag) : ILocaleSource
    {
        public string Locale { get; } = tag;

        public event EventHandler? LocaleChanged
        {
            add { }
            remove { }
        }
    }

    [AvaloniaFact]
    public void Attach_sets_direction_wraps_content_and_announces()
    {
        var window = new Window();
        var content = new TextBlock { Text = "hi" };

        ShellAttachment shell = ShellHost.Attach(window, content, new FakeLocale("ar"), new AnnouncementGate(TimeProvider.System));
        window.Content = shell.Content;

        Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
        Assert.Same(content, Assert.IsType<LayoutTransformControl>(Assert.IsType<Grid>(shell.Content).Children[0]).Child);
        Assert.True(shell.Announcer.Announce(AnnouncementCategory.Mention, "you were mentioned"));
    }

    [AvaloniaFact]
    public void A_disabled_category_is_not_spoken()
    {
        var gate = new AnnouncementGate(TimeProvider.System);
        gate.Configure(AnnouncementCategory.GroupMessage, new AnnouncementCategorySettings(false, TimeSpan.Zero));

        ShellAttachment shell = ShellHost.Attach(new Window(), null, new FakeLocale("en"), gate);

        Assert.False(shell.Announcer.Announce(AnnouncementCategory.GroupMessage, "hello"));
    }

    [AvaloniaFact]
    public void Install_moves_existing_content_under_the_chrome()
    {
        var content = new TextBlock { Text = "chat" };
        var window = new Window { Content = content };

        IShellAnnouncer announcer = ShellHost.Install(window, new FakeLocale("he"), new AnnouncementGate(TimeProvider.System));

        Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
        Assert.Same(content, Assert.IsType<LayoutTransformControl>(Assert.IsType<Grid>(window.Content).Children[0]).Child);
        Assert.True(announcer.Announce(AnnouncementCategory.ApprovalRequest, "needs you"));
    }

    [AvaloniaFact]
    public void Install_works_on_a_plain_content_host_as_on_android()
    {
        var content = new TextBlock { Text = "phone" };
        var host = new ContentControl { Content = content };

        ShellHost.Install(host, new FakeLocale("ar"), new AnnouncementGate(TimeProvider.System));

        Assert.Equal(FlowDirection.RightToLeft, host.FlowDirection);
        Assert.Contains(content, Assert.IsType<Grid>(host.Content).Children);
        Assert.Same(content, Assert.IsType<LayoutTransformControl>(Assert.IsType<Grid>(host.Content).Children[0]).Child);
    }
}
