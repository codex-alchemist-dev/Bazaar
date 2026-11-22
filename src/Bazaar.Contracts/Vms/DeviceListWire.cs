namespace Bazaar.Contracts.Vms;

/// <summary>Payload of the <c>vms.devices.list</c> request (doc 62 section 9.1). It has no fields.</summary>
public sealed record DeviceListRequest();

/// <summary>One enabled device provider with its capability report and probe result (doc 62 sections 4 and 9.1).</summary>
/// <param name="Kind">Provider kind, for example <c>android</c> or <c>microvm</c>.</param>
/// <param name="GuestOs">Guest operating system the provider runs.</param>
/// <param name="Usable">Whether the host probe passed.</param>
/// <param name="Reason">Why the provider is not usable, or null when it is.</param>
/// <param name="Isolation">Isolation class, as the abstractions spell it.</param>
/// <param name="Headless">Whether the provider runs without a display.</param>
/// <param name="Save">Whether the provider can save state.</param>
/// <param name="Checkpoint">Whether the provider can checkpoint.</param>
/// <param name="Restore">Whether the provider can restore.</param>
/// <param name="EgressEnforced">Whether the provider can make egress mandatory.</param>
/// <param name="MaxConcurrentInstances">Concurrent instance cap, or null for none.</param>
/// <param name="Running">Instances running now.</param>
/// <param name="RestartRequired">Names of install settings changed since the provider was built, which apply only after a server restart (doc 62 section 9.1), or null when none. Optional and trailing, so older clients still parse the record.</param>
public sealed record DeviceProviderInfo(
    string Kind,
    string GuestOs,
    bool Usable,
    string? Reason,
    string Isolation,
    bool Headless,
    bool Save,
    bool Checkpoint,
    bool Restore,
    bool EgressEnforced,
    int? MaxConcurrentInstances,
    int Running,
    IReadOnlyList<string>? RestartRequired = null);

/// <summary>One running device guest. It carries no agent name, port, path or screen content.</summary>
/// <param name="VmId">Guest id.</param>
/// <param name="Kind">Provider kind.</param>
/// <param name="GuestOs">Guest operating system.</param>
/// <param name="State">Lifecycle state, as the wire spells it.</param>
public sealed record DeviceGuestInfo(Guid VmId, string Kind, string GuestOs, string State);

/// <summary>Reply to <c>vms.devices.list</c>: enabled providers and running device guests.</summary>
/// <param name="Providers">Enabled providers.</param>
/// <param name="Guests">Running device guests.</param>
public sealed record DeviceListReply(IReadOnlyList<DeviceProviderInfo> Providers, IReadOnlyList<DeviceGuestInfo> Guests);
