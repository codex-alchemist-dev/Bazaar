using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Core.Tests.Mobile;

public sealed class PhoneShellProfileCacheTests : IDisposable
{
    private static readonly FoldSnapshot _flat = new(true, FoldState.Flat, false, false);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-shell-profile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Cached_profile_is_shown_before_a_fresh_one_replaces_it()
    {
        var cache = new MemoryProfileCache();
        cache.Save(new PhoneProfile(4, ["cached.one", "cached.two"]));
        var shell = new PhoneShell(Registry("registry.one"));

        Assert.True(shell.ShowCachedProfile(cache));

        Assert.Equal([["cached.one"], ["cached.two"]], shell.Pages.Select(p => p.ToArray()).ToArray());
        Assert.Equal(4, shell.ProfileRevision);

        shell.ApplyProfile(new PhoneProfile(5, ["fresh.one", "fresh.two", "fresh.three"]));

        Assert.Equal([["fresh.one"], ["fresh.two"], ["fresh.three"]], shell.Pages.Select(p => p.ToArray()).ToArray());
        Assert.Equal(5, shell.ProfileRevision);
    }

    [AvaloniaFact]
    public void With_no_cache_the_shell_keeps_the_registry_order()
    {
        var shell = new PhoneShell(Registry("registry.one", "registry.two"));

        Assert.False(shell.ShowCachedProfile(new MemoryProfileCache()));

        Assert.Equal([["registry.one"], ["registry.two"]], shell.Pages.Select(p => p.ToArray()).ToArray());
        Assert.Null(shell.ProfileRevision);
    }

    [AvaloniaFact]
    public void With_a_corrupt_cache_file_the_shell_keeps_the_registry_order()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FileProfileCache.FileName), "{ not json");
        var shell = new PhoneShell(Registry("registry.one"));

        Assert.False(shell.ShowCachedProfile(new FileProfileCache(_dir)));

        Assert.Equal([["registry.one"]], shell.Pages.Select(p => p.ToArray()).ToArray());
        Assert.Null(shell.ProfileRevision);
    }

    [AvaloniaFact]
    public void A_fold_switch_on_the_cached_profile_keeps_the_first_visible_pane()
    {
        var cache = new MemoryProfileCache();
        cache.Save(new PhoneProfile(1, ["a", "b", "c", "d"]));
        var shell = new PhoneShell(Registry("registry.one"));
        shell.ShowCachedProfile(cache);
        using var source = new FakePostureSource(_flat);
        using var binder = new PostureShellBinder(source, shell, () => 900, HalfOpenLayoutPolicy.UnfoldedIfWide);
        shell.GoToPage(0);

        // Folded page 2 shows "c" from the cached profile. Folding out to unfolded must keep "c" on screen.
        source.Push(FoldSnapshot.NoFold);
        shell.GoToPage(2);
        source.Push(_flat);

        Assert.Equal(LayoutVariant.Unfolded, binder.Layout);
        Assert.Equal(1, shell.SelectedPage);
        Assert.Equal(["c", "d"], shell.Pages[1].ToArray());
    }

    [AvaloniaFact]
    public void A_fresh_profile_keeps_the_first_visible_pane_when_it_still_exists()
    {
        var shell = new PhoneShell();
        shell.ApplyProfile(new PhoneProfile(1, ["a", "b", "c", "d"]));
        shell.GoToPage(1);

        shell.ApplyProfile(new PhoneProfile(2, ["w", "x", "y", "z"]));

        Assert.Equal(1, shell.SelectedPage);
        Assert.Equal(["x"], shell.Pages[1].ToArray());
    }

    [AvaloniaFact]
    public void The_window_shows_the_cached_panes_first_and_then_the_fresh_ones()
    {
        var cache = new MemoryProfileCache();
        cache.Save(new PhoneProfile(1, ["cached.one"]));
        var shell = new PhoneShell(Registry("registry.one"));
        var window = new Window { Content = shell, Width = 360, Height = 740 };
        window.Show();

        shell.ShowCachedProfile(cache);

        Assert.Contains("cached.one", ShownText(window));
        Assert.DoesNotContain("registry.one", ShownText(window));

        shell.ApplyProfile(new PhoneProfile(2, ["fresh.one"]));

        Assert.Contains("fresh.one", ShownText(window));
        Assert.DoesNotContain("cached.one", ShownText(window));
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

    private static List<string> ShownText(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
}
