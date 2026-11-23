using Bazaar.Server.Settings;

namespace Bazaar.Conduit.Settings;

/// <summary>How the <c>cloudflared</c> module runs: a named tunnel on an operator domain, or a quick tunnel with a changing address.</summary>
public enum CloudflaredMode
{
    /// <summary>A named tunnel with a registered domain (the recommended default).</summary>
    Named,

    /// <summary>A quick tunnel: no account, address changes on every start.</summary>
    Quick,
}

/// <summary>
/// The Conduit keys of docs/design/39-conduit-remote-connectivity.md (settings table). All are install scope and changed by the owner.
/// Anything that opens exposure (a tunnel, a port, a relay, a changed binary) needs Tier 2 approval, because opening a port or starting a
/// tunnel is an owner-approved action (doc 39 security notes). Turning a module off, or tightening, never waits. Tunnel tokens are secrets
/// (doc 14) and are not settings. The table's mixed-case <c>Conduit.relay.url</c> and <c>Conduit.tor.enabled</c> are spelled lower-case here,
/// like every other key.
/// </summary>
public static class ConduitSettings
{
    /// <summary>The setup wizard writes as the operator and the owner approves, so both roles may change a Conduit key (like the status bind).</summary>
    private static readonly SettingRole _ownerOrOperator = SettingRole.Owner | SettingRole.Operator;

    private const string _timeoutName = "conduit.connect_timeout_ms";
    private const string _refreshName = "conduit.offer_refresh_seconds";

    /// <summary><c>conduit.lan.enabled</c>: LAN discovery (doc 39 module <c>lan-mdns</c>).</summary>
    public static readonly SettingKey<bool> LanEnabled = Flag("conduit.lan.enabled", true, false, "Offer a LAN address to clients on the same network.");

    /// <summary><c>conduit.port-forward.enabled</c>.</summary>
    public static readonly SettingKey<bool> PortForwardEnabled = Flag("conduit.port-forward.enabled", false, true, "Publish an operator-configured public host and port.");

    /// <summary><c>conduit.port-forward.public_host</c>.</summary>
    public static readonly SettingKey<string> PortForwardPublicHost = Text("conduit.port-forward.public_host", "The public host name or address of the operator's port forward.", HostCheck);

    /// <summary><c>conduit.port-forward.probe_url</c>: where the module checks the forward from outside.</summary>
    public static readonly SettingKey<string> PortForwardProbeUrl = new(
        "conduit.port-forward.probe_url", string.Empty, SettingScope.Install, _ownerOrOperator,
        "An https URL that returns the server id, used to mark a port-forward offer verified. Empty skips the probe.",
        validate: value => value.Length == 0 || IsHttpsUrl(value) ? null : "must be empty or an https URL.");

    /// <summary><c>conduit.upnp-natpmp.enabled</c>.</summary>
    public static readonly SettingKey<bool> UpnpNatpmpEnabled = Flag("conduit.upnp-natpmp.enabled", false, true, "Open the port on the router with UPnP or NAT-PMP.");

    /// <summary><c>conduit.cloudflared.enabled</c>.</summary>
    public static readonly SettingKey<bool> CloudflaredEnabled = Flag("conduit.cloudflared.enabled", false, true, "Reach the server through a Cloudflare tunnel. Cloudflare ends TLS and sees plaintext.");

    /// <summary><c>conduit.cloudflared.mode</c>: <c>named</c> or <c>quick</c>.</summary>
    public static readonly SettingKey<CloudflaredMode> CloudflaredModeKey = new(
        "conduit.cloudflared.mode", CloudflaredMode.Named, SettingScope.Install, _ownerOrOperator,
        "named: a tunnel on the operator's registered domain. quick: no account, and the address changes on every start.",
        ApprovalTier.Tier2,
        requiresApprovalFor: mode => mode == CloudflaredMode.Quick);

    /// <summary><c>conduit.cloudflared.hostname</c>: the public host of a named tunnel.</summary>
    public static readonly SettingKey<string> CloudflaredHostname = Text("conduit.cloudflared.hostname", "The public host name of the named Cloudflare tunnel.", HostCheck);

    /// <summary><c>conduit.cloudflared.binary_path</c>.</summary>
    public static readonly SettingKey<string> CloudflaredBinaryPath = Text("conduit.cloudflared.binary_path", "Path of the cloudflared program. Empty uses the pinned download.", null);

    /// <summary><c>conduit.allow-unpinned-tls-termination</c>: lets a client trust a tunnel's public CA instead of the server key pin.</summary>
    public static readonly SettingKey<bool> AllowUnpinnedTlsTermination = Flag(
        "conduit.allow-unpinned-tls-termination", false, true,
        "Let clients trust a tunnel's public certificate instead of checking the server key end to end. Audited; the wizard warns.");

