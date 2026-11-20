using Avalonia.Headless.XUnit;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PostureShellBinderTests
{
    private static readonly FoldSnapshot _flat = new(true, FoldState.Flat, false, false);
    private static readonly FoldSnapshot _halfOpen = new(true, FoldState.HalfOpened, true, false);

    [AvaloniaFact]
    public void Binding_applies_the_initial_posture_at_once()
    {
        using var source = new FakePostureSource(_flat);
        var shell = new PhoneShell(Registry("a", "b"));

        using var binder = new PostureShellBinder(source, shell, () => 900, HalfOpenLayoutPolicy.UnfoldedIfWide);

        Assert.Equal(LayoutVariant.Unfolded, binder.Layout);
        Assert.Equal(LayoutVariant.Unfolded, shell.Layout);
    }

    [AvaloniaFact]
    public void Each_posture_change_switches_the_shell_locally()
    {
        using var source = new FakePostureSource();
        var shell = new PhoneShell(Registry("a", "b"));
        using var binder = new PostureShellBinder(source, shell, () => 900, HalfOpenLayoutPolicy.UnfoldedIfWide);

        source.Push(_flat);
        Assert.Equal(LayoutVariant.Unfolded, shell.Layout);

        source.Push(FoldSnapshot.NoFold);
        Assert.Equal(LayoutVariant.Folded, shell.Layout);
        Assert.Equal(LayoutVariant.Folded, binder.Layout);
    }

    [AvaloniaFact]
    public void Half_open_follows_the_width_the_head_reports()
    {
        using var source = new FakePostureSource();
        var width = 400.0;
        var shell = new PhoneShell(Registry("a", "b"));
        using var binder = new PostureShellBinder(source, shell, () => width, HalfOpenLayoutPolicy.UnfoldedIfWide);

        source.Push(_halfOpen);
        Assert.Equal(LayoutVariant.Folded, shell.Layout);

        width = 700;
        binder.Apply(PostureKind.HalfOpen);
        Assert.Equal(LayoutVariant.Unfolded, shell.Layout);
    }

    [AvaloniaFact]
    public void Disposing_the_binder_stops_following_the_source()
    {
        using var source = new FakePostureSource();
        var shell = new PhoneShell(Registry("a", "b"));
        var binder = new PostureShellBinder(source, shell, () => 900, HalfOpenLayoutPolicy.UnfoldedIfWide);
        binder.Dispose();

        source.Push(_flat);

        Assert.Equal(LayoutVariant.Folded, shell.Layout);
    }

    private static PaneRegistry Registry(params string[] kinds)
    {
        var registry = new PaneRegistry(PaneViews.Instance);
        foreach (var kind in kinds)
        {
            registry.Register(kind, descriptor => new GenericPaneViewModel(descriptor));
        }

        return registry;
    }
}
