namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// How the sheet host says what happened to a screen reader. The module keeps no reference to the shell's announcer; the shell
/// implements this seam over its <c>IShellAnnouncer</c> (the window's gated live region).
/// </summary>
public interface ISheetAnnouncer
{
    void Announce(string text);
}
