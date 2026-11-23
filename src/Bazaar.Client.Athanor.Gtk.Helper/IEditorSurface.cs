namespace Bazaar.Client.Athanor.Gtk.Helper;

/// <summary>Where the editor window goes and whether it shows (doc 57 section 2.1). Position is a hint on Wayland.</summary>
public readonly record struct WindowPlacement(int X, int Y, int Width, int Height, bool Visible, string? Title);

/// <summary>
/// The UI side of the helper: a window with a Monaco page in it. The real implementation is GTK and WebKitGTK; the
/// tests use a fake. All members except <see cref="Dispatch"/> run on the UI thread.
/// </summary>
public interface IEditorSurface
{
    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    void Dispatch(Action action);

    /// <summary>Hands one bridge message (a JSON text) to the page.</summary>
    void PostToPage(string json);

    void ApplyWindow(WindowPlacement placement);

    /// <summary>Ends the UI loop so the process can exit.</summary>
    void Quit();
}
