# Modular Remote Connectivity Transports

Status: PROPOSED (2026-11-09; decisions 1, 2 and 5 recorded from owner, 2026-11-09, requested by the owner in the "Design modular remote connectivity transports" thread) · Owner questions: 2 open (3 and 4)
Depends on: 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 32-remote-access-auth-and-privacy.md · Used by: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md

## Purpose
Doc 32 settles who may connect and how a device proves it. This doc settles how a device reaches the server at all, when the server sits behind a home router, CGNAT, or a phone on mobile data. The owner's principle is "make everything modular": every reachability method is a module behind one contract, the contract is transport-agnostic, and as many modules as practical ship.

Pairing (32, section 3), the wire format (02, JSON via `IWireCodec`) and authentication (32, sections 5 and 6) do not change with the transport. A transport only answers "can a client open a TLS connection to this server, and at what address?"

## Decided inputs
- Remote access is the operator's choice. The home host accepts no inbound connections by default (32, section 1; DECISIONS-LOG round 8).
- The network is never the only gate. Every connection presents a per-device credential, whatever the transport (32, trust rule).
- Plain `ws://` is allowed only on loopback. Everything else is TLS with the server key hash pinned at pairing (32, section 2).
- No web client, ever. No transport module serves Bazaar content to a browser (CLAUDE.md).
- Wire format is JSON source-generated `System.Text.Json` behind `IWireCodec`. Adding a transport adds no codec (CLAUDE.md, owner 2026-10-25).
- The server does all compute; clients render (CLAUDE.md). Client-side connection helpers (for example a local terminal relay) are unchanged.
- Anything OS-specific sits behind a provider interface (03). Module code is platform-neutral; process launching (cloudflared) and sockets go through that same rule.
- Third-party relays and overlays are operator choices with documented terms. None is a default that puts a vendor in the plaintext path (32, section 1).

## Design

### 1. Two contracts, one registry
Reachability is a property of the server host, and a transport is how one reachability path is used. The contracts split along that line:

```csharp
// Namespace Bazaar.Server.Transports (project src/Bazaar.Server.Transports)

public enum TransportScope { Loopback, Lan, Overlay, Internet, Relay, PeerToPeer, Onion }

/// Reachability source: discovers or obtains a way in (an address, a tunnel, a mapped port).
public interface IReachabilityProvider
{
    string Id { get; }                       // stable, e.g. "cloudflared"
    TransportScope Scope { get; }
    Task<ReachabilityResult> ProbeAsync(CancellationToken ct);    // no side effects beyond sockets
    Task<IAsyncDisposable> OpenAsync(ReachabilityContext ctx, CancellationToken ct); // starts, keeps alive
}

/// A transport module: what a client sees. Wraps one provider and publishes offers.
public interface ITransportModule
{
    string Id { get; }
    string DisplayName { get; }
    TransportScope Scope { get; }
    int Priority { get; }                    // lower = preferred when several offers exist
    bool DefaultEnabled { get; }             // false for everything except LAN/mDNS
    ValueTask<TransportModuleState> StartAsync(ReachabilityContext ctx, CancellationToken ct);
    ValueTask StopAsync(CancellationToken ct);
    IAsyncEnumerable<TransportOffer> OffersAsync(CancellationToken ct);  // changes as addresses change
}
```

- `IReachabilityProvider` answers "is this path usable here, and what address does it give?" It is the piece with OS, network or third-party dependencies, and it is the piece tests fake.
- `ITransportModule` is the unit the operator enables, orders and sees in the setup wizard. Most modules wrap one provider; a module may also wrap none (the manual port-forward module is configuration plus a probe).
- `TransportOffer` is what a client receives: module id, scope, endpoint URI (`wss://…` or `ws://` on loopback), server key hash, `expires_at` when the address is temporary, and a `requires_relay` flag. The offer never contains credentials and never names a human or agent.
- `TransportRegistry` (singleton) reads the `transport.*` settings, starts enabled modules, collects offers and publishes them to the pairing QR payload (32, section 3) and to the client's "server addresses" list. Clients try offers in `Priority` order and fall back on failure.

A client's connection attempt is: try the offers in order, each with a timeout (`transport.connect_timeout_ms`), accept the first whose TLS pin and `hello` succeed. Offers are hints; authentication after TLS is unchanged (32, section 5).

