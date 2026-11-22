using Bazaar.Accessibility;

namespace Bazaar.Client.Shell;

/// <summary>
/// How a feature module speaks through the window's live region (doc 41c). The shell applies the rate limit and the
/// per-category switch; a module only says what happened. No Avalonia reference.
/// </summary>
public interface IShellAnnouncer
{
    /// <summary>Announces <paramref name="text"/>; returns whether it was spoken (false when held back or switched off).</summary>
    bool Announce(AnnouncementCategory category, string text);
}
