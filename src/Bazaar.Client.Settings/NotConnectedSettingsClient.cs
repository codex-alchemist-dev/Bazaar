using Bazaar.Server.Settings;

namespace Bazaar.Client.Settings;

/// <summary>
/// The settings client a head gets until a wire implementation exists: nothing is listed and every write is refused with a plain
/// reason. No wire message for operator settings exists yet (doc 02 has only <c>prefs.set</c>, which is per person), so this
/// reports "not connected" instead of inventing one.
/// </summary>
public sealed class NotConnectedSettingsClient : ISettingsClient
{
    /// <summary>The one instance; the client holds no state.</summary>
    public static NotConnectedSettingsClient Instance { get; } = new();

    public Task<SettingsSnapshot> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<SettingsSnapshot>(new SettingsNotConnectedException());

    public Task<SettingsEntry?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromException<SettingsEntry?>(new SettingsNotConnectedException());

    public Task<SettingsChangeResult> SetAsync(string name, string valueJson, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SettingsChangeResult(SettingWriteOutcome.Rejected, new SettingsNotConnectedException().Message));
}