### 2. Module catalogue
Modules are opt-in unless marked **default**. The priority column orders client attempts when several are live.

| Priority | Module id | Scope | Default | Dependencies | Mechanism | Who sees the server | Key trade-offs |
|---|---|---|---|---|---|---|---|
| 10 | `lan-mdns` | Lan | **default on** | none (UDP 5353 multicast) | Advertises `_bazaar._tcp.local` with a TXT record holding the server id, key hash prefix and version; serves the LAN listener | Local network only | Zero config; no outside reach; blocked on guest Wi-Fi with client isolation |
| 20 | `manual-port-forward` | Internet | off | Operator-owned router rule or static public IP | Operator enters a public host and port; the module probes it from outside through a configured probe URL and publishes the offer | Internet | Works with any router; exposes the TCP port to the internet; DDNS is an operator concern (documented, not built in) |
| 25 | `upnp-natpmp` | Internet | off | Router with UPnP IGD or NAT-PMP enabled | Requests a port mapping from the gateway (SSDP discovery, then SOAP for UPnP; NAT-PMP on UDP 5351); renews on a lease timer; removes on stop | Internet | Zero config when the router allows it; many routers disable it or map to a wrong address; security-sensitive (any LAN device can ask), so it is opt-in and the mapping is shown in the admin panel |
| 30 | `cloudflared` | Internet | off | `cloudflared` binary (operator installs or Bazaar downloads a pinned release) plus a Cloudflare account for a named tunnel | Runs `cloudflared tunnel run` (named) or `cloudflared tunnel --url` (quick tunnel) as a child process with an argument list; publishes the tunnel hostname | Cloudflare terminates TLS and sees plaintext | No inbound port; WebSocket support is verified by the module's probe; weakest self-hosted posture (32 lists it as operator-only) |
| 40 | `overlay-tailscale` | Overlay | off | Tailscale or Headscale on host and devices | Uses the host's overlay address; publishes `ws://<overlay-ip>` only when the overlay is the transport (TLS pin still applies) | Overlay control plane; data is WireGuard | No port; per-human seat on the Tailscale side; Personal plan is non-commercial (needs counsel, 32) |
| 41 | `overlay-wireguard` | Overlay | off | Operator-run WireGuard interface | Publishes the WireGuard address of the host; no daemon managed by Bazaar | none beyond the operator | One UDP port; CGNAT blocks it; most manual |
| 50 | `relay-selfhost` | Relay | off | Operator-run relay (Bazaar relay package, open source) | Outbound WebSocket from the server to the relay; the relay forwards opaque bytes between a device and the server; TLS end-to-end to the server key | Relay sees ciphertext and metadata (timing, sizes, device ids) | Works from anywhere with no inbound port; the operator runs it; the relay must never hold keys |
| 55 | `p2p-ice` | PeerToPeer | off | STUN servers (operator list; public defaults documented), a signaling path (the relay or a paired device) | Gathers host, server-reflexive (STUN) and relay candidates; establishes a direct UDP path for a QUIC connection carrying the same TLS-pinned wire | Peers exchange candidates through signaling; STUN servers see the public address | Works behind many NATs; fails under symmetric NAT (falls back to relay); needs a signaling channel first |
| 70 | `tor-onion` | Onion | off | A Tor daemon with a control port (operator-installed) | Creates an onion service that forwards to the loopback listener; publishes the `.onion` address | Tor network | Strong network anonymity and no inbound port; high latency; pinned key still required |
| 75 | `ssh-reverse` | Internet | off | An SSH server the operator controls | `ssh -R` from the host to a jump host, then a forwarded port | The jump host operator | Works through strict firewalls; a key-management burden; not recommended for mobile |
| 80 | `ngrok-style` | Internet | off | A tunnel provider account (ngrok, or any compatible agent) | Runs the operator's chosen agent as a child process; publishes its public URL | Tunnel provider sees plaintext | Quick to set up; the provider holds TLS termination; free tiers change often, so the module is generic |
| 90 | `bluetooth-local` | Lan (proximity) | off | Bluetooth LE on host and device (Windows and Android) | Pairing bootstrap only: the offer is carried over BLE; data never travels BLE | none | Good for first pairing when no network is shared; not a data path; platform APIs sit behind a provider |
| 91 | `wifi-direct-local` | Lan (proximity) | off | Wi-Fi Direct support on both ends | Same role as BLE: pairing bootstrap and, where supported, a short-lived direct data path | none | Platform support is uneven; treated as a bootstrap, not a steady-state transport |

