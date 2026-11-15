namespace Bazaar.Client.Core.Layout;

/// <summary>
/// Chooses the layout a head shows for the current device class, display and posture, and turns it into a placed tree.
/// Pure: the head passes in whether a pane kind is registered (for example <c>registry.Contains</c>), so no
/// Avalonia type reaches this code. Falls back to the company's baked default when the effective document has no
/// matching slot (doc 41 section 4.8: no round trip, no server needed).
/// </summary>
public static class LayoutSelection
{
    /// <returns>The placed tree, or null when neither the effective layout nor the baked default has a matching slot.</returns>
    public static PlacedNode? Place(
        LayoutDocument? effective,
        LayoutDocument? bakedDefault,
        string deviceClass,
        string display,
        string posture,
        Func<string, bool> isKnownPaneKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(display);
        ArgumentException.ThrowIfNullOrWhiteSpace(posture);
        ArgumentNullException.ThrowIfNull(isKnownPaneKind);

        LayoutSlot? slot = effective is null ? null : LayoutResolver.Select(effective, deviceClass, display, posture);
        slot ??= bakedDefault is null ? null : LayoutResolver.Select(bakedDefault, deviceClass, display, posture);
        return slot is null ? null : LayoutTree.Build(slot.Root, isKnownPaneKind);
    }
}
