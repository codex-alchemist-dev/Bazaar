using Avalonia.Controls;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Posture;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Shell;

/// <summary>
/// The one call a desktop head makes for the visual system (doc 93): materials on the window chrome and the posture layout in the
/// window. Call <see cref="Install"/> before <see cref="ShellHost.Install"/>, which then wraps the result in the accessibility chrome.
/// The head supplies the platform pieces (its <see cref="IMaterialEnvironmentSource"/> and <see cref="IPostureSource"/>); nothing here
/// reads the OS.
/// </summary>
public sealed class ShellVisuals : IDisposable
{
    private ShellVisuals(ShellMaterials materials, ShellPosture posture)
    {
        Materials = materials;
        Posture = posture;
    }

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
        return new ShellVisuals(materials, shellPosture);
    }

    public void Dispose() => Posture.Dispose();
}
