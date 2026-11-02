using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PhoneShellTests
{
    [AvaloniaFact]
    public void Shell_starts_folded()
    {
        var shell = new PhoneShell();

        Assert.Equal(LayoutVariant.Folded, shell.Layout);
    }

    [AvaloniaFact]
    public void Unfolding_switches_the_variant_locally()
    {
        var shell = new PhoneShell();

        var variant = shell.ApplyPosture(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        Assert.Equal(LayoutVariant.Unfolded, variant);
        Assert.Contains(FindText(shell), text => text == "Unfolded layout");
    }

    [AvaloniaFact]
    public void Half_open_follows_width_under_the_default_policy()
    {
        var shell = new PhoneShell();

        Assert.Equal(LayoutVariant.Folded, shell.ApplyPosture(PostureKind.HalfOpen, HalfOpenLayoutPolicy.UnfoldedIfWide, 400));
        Assert.Equal(LayoutVariant.Unfolded, shell.ApplyPosture(PostureKind.HalfOpen, HalfOpenLayoutPolicy.UnfoldedIfWide, 700));
    }

    private static IEnumerable<string> FindText(PhoneShell shell) =>
        ((StackPanel)shell.Content!).Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty);
}