    /// <summary><c>conduit.overlay-tailscale.enabled</c>.</summary>
    public static readonly SettingKey<bool> OverlayTailscaleEnabled = Flag("conduit.overlay-tailscale.enabled", false, true, "Offer the server's Tailscale address.");

    /// <summary><c>conduit.relay.enabled</c>.</summary>
    public static readonly SettingKey<bool> RelayEnabled = Flag("conduit.relay.enabled", false, true, "Use the operator relay for devices off the LAN and off an overlay.");

    /// <summary><c>conduit.relay.url</c>.</summary>
    public static readonly SettingKey<string> RelayUrl = new(
        "conduit.relay.url", string.Empty, SettingScope.Install, _ownerOrOperator,
        "The wss URL of the operator relay.", ApprovalTier.Tier2,
        validate: value => value.Length == 0 || value.StartsWith("wss://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out _)
            ? null
            : "must be empty or a wss:// URL.");

    /// <summary><c>conduit.p2p.enabled</c>.</summary>
    public static readonly SettingKey<bool> P2pEnabled = Flag("conduit.p2p.enabled", false, true, "Try a direct peer-to-peer path through NAT with STUN.");

    /// <summary><c>conduit.p2p.stun_servers</c>.</summary>
    public static readonly SettingKey<IReadOnlyList<string>> P2pStunServers = new(
        "conduit.p2p.stun_servers", [], SettingScope.Install, _ownerOrOperator,
        "STUN servers for peer-to-peer, as host:port. The operator picks documented public servers.",
        validate: servers => servers.All(s => s.Contains(':', StringComparison.Ordinal) && s.Length > 2) ? null : "each entry must be host:port.");

    /// <summary><c>conduit.tor.enabled</c>.</summary>
    public static readonly SettingKey<bool> TorEnabled = Flag("conduit.tor.enabled", false, true, "Offer an onion address.");

    /// <summary><c>conduit.connect_timeout_ms</c>: how long a client waits for one path before trying the next.</summary>
    public static readonly SettingKey<int> ConnectTimeoutMs = new(
        _timeoutName, 4000, SettingScope.Install, _ownerOrOperator, "Milliseconds a client waits for one offer before trying the next.",
        validate: value => value is >= 250 and <= 60000 ? null : "must be 250 to 60000.");

    /// <summary><c>conduit.offer_refresh_seconds</c>: how often offers are re-published.</summary>
    public static readonly SettingKey<int> OfferRefreshSeconds = new(
        _refreshName, 60, SettingScope.Install, _ownerOrOperator, "Seconds between offer refreshes.",
        validate: value => value is >= 5 and <= 3600 ? null : "must be 5 to 3600.");

    /// <summary>Every Conduit key.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        LanEnabled, PortForwardEnabled, PortForwardPublicHost, PortForwardProbeUrl, UpnpNatpmpEnabled,
        CloudflaredEnabled, CloudflaredModeKey, CloudflaredHostname, CloudflaredBinaryPath, AllowUnpinnedTlsTermination,
        OverlayTailscaleEnabled, RelayEnabled, RelayUrl, P2pEnabled, P2pStunServers, TorEnabled,
        ConnectTimeoutMs, OfferRefreshSeconds,
    ];

    /// <summary>A switch. Turning it on needs Tier 2 approval when <paramref name="exposes"/>; turning it off never waits.</summary>
    private static SettingKey<bool> Flag(string name, bool defaultValue, bool exposes, string description) =>
        new(name, defaultValue, SettingScope.Install, _ownerOrOperator, description,
            exposes ? ApprovalTier.Tier2 : ApprovalTier.None,
            requiresApprovalFor: exposes ? enabled => enabled : null);

    /// <summary>A text setting, empty by default. A non-empty value needs Tier 2 approval, because it changes what is exposed.</summary>
    private static SettingKey<string> Text(string name, string description, Func<string, string?>? check) =>
        new(name, string.Empty, SettingScope.Install, _ownerOrOperator, description, ApprovalTier.Tier2,
            validate: check, requiresApprovalFor: value => value.Length > 0);

    private static string? HostCheck(string value) =>
        value.Length == 0 || Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? null
            : "must be empty or a valid host name or address.";

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>Registers the Conduit keys with the settings registry, so the setup plan, wizard and admin panel know them.</summary>
public sealed class ConduitSettingContribution : ISettingContribution
{
    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> Settings => ConduitSettings.All;
}
