using Bazaar.Localisation;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// Accessible names and visible text of the chrome, resolved from message ids (<see cref="ChromeKeys"/>) through a locale's lookup chain.
/// Immutable: a locale change produces a new instance (see <see cref="ChromeLabelSource"/>). <see cref="Default"/> is English.
/// </summary>
public sealed class ChromeLabels
{
    private readonly LocalisedText _text;

    private ChromeLabels(string locale, LocalisedText text)
    {
        Locale = locale;
        _text = text;
    }

    /// <summary>English, the source locale.</summary>
    public static ChromeLabels Default { get; } = For("en");

    /// <summary>The locale these labels were resolved for.</summary>
    public string Locale { get; }

    public string SidebarName => _text.Resolve(ChromeKeys.SidebarName);

    public string CollapseSidebar => _text.Resolve(ChromeKeys.SidebarCollapse);

    public string ExpandSidebar => _text.Resolve(ChromeKeys.SidebarExpand);

    public string NoGroupChat => _text.Resolve(ChromeKeys.StripNoGroupChat);

    public string StripName => _text.Resolve(ChromeKeys.StripName);

    public string StatusName => _text.Resolve(ChromeKeys.StripStatus);

    public string CloseSheet => _text.Resolve(ChromeKeys.SheetClose);

    public string SheetGrip => _text.Resolve(ChromeKeys.SheetGrip);

    /// <summary>Spoken when a sheet opens.</summary>
    public string SheetOpened(string title) => _text.ResolveIcu(ChromeKeys.SheetOpened, TitleArgs(title));

    /// <summary>Spoken when a sheet closes.</summary>
    public string SheetClosed(string title) => _text.ResolveIcu(ChromeKeys.SheetClosed, TitleArgs(title));

    /// <summary>Labels for <paramref name="locale"/>, using any catalogues the head supplies and falling back to English.</summary>
    public static ChromeLabels For(string locale, IEnumerable<MessageCatalog>? additional = null) =>
        new(locale, ChromeCatalog.ChainFor(locale, additional));

    private static Dictionary<string, object?> TitleArgs(string title) => new() { [ChromeKeys.TitleArgument] = title };
}
