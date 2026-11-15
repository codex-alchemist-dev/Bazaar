namespace Bazaar.Client.Core.Layout;

/// <summary>
/// Picks the slot for the current device class, display and posture. Pure: no Avalonia and no I/O, so the same
/// rule runs on every head and in tests (doc 41 section 4.8: the client selects locally, with no round trip).
/// </summary>
public static class LayoutResolver
{
    /// <summary>
    /// Returns the matching slot with the most concrete fields. A slot with a wildcard in a field matches any value
    /// there. Ties go to the earlier slot in the document. Returns null when nothing matches, and the caller then uses
    /// the company's baked default.
    /// </summary>
    public static LayoutSlot? Select(LayoutDocument document, string deviceClass, string display, string posture)
    {
        ArgumentNullException.ThrowIfNull(document);

        LayoutSlot? best = null;
        int bestScore = -1;
        foreach (LayoutSlot slot in document.Slots)
        {
            if (!Matches(slot.DeviceClass, deviceClass) || !Matches(slot.Display, display) || !Matches(slot.Posture, posture))
            {
                continue;
            }

            int score = Specificity(slot);
            if (score > bestScore)
            {
                best = slot;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>True when a lock pattern (<c>deviceClass/display/posture</c>, with <c>*</c> wildcards) covers the slot.</summary>
    public static bool IsLocked(IReadOnlyList<string> locks, LayoutSlot slot)
    {
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(slot);

        foreach (string pattern in locks)
        {
            string[] parts = pattern.Split('/');
            if (parts.Length == 1 && parts[0] == LayoutVocabulary.Any)
            {
                return true;
            }

            if (parts.Length == 3
                && Matches(parts[0], slot.DeviceClass)
                && Matches(parts[1], slot.Display)
                && Matches(parts[2], slot.Posture))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Key of a slot in <c>deviceClass/display/posture</c> form, used in locks and audit records.</summary>
    public static string KeyOf(LayoutSlot slot) => $"{slot.DeviceClass}/{slot.Display}/{slot.Posture}";

    private static bool Matches(string pattern, string value) =>
        pattern == LayoutVocabulary.Any || string.Equals(pattern, value, StringComparison.Ordinal);

    private static int Specificity(LayoutSlot slot)
    {
        int score = 0;
        if (slot.DeviceClass != LayoutVocabulary.Any)
        {
            score++;
        }

        if (slot.Display != LayoutVocabulary.Any)
        {
            score++;
        }

        if (slot.Posture != LayoutVocabulary.Any)
        {
            score++;
        }

        return score;
    }
}
