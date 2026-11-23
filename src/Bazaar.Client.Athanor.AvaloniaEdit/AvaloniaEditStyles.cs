using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>
/// The AvaloniaEdit control theme (doc 57 §2.2). A head adds it to the application styles once; the editor control
/// renders unstyled without it. It is not added by <c>Bazaar.Client.Core</c>.
/// </summary>
public static class AvaloniaEditStyles
{
    /// <summary>The resource the package ships for the Fluent theme.</summary>
    public static Uri Source { get; } = new("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml");

    /// <summary>Adds the include to <paramref name="application"/> unless it is already there. Returns true when it added it.</summary>
    public static bool Include(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        foreach (var style in application.Styles)
        {
            if (style is StyleInclude include && include.Source == Source)
            {
                return false;
            }
        }

        application.Styles.Add(new StyleInclude(new Uri("avares://AvaloniaEdit/")) { Source = Source });
        return true;
    }
}