Notes on the table:
- Modules that expose plaintext to a third party (cloudflared, ngrok-style, Tailscale's control plane) say so in the setup wizard. Where TLS is terminated by a third party, the offer is marked `TerminatesTls` and the end-to-end pin does not apply (section 3).
- **Relay and P2P are the open-source fallbacks.** The default recommendation for a device that is neither on the LAN nor on an overlay is `relay-selfhost`, with `p2p-ice` as an opt-in direct path that falls back to it.
- The owner's default remains zero-config: `lan-mdns` on, everything else off until the operator turns it on in the setup wizard or advanced settings.

### 3. Security model
- **TLS everywhere off loopback.** The server certificate comes from `ICertificateProvider` (32, section 2). Clients pin the server key hash from the pairing QR. A mismatch fails the attempt and does not fall back to a weaker offer.
- **Third-party TLS termination (decided 2026-11-09, owner via coordinator).** Where a third party ends TLS (`cloudflared`, `ngrok-style`), the offer sets `TerminatesTls`. The server key pin is still required by default: the client must verify the server key hash end to end, inside the tunnel, by running its own TLS session over the tunneled bytes. Trusting the tunnel's public CA chain alone is allowed only when the operator has set `transport.allow-unpinned-tls-termination` to true. That opt-out is off by default, is audited (15), and shows a warning in the wizard. Pin enforcement in the registry is unchanged; the client-side check is WP 12.
- **No downgrade.** A module may not publish a `ws://` offer unless its scope is `Loopback`. The registry rejects any other offer at publish time (a test covers this). A `TerminatesTls` offer is published only with its key hash; the pin is verified by the client as described above.
- **Pins travel with the server, not the address.** The pin is the server's key, so a changed address, tunnel hostname or relay does not break trust, and an attacker-controlled address does not gain trust.
- **Reachability is not identity.** Any transport can carry the 32 handshake; authentication is unchanged. Overlay or tunnel membership grants no access.
- **Relays and tunnels see only what their scope exposes.** The catalogue states who sees plaintext. Relay modules forward opaque bytes; they never hold keys, tokens or the pinned key.
- **SoulGuard (15).** Transport endpoints, mappings and tunnel credentials are secrets for Identity Service (14). Tunnel tokens and relay credentials are stored there, never in config files or logs, and never passed to agents. A transport module that needs an agent-visible value gets a redacted copy.
- **Untrusted input.** Traffic arriving through any transport is untrusted (15). Transports do not change taint handling.
- **Opening ports is audited.** `upnp-natpmp` and `manual-port-forward` record mapping creation, renewal and removal in `audit_log` (15). Opening a port or starting a tunnel is an owner-approved action, because it changes exposure.
- **Rate limits** from 32 section 7 apply to every transport's listener. A transport never raises them.

### 4. Discovery and lifecycle
1. On start, `TransportRegistry` reads `transport.*` settings and starts each enabled module in priority order. A failing module is marked `Failed` with a reason and does not stop the others.
2. A module publishes offers as they become true (for example `upnp-natpmp` after a mapping succeeds). Changes are sent to paired devices as a `transport.offers` frame on the existing socket (02), or on the next connect if no socket is open.
3. Expiring offers (leases, quick-tunnel URLs) carry `expires_at`. A module renews before expiry; if renewal fails the offer is withdrawn and the state is shown to the owner.
4. On stop, modules withdraw offers, remove mappings and stop child processes in reverse order, with a bounded timeout (03 shutdown rules).
5. Providers run as background services under the host (03). None of them need an elevated process except `upnp-natpmp` when the operator's OS requires it for raw sockets; that is a Windows provider concern behind 03.

### 5. Setup and operations
- The setup wizard asks one question: "Can your devices reach this server while they are away from home?" and lists the modules that fit the answers (no inbound port, own domain, overlay account, none). Choosing a module with a third party shows its trade-off text.
- Advanced settings show every module, its state, its last failure and its current offers. Changes are audited.
- Quick-tunnel modules (`cloudflared`, `ngrok-style`) warn that addresses change on restart and that the pin still protects the connection.

### Settings
Registered in 03's settings registry. Changes are audited (15). "Yes" means the change needs owner approval through 15's flow.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `transport.lan-mdns.enabled` | bool | true | install | owner | no |
| `transport.manual-port-forward.enabled` | bool | false | install | owner | yes, when enabled |
| `transport.manual-port-forward.public_host` | string | unset | install | owner | yes |
| `transport.manual-port-forward.probe_url` | string | unset (probe skipped, offer marked unverified) | install | owner | no |
| `transport.upnp-natpmp.enabled` | bool | false | install | owner | yes, when enabled |
| `transport.cloudflared.enabled` | bool | false | install | owner | yes, when enabled |
| `transport.allow-unpinned-tls-termination` | bool | false | install | owner | yes, when enabled; audited; warning shown |
| `transport.cloudflared.mode` | enum: `named`, `quick` | `named` | install | owner | yes, if changed to `quick` |
| `transport.cloudflared.binary_path` | string | unset (use pinned download) | install | owner | yes |
| `transport.overlay-tailscale.enabled` | bool | false | install | owner | yes, when enabled |
| `transport.relay.url` | string | unset | install | owner | yes |
| `transport.relay-selfhost.enabled` | bool | false | install | owner | yes, when enabled (default fallback for devices off the LAN and off an overlay, decided 2026-11-09) |
| `transport.p2p-ice.enabled` | bool | false | install | owner | yes, when enabled |
| `transport.p2p-ice.stun_servers` | list | operator list (documented public servers) | install | owner | no |
| `transport.tor.enabled` | bool | false | install | owner | yes, when enabled |
| `transport.connect_timeout_ms` | int | 4000 | install | owner | no |
| `transport.offer_refresh_seconds` | int | 60 | install | owner | no |

Shipped floors that are not settings: no `ws://` offer off loopback; no module holds server keys; no module may raise the 32 rate limits; no transport is enabled by a device or agent.

## Data and interfaces
- Config is stored in the Settings table (03). No new SQL table is required for v1. Offer state is in memory and reported over 02.
- Settings keys use the module id as written in the catalogue: `transport.<module-id>.enabled`, with sub-keys such as `transport.manual-port-forward.public_host` (the registry reads `transport.<id>.enabled`).
- `transport_mapping(id, module_id, external_addr, internal_port, created_at, renew_at, removed_at)` is proposed for audit of port mappings only. Owned here; the migration number is assigned at implementation time from 04's list.
- The offer frame: `{ "module": "lan-mdns", "scope": "lan", "uri": "wss://192.168.1.20:7443/ws", "keyHash": "sha256:…", "expiresAt": null, "requiresRelay": false, "verified": true, "terminatesTls": false }`. Versioned under 02's envelope.

```csharp
public sealed record TransportOffer(string ModuleId, TransportScope Scope, Uri Uri, string ServerKeyHash,
    DateTimeOffset? ExpiresAt, bool RequiresRelay, bool Verified, bool TerminatesTls = false);
public sealed record ReachabilityResult(bool Usable, string Reason, IReadOnlyList<Uri> Candidates);
public sealed record ReachabilityContext(int LocalPort, ITransportSettings Settings, TimeProvider Time, ILogger Logger);
public enum TransportModuleState { Disabled, Starting, Running, Degraded, Failed }
```

## Behaviour and edge cases
- Two modules offer the same server: the client tries the lower priority number first and falls back on timeout.
- Client on a different network than the LAN offer: the LAN offer fails with a timeout; the next offer is tried. The LAN timeout is short (`transport.connect_timeout_ms`) so a roaming phone does not stall.
- Address changes (DHCP, quick tunnel restart): the module publishes a new offer; the client reconnects with the new one after the old socket closes. Pins do not change.
- Relay down: `relay-selfhost` is marked `Degraded`; other modules keep working.
- Symmetric NAT: `p2p-ice` cannot find a direct path; it reports `Failed` with reason `no-direct-path` and the relay fallback applies.
- Mapping refused by the router: `upnp-natpmp` reports `Failed`; no retries faster than once per 5 minutes.
- Tor or SSH unavailable on the host: the module reports `Failed`; no partial state is left behind.

## Testing
Each module has unit tests against fakes; a shared contract suite runs against every module.
- **Contract suite (all modules):** start and stop are idempotent; a failed start reports `Failed` and leaves no offers; offers never include `ws://` unless scope is `Loopback`; stop withdraws every offer within the timeout; no offer contains a credential.
- **Registry:** priority order; a failing module does not stop others; a `ws://` offer is rejected at publish; unknown module ids are rejected at config load.
- **`lan-mdns`:** the mDNS packet encoder and parser round-trip the service record; the responder answers a PTR query with the expected TXT; a fake multicast socket is used in CI.
- **`manual-port-forward`:** a probe URL that returns the server id marks the offer `Verified`; a timeout leaves it unverified and still publishes it with a warning; an invalid host is rejected.
- **`upnp-natpmp`:** SSDP response and SOAP mapping parsed from captured fixtures; NAT-PMP packet encode and decode; lease renewal timer with a fake `TimeProvider`.
- **`cloudflared`:** the child process is started with an argument list (no shell); quick-tunnel URL parsed from fixture output; process exit marks the module `Degraded` and restarts with backoff; the tunnel token is never written to logs.
- **`p2p-ice` (STUN):** a Binding Request encodes the magic cookie and transaction id; a fixture Binding Success Response yields the XOR-MAPPED-ADDRESS for IPv4 and IPv6; a response with a wrong transaction id is ignored; a timeout returns no reflexive candidate.
- **Pins:** a client refuses an offer whose pin does not match, and does not fall back to another offer with a different pin.
- Real sockets are used only for loopback tests (the listener and the STUN parser against a local fake server).

## Work packages
1. Contracts and `TransportRegistry` in `src/Bazaar.Server.Transports`, with the shared contract suite (this change set).
2. `lan-mdns` module: mDNS packet codec, responder, offer publication (this change set).
3. `manual-port-forward` module: configuration and probe (this change set).
4. `cloudflared` module: process wrapper, quick and named modes, URL parsing (this change set).
5. `p2p-ice` STUN candidate gathering (this change set). Signaling and the QUIC data path follow after the relay (WP 7).
6. `upnp-natpmp` module (next wave).
7. `relay-selfhost` package and client (next wave; needs 02 transport framing and 14 credential storage).
8. `overlay-tailscale` and `overlay-wireguard` modules (next wave).
9. `tor-onion`, `ssh-reverse`, `ngrok-style` modules (later; opt-in, each with its trade-off text).
10. `bluetooth-local` and `wifi-direct-local` pairing bootstrap providers behind the client platform layer (later; needs 29 and 28 for platform APIs).
11. Setup wizard and advanced settings pane (needs 27).

## Conflicts
- 32 section 1 lists five options; this doc adds the catalogue. 32 remains the source for trust, pairing and sign-in. Wording in 32 that calls Cloudflare Tunnel "operator option only" is kept and repeated here.
- 02 says "the socket is the only channel"; offer delivery (`transport.offers`) is a frame on that socket, so the statement holds.
- CLAUDE.md "No web client, ever" is unaffected: no module serves a browser client.
- `p2p-ice` and `relay-selfhost` need a QUIC or WebSocket framing choice that 02 does not yet fix. The P2P data path stays unimplemented until 02 settles it.

## Questions for the owner
1. DECIDED 2026-11-09 (owner via coordinator): `relay-selfhost` is the default fallback for a device that is neither on the LAN nor on an overlay. It is open source and self-hostable, and it is enabled by the operator.
2. Should `upnp-natpmp` and `manual-port-forward` need a separate owner approval per mapping, or once per install? Default: once per install, audited.
3. Which public STUN servers ship as the default list? Default: operator-editable list, no vendor pinned in code.
4. Is the Bluetooth and Wi-Fi Direct bootstrap in scope for v1.0, or a post-1.0 module? Default: post-1.0.
5. DECIDED 2026-11-09 (owner via coordinator): TLS-terminated offers keep the server key pin by default. Dropping the pin is an explicit per-install opt-out (`transport.allow-unpinned-tls-termination`), off by default, audited, with a warning.
