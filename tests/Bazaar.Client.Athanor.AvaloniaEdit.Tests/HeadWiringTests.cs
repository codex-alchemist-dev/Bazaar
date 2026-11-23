using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Bazaar.Client.Athanor.Hosting;

namespace Bazaar.Client.Athanor.AvaloniaEdit.Tests;

public sealed class HeadWiringTests
{
    [AvaloniaFact]
    public void Style_include_is_added_once()
    {
        var app = Application.Current!;
        var before = app.Styles.OfType<StyleInclude>().Count(i => i.Source == AvaloniaEditStyles.Source);

        var first = AvaloniaEditStyles.Include(app);
        var second = AvaloniaEditStyles.Include(app);

        Assert.False(second);
        Assert.Equal(before == 0, first);
        Assert.Equal(1, app.Styles.OfType<StyleInclude>().Count(i => i.Source == AvaloniaEditStyles.Source));
    }

    [AvaloniaFact]
    public async Task Host_exposes_its_root_as_the_pane_surface()
    {
        await using var host = new AvaloniaEditHost();

        var surface = Assert.IsAssignableFrom<IEditorSurfaceHost>(host);

        Assert.Same(host.Root, surface.Surface);
    }
}
