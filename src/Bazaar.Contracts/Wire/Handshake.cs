namespace Bazaar.Contracts.Wire;

/// <summary>A client's position in one stream: the last <c>n</c> and <c>ev</c> it applied.</summary>
/// <param name="S">Stream name.</param>
/// <param name="After">Last <c>n</c> applied.</param>
/// <param name="EditAfter">Last <c>ev</c> applied.</param>
public sealed record StreamCursor(string S, long After, long EditAfter);

/// <summary>First message from a client (unless the credential rode in the upgrade header).</summary>
/// <param name="VersionMin">Lowest protocol major the client speaks.</param>
/// <param name="VersionMax">Highest protocol major the client speaks.</param>
/// <param name="Minor">Protocol minor the client implements at <paramref name="VersionMax"/>.</param>
/// <param name="Codecs">Codec names the client can decode, preferred first, such as <c>json</c>.</param>
/// <param name="DeviceId">Stable device id.</param>
/// <param name="DeviceClass">Device class the server uses to pick a UI profile, such as <c>windows</c> or <c>android</c>.</param>
/// <param name="ClientBuild">Client build string for diagnostics and upgrade checks.</param>
/// <param name="Credential">Credential, when it did not ride in the upgrade header.</param>
/// <param name="Cursors">Cursors for streams to resume, or null.</param>
public sealed record Hello(
    int VersionMin,
    int VersionMax,
    int Minor,
    IReadOnlyList<string> Codecs,
    string DeviceId,
    string DeviceClass,
    string ClientBuild,
    string? Credential,
    IReadOnlyList<StreamCursor>? Cursors);

/// <summary>Limits the server announces in <see cref="Welcome"/>; the client stays inside them.</summary>
/// <param name="MaxFrameBytes">Largest inbound frame.</param>
/// <param name="InflightRequestsMax">Concurrent requests allowed.</param>
/// <param name="ReplayPageSize">Entries per replay page.</param>
public sealed record WireLimits(int MaxFrameBytes, int InflightRequestsMax, int ReplayPageSize);

/// <summary>Server answer to <see cref="Hello"/>.</summary>
/// <param name="Version">Chosen protocol major.</param>
/// <param name="Minor">Server's protocol minor.</param>
/// <param name="Codec">Chosen codec name.</param>
/// <param name="PrincipalId">Authenticated principal.</param>
/// <param name="ServerTime">Server clock at welcome; clients never use local time for ordering.</param>
/// <param name="HeartbeatIntervalS">Server ping interval in seconds.</param>
/// <param name="HeartbeatTimeoutS">Silence, in seconds, after which the socket closes.</param>
/// <param name="Limits">Server limits.</param>
/// <param name="Capabilities">Capability flags; clients ignore ones they do not know.</param>
/// <param name="UiRevision">Current revision of the device's UI profile.</param>
/// <param name="UpgradeRequired">Set when no version overlaps; the socket then closes with 4426.</param>
/// <param name="MinimumBuild">With <paramref name="UpgradeRequired"/>: the lowest acceptable client build.</param>
public sealed record Welcome(
    int Version,
    int Minor,
    string Codec,
    Guid PrincipalId,
    DateTimeOffset ServerTime,
    int HeartbeatIntervalS,
    int HeartbeatTimeoutS,
    WireLimits Limits,
    IReadOnlyList<string> Capabilities,
    long UiRevision,
    bool? UpgradeRequired,
    string? MinimumBuild);
