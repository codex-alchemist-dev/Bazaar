namespace Bazaar.Push;

/// <summary>
/// Reads the kind token of a bare wake. The wire value is the kind alone (<c>mention</c>, <c>dm</c>, <c>owner_decision</c>,
/// <c>quota</c>), matched exactly. Anything else, including a value with extra text, maps to <see cref="WakeKind.Unknown"/>.
/// </summary>
public static class BareWakeParser
{
    /// <summary>Longer values are not kinds. This bounds what a provider can hand the handler.</summary>
    public const int MaxKindLength = 32;

    public static WakeKind Parse(string? rawKind)
    {
        if (string.IsNullOrEmpty(rawKind) || rawKind.Length > MaxKindLength)
        {
            return WakeKind.Unknown;
        }

        return rawKind switch
        {
            "mention" => WakeKind.Mention,
            "dm" => WakeKind.DirectMessage,
            "owner_decision" => WakeKind.OwnerDecision,
            "quota" => WakeKind.Quota,
            _ => WakeKind.Unknown,
        };
    }
}
