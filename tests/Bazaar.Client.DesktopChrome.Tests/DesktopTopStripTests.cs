using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Motion;

namespace Bazaar.Client.DesktopChrome.Tests;

public class DesktopTopStripTests
{
    [AvaloniaFact]
    public void Shows_the_placeholder_until_a_group_chat_is_set_then_its_name()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();

        Assert.Equal(ChromeLabels.Default.NoGroupChat, shell.Strip.TitleText);

        shell.Strip.GroupChatName = "build-room";

        Assert.Equal("build-room", shell.Strip.TitleText);
        Assert.Equal("build-room", AutomationProperties.GetName(FindTitle(shell)));
    }

    [AvaloniaFact]
    public void The_name_cross_fades_in_and_ends_fully_visible()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        TextBlock title = FindTitle(shell);

        shell.Strip.GroupChatName = "design";
        Assert.Equal(0, title.Opacity);

        TimeSpan took = rig.Frames.Settle();

        Assert.Equal(1, title.Opacity);
        Assert.InRange(took.TotalMilliseconds, 150, 230);
    }

    [AvaloniaFact]
    public void Reduced_motion_fades_in_ninety_milliseconds_or_less()
    {
        var rig = new ChromeRig(MotionLevel.Reduced);
        DesktopChromeShell shell = rig.Create();

        shell.Strip.GroupChatName = "design";
        TimeSpan took = rig.Frames.Settle();

        Assert.Equal(1, FindTitle(shell).Opacity);
        Assert.True(took <= TimeSpan.FromMilliseconds(90 + 32));
    }

    [AvaloniaFact]
    public void The_status_slot_holds_what_the_head_puts_in_it_and_is_named()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        var chip = new TextBlock { Text = "healthy" };

        shell.Strip.StatusSlot.Child = chip;

        Assert.Same(chip, shell.Strip.StatusSlot.Child);
        Assert.Equal(ChromeLabels.Default.StatusName, AutomationProperties.GetName(shell.Strip.StatusSlot));
        Assert.Equal(ChromeLabels.Default.StripName, AutomationProperties.GetName(shell.Strip.View));
    }

    [AvaloniaFact]
    public void The_strip_height_is_the_metric_times_the_spacing_token()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();

        Assert.Equal(new ChromeMetrics().StripHeightUnits * new ChromeTheme(rig.Theme).Length("space.lg"), shell.Strip.View.Height);
    }

    private static TextBlock FindTitle(DesktopChromeShell shell) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(shell.Strip.View).OfType<TextBlock>().First(t => AutomationProperties.GetAutomationId(t) == "bazaar.strip.title");
}
