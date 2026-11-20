using Avalonia.Automation;
using Avalonia.Controls;

namespace Bazaar.Accessibility.Avalonia;

/// <summary>
/// A live region that speaks only what an <see cref="AnnouncementGate"/> allows (doc 49 section 2: live regions are
/// rate-limited and can be turned off per category). A visually hidden text block whose text changes; screen readers
/// announce the change. Place <see cref="Control"/> in the window once. Mentions, approvals and incidents are
/// assertive, group messages polite.
/// </summary>
public sealed class GatedLiveRegion
{
    private readonly AnnouncementGate _gate;
    private readonly Func<string, int, string>? _withHeldBack;

    /// <param name="gate">The policy.</param>
    /// <param name="withHeldBack">
    /// Optional phrasing of the announcement together with the number held back since the last spoken one, for example
    /// an ICU message from the head's catalogue, so the head never concatenates. When null the count is not spoken.
    /// </param>
    public GatedLiveRegion(AnnouncementGate gate, Func<string, int, string>? withHeldBack = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
        _withHeldBack = withHeldBack;
        Control = new TextBlock { Width = 1, Height = 1, Opacity = 0 };
    }

    /// <summary>The hidden control that carries the announcements.</summary>
    public TextBlock Control { get; }

    /// <summary>Speaks <paramref name="text"/> when the gate allows it; returns whether it spoke.</summary>
    public bool Announce(AnnouncementCategory category, string text)
    {
        AnnouncementDecision decision = _gate.Decide(category, text);
        if (!decision.Speak)
        {
            return false;
        }

        AutomationProperties.SetLiveSetting(
            Control,
            category == AnnouncementCategory.GroupMessage ? AutomationLiveSetting.Polite : AutomationLiveSetting.Assertive);
        Control.Text = decision.SuppressedBefore > 0 && _withHeldBack is not null
            ? _withHeldBack(decision.Text, decision.SuppressedBefore)
            : decision.Text;
        return true;
    }
}
