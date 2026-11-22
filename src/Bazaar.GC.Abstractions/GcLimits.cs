namespace Bazaar.Server.Gc;

/// <summary>Limits every GC writer shares, so a poster outside the GC module applies the same bound.</summary>
public static class GcLimits
{
    /// <summary>Longest message text, in UTF-16 units, that a send, an edit or an agent post may carry.</summary>
    public const int MaxTextLength = 64 * 1024;
}
