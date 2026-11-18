using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync;

/// <summary>A server-held control that cannot change while offline, with the reason every head shows.</summary>
/// <param name="ControlId">Stable id for the control.</param>
/// <param name="Name">Display name.</param>
/// <param name="Reason">Why it is disabled offline, shown as is.</param>
public sealed record OfflineDisabledControl(string ControlId, string Name, string Reason);

/// <summary>
/// The offline write policy (doc 52 section 6, doc 49 section 4). Settings, company locks, agent memory, Passport and secrets are
/// held by the server and disabled offline. Every data class in <see cref="OfflineDataClass"/> is writable offline.
/// </summary>
public static class OfflinePolicy
{
    /// <summary>The server-held controls disabled offline, in display order.</summary>
    public static IReadOnlyList<OfflineDisabledControl> DisabledControls { get; } =
    [
        new("settings", "Settings", "Settings are held by the server and cannot change while offline. Reconnect to edit them."),
        new("company-locks", "Company locks", "Company locks are enforced by the server. Reconnect to change them."),
        new("agent-memory", "Agent memory", "Agent memory is written only by the server. Reconnect to edit it."),
        new("passport", "Passport", "Passport access and account profiles are managed by the server. Reconnect to change them."),
        new("secrets", "Secrets", "Secrets are injected by the server and cannot be changed offline."),
    ];

    private static readonly Dictionary<string, string> _reasonsById = DisabledControls.ToDictionary(c => c.ControlId, c => c.Reason, StringComparer.Ordinal);

    /// <summary>Whether a write of this data class may be queued offline. True for every defined class.</summary>
    public static bool IsWritableOffline(OfflineDataClass dataClass) => Enum.IsDefined(dataClass);

    /// <summary>The reason a control is disabled offline, or null when the control is not in <see cref="DisabledControls"/>.</summary>
    /// <param name="controlId">The control id, as in <see cref="OfflineDisabledControl.ControlId"/>.</param>
    public static string? DisabledReason(string controlId)
    {
        ArgumentNullException.ThrowIfNull(controlId);
        return _reasonsById.TryGetValue(controlId, out var reason) ? reason : null;
    }
}
