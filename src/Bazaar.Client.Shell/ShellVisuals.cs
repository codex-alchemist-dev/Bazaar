using Avalonia.Controls;
using Bazaar.Accessibility;
using Bazaar.Client.DesktopChrome;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Posture;
using Bazaar.Client.Theme;
using Bazaar.Localisation;

namespace Bazaar.Client.Shell;

/// <summary>
/// The one call a desktop head makes for the visual system (doc 93): materials on the window chrome and the posture layout in the
/// window. Call <see cref="Install"/> before <see cref="ShellHost.Install"/>, which then wraps the result in the accessibility chrome.
/// The head supplies the platform pieces (its <see cref="IMaterialEnvironmentSource"/> and <see cref="IPostureSource"/>); nothing here
/// reads the OS.
/// </summary>
public sealed class ShellVisuals : IDisposable
{
    private readonly MotionSettings _motion;
    private DesktopChromeShell? _chrome;
    private IShellAnnouncer? _announcer;

    private ShellVisuals(ShellMaterials materials, ShellPosture posture, MotionSettings motion)
    {
        Materials = materials;
        Posture = posture;
        _motion = motion;
    }

    /// <summary>The desktop chrome (sidebar, top strip, sheet host) once <see cref="MountDesktopChrome"/> has run; otherwise null.</summary>
    public DesktopChromeShell? Chrome => _chrome;

    /// <summary>Chrome materials: wrap sidebars, the top strip and sheets with <see cref="ShellMaterials.Surface"/>.</summary>
    public ShellMaterials Materials { get; }

    /// <summary>The posture layout: put the current pane in <see cref="ShellPosture.PaneSlot"/>.</summary>
    public ShellPosture Posture { get; }

    /// <summary>
    /// Sets the window's content to the posture layout holding <paramref name="chat"/> (and <paramref name="pane"/> where there is one)
    /// and applies the chrome material to the window. The chat and pane are never wrapped in a material: they are content.
    /// </summary>
    public static ShellVisuals Install(
        Window window,
        Control? chat,
        Control? pane,
        IMaterialEnvironmentSource environment,
        IPostureSource posture,
        ThemeDefinition theme,
        MotionSettings motion,
        IFrameDriver? frames = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(environment);

        window.Content = null;
        var materials = new ShellMaterials(new MaterialPolicy(environment), theme, environment as IMaterialFeedback);
        var shellPosture = ShellPosture.Mount(chat, pane, posture, motion, frames);
        window.Content = shellPosture.Panel;
        materials.AttachWindow(window);
        return new ShellVisuals(materials, shellPosture, motion);
    }

    /// <summary>
    /// Desktop heads only: puts the sidebar, top strip and sheet host around the posture layout (doc 93, Desktop and phone split). The
    /// window's content becomes the chrome and the posture panel moves into its content slot, so chat and pane stay unwrapped. The
    /// chrome surfaces are drawn through <see cref="Materials"/>, so they follow the OS and the person's setting like the window does.
    /// Call it once, before <see cref="ShellHost.Install"/>. Pass the head's <paramref name="locale"/> (and any translated
    /// <paramref name="catalogues"/>) and the chrome's names follow it, English being the fallback; <paramref name="labels"/> is
    /// used only when no locale is passed.
    /// </summary>
    public DesktopChromeShell MountDesktopChrome(
        Window window,
        ThemeDefinition theme,
        IFrameDriver? frames = null,
        ChromeLabels? labels = null,
        ILocaleSource? locale = null,
        IEnumerable<MessageCatalog>? catalogues = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(theme);
        if (_chrome is not null)
        {
            throw new InvalidOperationException("The desktop chrome is already mounted.");
        }

        Control? content = window.Content as Control;
        window.Content = null;
        var placeholder = new Border();
        DesktopChromeShell chrome = DesktopChromeShell.Create(Materials.Surface, theme, _motion, frames ?? new TopLevelFrameDriver(placeholder), content, labels);
        _chrome = chrome;
        if (locale is not null)
        {
            // The chrome's names and announcements follow the active locale and change with it, with no restart.
            chrome.Follow(new ChromeLabelSource(locale, catalogues));
        }

        chrome.Sheets.Announcer = _announcer is null ? null : new SheetAnnouncer(_announcer);
        window.Content = chrome.View;
        return chrome;
    }

    /// <summary>
    /// Sends sheet open and close announcements through the window's live region (the announcer <see cref="ShellHost.Install"/> returns).
    /// Works before or after <see cref="MountDesktopChrome"/>.
    /// </summary>
    public void AnnounceThrough(IShellAnnouncer announcer)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        _announcer = announcer;
        _chrome?.Sheets.Announcer = new SheetAnnouncer(announcer);
    }

    /// <summary>Switches the window material, the chrome and (through them) every surface to another theme.</summary>
    public void ApplyTheme(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Materials.Theme = theme;
        _chrome?.Theme.Use(theme);
    }

    public void Dispose() => Posture.Dispose();

    private sealed class SheetAnnouncer(IShellAnnouncer inner) : ISheetAnnouncer
    {
        public void Announce(string text) => inner.Announce(AnnouncementCategory.Dialog, text);
    }
}
