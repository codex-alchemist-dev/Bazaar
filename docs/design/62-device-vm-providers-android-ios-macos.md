# Optional VM providers: microVM, Android, iOS and macOS

Status: PROPOSED (owner request, 2026-11-19). Nothing here is DECIDED until the owner answers the questions in section 12. Owner answer (card, 2026-11-19): Android only for now. The iOS, macOS and Mac node parts (sections 3, 5.2, 5.3, 6 and work packages 5 to 8) are PARKED until the owner has a Mac; they stay as design only and are not built. microVM and Android are built first. Platform facts carry a tag: SOURCED (vendor documentation), MEASURED (this repo's notes) or UNVERIFIED (needs the spike in section 11). Apple licensing statements are a reading of public terms, not legal advice; counsel reviews them before any public release.
Depends on: `16-agent-vms-and-environments.md` (VM contract, lifecycle, settings), `50-windows-sandbox-provider.md` (provider capability report, host-compatibility metadata), `03-server-host-and-providers.md` (provider selection), `59-desktop-density-and-guest-display.md` (display sources, admission governor), `15-security-taint-and-approvals.md`, `42-deps-as-modules.md` (toolchains as Deps), `45-onboarding.md` (setup wizard) · Used by: `17-coding-agent-harness.md`, `26-fun-systems.md` (Playtest), `20-engine-integration-api.md`

## 1. Purpose and short answers
The owner asked three things on 2026-11-19: are VMs modules, do the providers (Hyper-V, Windows Sandbox, microVM and so on) still exist, and can Android, iOS and macOS guests be separate, optional modules that the server owner or installer picks, and that cost nothing until used. They matter for development: an agent that builds a mobile app needs somewhere to run it.

**Are VMs modules? Yes, one module per provider.** `Bazaar.Server.Vms` holds the shared contract and lifecycle (`IVmBackend`, state machine, golden images, environments). Each provider is its own project behind that contract:

| Provider | Project | Host | Status |
|---|---|---|---|
| Hyper-V | Hyper-V backend module | Windows | PR #688, not merged |
| KVM/QEMU | `Bazaar.Server.Vms.Kvm` | Linux | PR #840, not merged |
| Windows Sandbox | `Bazaar.Server.Vms.WindowsSandbox` (doc 50) | Windows | merged; not registered in any host composition |
| microVM (Firecracker, Cloud Hypervisor) | `Bazaar.Server.Vms.MicroVm` (this doc) | Linux with KVM | PROPOSED |
| Android emulator | `Bazaar.Server.Vms.Android` (this doc) | Windows, Linux, macOS | PROPOSED |
| iOS Simulator | `Bazaar.Server.Vms.Ios` (this doc) | macOS only | PROPOSED |
| macOS VM | `Bazaar.Server.Vms.MacOs` (this doc) | Apple silicon Mac only | PROPOSED |

**Other VM modules around them** (not providers): `Bazaar.Server.VmDisplay` (screenshots and input, doc 59), `Bazaar.Server.VmDensity` (admission governor, doc 59), mounts, egress and guest transport (doc 16 WP7 to WP9).

## 2. Scope
- Four new optional provider modules (microVM, Android, iOS, macOS), one shared abstractions project for the device three, and one client module. None is on by default.
- Doc 16's topology is unchanged: every agent still gets one Windows VM and one Linux VM. A device guest is an **extra** guest an environment requests on demand, for example "run this APK" or "run this app on iOS".
- This doc does not change `host.provider.vm`. That setting picks the provider for the Windows and Linux pair. Device providers are enabled by their own setting (section 7).
- This doc does not make Bazaar run macOS or iOS on anything but Apple hardware, and does not ship, document or support any workaround for that.

## 3. Apple hardware rule (plain statement)
Apple's macOS software licence allows macOS to run in a virtual machine only on Apple-branded hardware, with a limit on the number of virtual instances per Mac (two at the time of writing; the Virtualization framework enforces the same cap on Apple silicon, SOURCED secondary, UNVERIFIED on the target Mac). Xcode and the iOS Simulator are licensed for use on Apple-branded hardware. So:

1. The iOS and macOS modules run **only on a Mac**. They declare host requirements `os: macos` and refuse any other host at install and selection time (doc 50 section 5).
2. A Windows or Linux Bazaar server that wants iOS or macOS guests needs a **Mac node** (section 6). There is no emulation path and none will be built.
3. The setup wizard says this in one sentence before it offers either module, and the module page repeats it.
4. Counsel confirms the instance cap and the "software development and testing" wording for a company running these on the owner's own Mac before the first public release. Operators are responsible for their own licence compliance; the module only enforces the technical cap.

## 4. The shared abstraction: `Bazaar.Server.Vms.Devices.Abstractions`
Android, iOS and macOS fit the doc 16 lifecycle but differ from the Windows and Linux pair in four ways: the parent is an SDK system image, a simulator runtime or a restore image rather than a Bazaar golden disk; some have no guest agent; some are not isolated VMs at all; and the host may be another machine. One small abstractions project holds what the three share and nothing else:

```csharp
public enum DeviceGuestOs { Android, Ios, MacOs }

public sealed record DeviceProviderCapabilities(
    DeviceGuestOs GuestOs,
    IsolationLevel Isolation,          // HardwareVirtualised | HostProcess
    bool Save, bool Checkpoint, bool Restore,
    bool Headless,
    int MaxConcurrentInstances,
    bool GuestAgent,                   // Bazaar.Guest.Agent runs inside
    bool EgressEnforced,               // per-agent egress proxy can be made mandatory (doc 16 egress)
    bool ParentContentPinned,
    bool ScreenSource,                 // can supply frames to IGuestDisplay (doc 59)
    bool RemoteNode);                  // runs on a Mac node, not the primary server

public interface IDeviceControl            // what a provider offers beyond IVmBackend
{
    Task InstallAppAsync(VmHandle vm, DeviceAppPackage package, CancellationToken ct);   // APK, .app
    Task LaunchAppAsync(VmHandle vm, string appId, CancellationToken ct);
    Task<DeviceLogSlice> ReadLogAsync(VmHandle vm, DeviceLogQuery query, CancellationToken ct);
    Task<DeviceScreen> CaptureAsync(VmHandle vm, CancellationToken ct);                   // PNG
    Task SendInputAsync(VmHandle vm, DeviceInput input, CancellationToken ct);            // tap, key, text
}
```

- Each provider **also implements `IVmBackend`**, so the lifecycle state machine, per-VM locks, audit and reconciler of doc 16 apply unchanged. Operations a provider cannot do throw `NotSupportedException` and the lifecycle records nothing for a refused call (the doc 50 rule).
- `VmOs` gains `Android`, `Ios` and `MacOs` (additive). Existing `switch` expressions on `VmOs` must stay exhaustive; the architecture tests catch a missed one. `VmSpec` gains an optional `DeviceProfile` (device model, API level or runtime, screen size). The `vm.windows.*` and `vm.linux.*` sizing settings are unaffected.
- `IDeviceControl` is typed. No provider takes a shell string from a model. Each call builds a fixed argv from validated fields (package id, file path inside the agent's workspace share, key codes), the same rule as doc 16's exec path and doc 59's guest tools.
- The capability report is what the status page, the lifecycle service and the density governor read. Every value is backed by tested behaviour (doc 50 section 2).

## 5. The three providers

### 5.0 microVM (`Bazaar.Server.Vms.MicroVm`)
A Linux guest provider for many small, fast, disposable VMs, on a **Linux host with KVM**. It is a pair-style provider, not a device: it implements `IVmBackend` for `VmOs.Linux` only and does not use `IDeviceControl`. It is the optional "dense Linux" alternative to the KVM/QEMU module (#840).

| Item | Design |
|---|---|
| Monitors | Firecracker or Cloud Hypervisor, chosen by `vms.microvm.monitor` (default `firecracker`). One module, one `IMicroVmMonitor` seam with two adapters. Both are driven through their REST API over a per-VM Unix socket, never a shell string. |
| Host | Linux x64 or arm64 with `/dev/kvm`; host requirement feature `kvm` plus `microvm`. Not Windows, not macOS. |
| Parent | A kernel image and a root filesystem built from the doc 16 golden recipe, sealed and hash-pinned. A child is a copy-on-write file (reflink where the host file system has it, otherwise a sparse copy). `ParentContentPinned = true`. |
| Guest agent | Yes. `Bazaar.Guest.Agent` over `AF_VSOCK`, as doc 16 uses for Linux guests. |
| Save, checkpoint, restore | Snapshot and restore are supported by both monitors; `Save`, `Checkpoint` and `Restore` are true once the spike confirms them. UNVERIFIED. |
| Display | No graphics device, so no framebuffer. The doc 59 `guest` and `browser` sources work; the `hypervisor` source does not. `Headless = true`. |
| Isolation | Hardware-virtualised, with the monitor itself confined by its jailer or seccomp profile and run as an unprivileged user. |
| Egress | Per-VM tap device in its own network namespace, so `EgressEnforced` can be true on Linux; the egress module (#796) supplies the proxy. UNVERIFIED until the spike. |
| Limits | Linux guests only; no Windows guest, no GPU, no desktop session. |

### 5.1 Android (`Bazaar.Server.Vms.Android`)
Runs the Android Emulator from the Android SDK on the **primary server**. No Mac and no node.

| Item | Design |
|---|---|
| Host | Windows (x64) with the Windows Hypervisor Platform feature, Linux (x64) with `/dev/kvm`, macOS (Apple silicon, Hypervisor.framework). SOURCED (Android emulator acceleration docs). Windows Hypervisor Platform works alongside Hyper-V; the provider verifies it at probe, UNVERIFIED on the owner's PC. |
| Toolchain | A Deps module (doc 42) or an operator-supplied SDK. The provider needs `sdk_root` and `avd_home` as explicit settings and passes `ANDROID_HOME`, `ANDROID_SDK_ROOT` and `ANDROID_AVD_HOME` to every child process. It never relies on the user-profile default, so nothing is written under a home or system drive the operator did not choose. On the owner's PC the values are `V:\TOOL\android-sdk` and `V:\TOOL\android-user`. The provider refuses to start with either unset. |
| SDK licence | `sdkmanager --licenses` is a human acceptance, recorded once with the accepting person and the licence hashes in the audit log. The provider never accepts on anyone's behalf. |
| Parent | An SDK system image (for example `system-images;android-<api>;google_apis;x86_64`). Pinned by its package id, revision and the SHA-256 of its files; `ParentContentPinned = true`. A child is an AVD created with `avdmanager` from that image. |
| Control | `emulator -avd <name> -no-window -no-audio -no-boot-anim -port <p>` with a per-VM loopback port pair, and one **private adb server per VM** (`ANDROID_ADB_SERVER_PORT`) so one agent can never see another's emulator. The emulator console auth token stays in the per-VM home. |
| Headless | Yes (`-no-window`). |
| Save, checkpoint, restore | Emulator snapshots (`-snapshot`, `avd snapshot`). `Save = true`, `Checkpoint = true`, `Restore = true`. UNVERIFIED for the chosen images. |
| Guest agent | No. The channel is adb. `GuestAgent = false`; `IDeviceControl` maps to `adb install`, `am start`, `logcat`, `exec-out screencap -p` and `input`. |
| Display | `ScreenSource = true` through `screencap` and `input`; reported to `IGuestDisplay` as a `device` source (a follow-up edit to doc 59, section 3). |
| Concurrency | An operator setting, default 2. Emulator memory is the cost; the density governor (doc 59 section 8) treats Android guests like any VM. |
| Egress | `EgressEnforced = false` until the spike proves it. `-http-proxy` covers HTTP only, so it is not a boundary. See section 8. |

### 5.2 iOS Simulator (`Bazaar.Server.Vms.Ios`)
Drives `xcrun simctl` on a **Mac**. The iOS Simulator is **not a virtual machine**: simulated apps are macOS processes running against a simulated iOS runtime, and share the host kernel, file system and user account.

| Item | Design |
|---|---|
| Host | macOS with Xcode and its command line tools. Apple silicon or Intel. |
| Isolation | `Isolation = HostProcess`, reported plainly. The module therefore runs the whole Mac node as a dedicated unprivileged macOS user that holds nothing but simulator state and a workspace share. Never the owner's login account. |
| Parent | A simulator runtime (`xcrun simctl runtime list`), pinned by identifier and build. A child is a simulator device created with `simctl create` from a device type and a runtime; `simctl clone` where available (UNVERIFIED). |
| Control | `simctl boot`, `shutdown`, `install`, `launch`, `spawn`, `io <device> screenshot`, `terminate`, `delete`, `get_app_container`. `IDeviceControl` maps to these. Headless: yes, no Simulator window is opened. |
| Save, checkpoint, restore | Not supported by `simctl` as a supported call; the provider reports `Save = false`. A "restore" boots a cloned device. UNVERIFIED. |
| Guest agent | No (nothing runs inside the simulated OS). |
| Limits | Cannot run an unmodified App Store binary, only apps built for the simulator. No real push notification, camera, Face ID or most hardware. The module's page lists this. |
| Concurrency | An operator setting, default 2; memory is the limit. The Apple VM-instance cap does not apply to the Simulator. |

### 5.3 macOS VM (`Bazaar.Server.Vms.MacOs`)
Runs macOS guests with Apple's Virtualization framework on an **Apple silicon Mac**.

| Item | Design |
|---|---|
| Host | macOS 13 or newer on Apple silicon. (Intel guests are out of scope; UNVERIFIED which framework features need which OS version.) |
| Isolation | `Isolation = HardwareVirtualised`. |
| Parent | A macOS restore image (IPSW) fetched by `VZMacOSRestoreImage`, installed once into a sealed golden image (the doc 16 recipe, hash and read-only rules apply). A child is an APFS clone of the disk image (`clonefile`), so differencing needs no extra feature. UNVERIFIED on the target Mac. |
| Control | A small signed helper executable in the node, built against `Virtualization.framework` and carrying the `com.apple.security.virtualization` entitlement. Whether ad hoc signing is enough on the operator's own Mac, or a developer certificate is needed, is UNVERIFIED (spike). The helper is the only code that touches the framework; the module talks to it over a typed local channel. |
| Guest agent | Yes: `Bazaar.Guest.Agent` published for `osx-arm64` and baked into the golden image, on a virtio socket device (`VZVirtioSocketDevice`), the same role `AF_VSOCK` plays on Linux. |
| Save, checkpoint, restore | Saved machine state exists in the framework for recent macOS releases. UNVERIFIED for macOS guests; the report says `Save = false` until the spike passes. |
| Display | Through the framework's framebuffer or the guest's own capture tool (`screencapture`); `ScreenSource = true` once the spike passes. |
| Concurrency | **2**, hard cap (section 3). The setting may lower it, never raise it. |
| First boot | The macOS Setup Assistant needs a one-time human or scripted completion when the golden image is built. The recipe records how; this cannot be skipped. UNVERIFIED how much can be automated. |

## 6. Mac nodes
A Windows or Linux primary server cannot run section 5.2 or 5.3. A **Mac node** is the answer: a second, headless Bazaar process on a Mac that exists only to host these providers.

- **Process.** The node is the same one OS-agnostic server host (CLAUDE.md, "one OS-agnostic .NET build") started with `--role node`, built for `osx-arm64`. It loads only the modules the node's own manifest lists (here `Bazaar.Server.Vms.Ios`, `Bazaar.Server.Vms.MacOs` and their dependencies). It has no Postgres, no GC and no Vault. [PROPOSED; a `node` role does not exist yet.]
- **Link.** The node connects **out** to the primary server (no inbound port on the Mac) over the Conduit channel (doc 39), with mutual authentication by pinned keys: the node's identity key and the primary's key are pinned at install, and a mismatch fails closed. No pairing flow (owner decision, 2026-11-16).
- **Commands.** The primary sends only typed commands (`CreateChild`, `Start`, `Stop`, `Capture`, `Input`, `InstallApp`, a file transfer with hash check). The node accepts nothing else, never a shell string, never a path outside its jail. Frames and logs return over the same link.
- **Provider on the primary.** `Bazaar.Server.Vms.Ios` and `.MacOs` each ship a thin **node client** that implements `IVmBackend` and `IDeviceControl` by forwarding to the node, and a **node side** that does the work on the Mac. One project per provider holds both halves; the manifest says which half loads where. `RemoteNode = true` in the capability report.
- **Workspace share.** The agent's workspace reaches the node through the mounts module (doc 16 WP8) as a copy-in and copy-out transfer with hashes, not a network file share. A guest never sees the Soul folder, Passport or any credential (section 8).
- **Several Macs.** One node per Mac; the provider picks a node with free capacity. v1 supports one node; more is a follow-up.

## 7. Install, selection and "off until used"
- **Modules are installed, not bundled.** None of the three providers, the abstractions project nor the client module ships enabled. The setup wizard (doc 45) has an optional step "Mobile and Apple guests" with the Apple hardware sentence (section 3). The same choice is available later in the in-app admin panel and the web admin module (doc 51). Installing and removing follow doc 41 and doc 42 (removing keeps data and disables the module).
- **Host check.** A module refuses an unusable host with the reason (doc 50 section 5). Android declares `windows` and `linux` (x64) with feature `android-emulator` (the macOS host waits for the parked Apple work); iOS declares `macos` with `xcode-simulator`; macOS declares `macos`, `arm64` with `virtualization-framework`. This adds the `android-emulator` feature now, and `macos` with the Apple features later, to the known lists in `HostRequirements`, which is a `Bazaar.Contracts` change (schema stays at version 1; the lists are additive).
- **Not running until used.** Enabling a provider starts no emulator, simulator or VM and holds no port. A device guest starts when an environment requests it, stops after an idle timeout, and the provider's probe is a cheap file and feature check. The density governor (doc 59) counts device guests in its admission decision.
- **Selection.** `host.provider.vm` stays the pair provider. Device providers are listed in `vms.devices.enabled` (list of kinds, default empty), and a request for a guest OS whose provider is not enabled fails with a plain reason; there is no fallback to another provider.

Settings (all `vms.devices.*` entries follow the doc 16 and DECISIONS-LOG round 13 pattern: safe shipped default, audited change):

| Key | Type | Default | Applies | Who | Note |
|---|---|---|---|---|---|
| `vms.devices.enabled` | list | `[]` | install | operator | `microvm`, `android`, `ios`, `macos` |
| `vms.microvm.monitor` | enum | `firecracker` | install | operator | or `cloud_hypervisor` |
| `vms.microvm.max_concurrent` | int | 8 | live | operator | |
| `vms.microvm.state_dir` | path | (unset) | install | operator | where per-VM sockets and state live; empty means unset |
| `vms.devices.idle_stop_minutes` | int | 10 | live | operator | stop an unused guest |
| `vms.devices.max_concurrent` | int | 2 | live | operator | all device guests together; the governor may lower it |
| `vms.android.sdk_root`, `vms.android.avd_home` | path | unset | install | operator | required; no profile default |
| `vms.android.max_concurrent` | int | 2 | live | operator | |
| `vms.android.system_image` | string | unset | install | operator | pinned package id and hash |
| `vms.ios.max_concurrent` | int | 2 | live | operator | |
| `vms.macos.max_concurrent` | int | 2 | live | operator | may lower, never above 2 |
| `vms.node.mac.endpoint`, `vms.node.mac.key_pin` | string | unset | install | operator | outbound link and pinned identity |

## 8. Security rules
These are invariants, not settings.
1. **Device guests hold no secrets.** No Soul mount, no Passport profile, no provider key, no cookie jar, and no account session (doc 14, doc 16). Until `EgressEnforced` is true for a provider, the lifecycle refuses to attach anything credential-bearing and marks the guest `hard-tainted` by default (doc 15). Apps under test use test accounts the human supplies.
2. **Egress is not assumed.** Emulator proxy flags cover only part of the traffic. A provider reports `EgressEnforced = true` only after a spike proves that every packet leaves through the per-agent egress proxy (doc 16, WP9), for example by a network namespace on Linux. Until then device guests are allowed no network, or only a host-loopback service the agent started, and the capability report says so.
3. **The iOS Simulator is a host process.** Its node runs as a dedicated unprivileged user with no access to the owner's files, and the module refuses to start if it detects it is running as the owner's login or as root.
4. **Node commands are typed and jailed** (section 6). A compromised guest cannot widen them. A node that fails its key check is refused, not warned about.
5. **Screens and logs are untrusted external content** (doc 59, rule 1). They taint the session that reads them.
6. **No privileged path.** The emulator and helper run unprivileged. The Mac helper's one entitlement is virtualization; nothing else is requested.
7. **Kill switch and audit.** The kill switch stops every device guest of an agent. Every lifecycle call and every `IDeviceControl` call writes an audit row with the provider kind, the guest id and the argv class, not pixels or logs.

## 9. Client module
`Bazaar.Client.Devices` (and `Bazaar.Client.Devices.Abstractions` only if another module needs its contract; otherwise one project) registers a pane that lists the enabled providers, shows their capability report and probe reasons, and lists each running device guest and its state. Start, stop and a screen view are follow-ups (section 9.1). It reads the server through one new wire request, `vms.devices.list` (section 9.1). It hides itself when no device provider is enabled. Avalonia.Headless tests cover the pane (CLAUDE.md). The Android client is a development console and shows the list only.


### 9.1 Wire request `vms.devices.list` (PROPOSED)
Doc 16 and the existing routes give a client no way to ask which device providers are on and which device guests run, so this adds one read-only request. It is a wire change, so it lands docs first (this section) and follows doc 44's and the wire contract's rules: a typed payload in `Bazaar.Contracts`, source-generated JSON, no new serializer.
- **Request type** `vms.devices.list`, payload `DeviceListRequest()` (empty record).
- **Reply** `DeviceListReply(Providers, Guests)`. `DeviceProviderInfo(Kind, GuestOs, Usable, Reason, Isolation, Headless, Save, Checkpoint, Restore, EgressEnforced, MaxConcurrentInstances, Running)` is the capability report of section 4 plus the probe result. `DeviceGuestInfo(VmId, Kind, GuestOs, State)` has no agent name, port, path or screen content.
- **Who may call it:** the owner principal only. Any other principal gets `not_found`, as the Orrery list does, so a company's device usage is not disclosed.
- **Only enabled providers appear.** An install with `vms.devices.enabled` empty returns two empty lists, and the client hides the pane.
- **Server side:** a new module `Bazaar.Server.Vms.Devices` (component `vms-devices`) holds the handler, the `DeviceProvidersComponent` that registers the `vms.devices.*`, `vms.android.*` and `vms.microvm.*` settings through `ISettingContribution`, and registers the setup step of section 7. Providers implement `IDeviceProviderCatalogEntry` (in the device abstractions) to report themselves; the module only reads them.
- **Screen view is not in v1 of the pane.** A client screen view needs its own request and a decision about how pixels, which are untrusted and may hold secrets (section 8, rule 5), reach a human. It is a follow-up and a separate section.

## 10. Work packages
Each is its own module project with tests. The abstractions and node client come first; the three providers follow in parallel. All code uses fakes for the host (`IAndroidHost`, `ISimctlHost`, `IMacVmHelper`, `IMacNodeLink`) so cloud CI makes no emulator, Xcode or Apple calls, as doc 50 does for Windows Sandbox.

| # | Work package | Depends on |
|---|---|---|
| 1 | `Bazaar.Server.Vms.Devices.Abstractions`: types, capabilities, `IDeviceControl`, argv builders and validators, `VmOs` and `VmSpec` additions | doc 16 lifecycle |
| 2 | `HostRequirements` additions (`macos`, new features) and tests | doc 50 |
| 3 | `Bazaar.Server.Vms.Android`: backend, AVD and emulator argv, per-VM adb, fake host, capability report | 1, 2 |
| 4 | Android real host adapter (`avdmanager`, `emulator`, `adb`) and the gated integration test category `Android` | 3 |
| 5 | Node link contract and the `--role node` host composition | 1, Conduit (doc 39) |
| 6 | `Bazaar.Server.Vms.Ios`: client half, node half, `simctl` adapter | 1, 2, 5 |
| 7 | `Bazaar.Server.Vms.MacOs`: client half, node half, helper protocol | 1, 2, 5 |
| 8 | macOS helper executable (Swift or .NET interop) | 7, spike S-MAC |
| 9 | `Bazaar.Client.Devices` pane | 1 |
| 10 | Display source `device` in `Bazaar.Server.VmDisplay` | doc 59, 1 |
| 11 | Setup wizard step and admin-panel toggles | doc 45, doc 51 |
| 11a | `Bazaar.Server.Vms.MicroVm`: backend, `IMicroVmMonitor` with Firecracker and Cloud Hypervisor adapters, fake monitor, capability report | 1, 2 |
| 12 | Doc updates: README index, doc 16 and doc 59 cross-references, `BAZAAR-COMPONENTS.md` | this doc |

## 11. Spikes (the owner's machines measure)
- **S-AND (owner's PC, Windows):** the emulator under the Windows Hypervisor Platform with Hyper-V enabled, with the SDK at `V:\TOOL\android-sdk` and AVDs at `V:\TOOL\android-user`, nothing on the system drive. Measure boot time, memory per instance, snapshot save and restore, headless screenshot and input, and whether a network namespace or firewall rule can make egress mandatory.
- **S-MVM (Linux, KVM):** Firecracker and Cloud Hypervisor boot time, memory per VM, snapshot restore, vsock, a tap in a network namespace with mandatory egress.
- **S-AND-LNX (Linux, KVM):** the same on a KVM host, plus the egress enforcement test.
- **S-SIM (a Mac):** `simctl` lifecycle, headless screenshot, a clone, the unprivileged-user setup.
- **S-MAC (an Apple silicon Mac):** restore image install, an APFS-clone child, boot, the virtio socket and a `Bazaar.Guest.Agent` for `osx-arm64`, saved state, signing and entitlement requirements, the two-VM cap, Setup Assistant automation.
- **S-NODE (a Mac plus the primary server):** outbound link, key pin and fail-closed behaviour, a hash-checked file round trip.

## 12. Questions for the owner
1. **Mac node.** Is a second headless Bazaar process on a Mac (section 6) acceptable for iOS and macOS, given "the server does all compute"? *Recommend* yes: Apple's terms leave no other legal route. The alternative is to ship Android only and park iOS and macOS until a Mac is available.
2. **Do you have a Mac, and which?** The spikes S-SIM, S-MAC and S-NODE need Apple hardware. Apple silicon is assumed for macOS guests.
3. **Device guests with no network until egress is proven** (section 8, rule 2). *Recommend* yes. Agents can still build, install, run and screenshot an app that talks to a service on the host.
4. **SDK licence acceptance** (section 5.1): a human accepts the Android SDK licences once and the audit log records it. *Recommend* yes.
5. **microVM monitor default.** *Recommend* Firecracker first, Cloud Hypervisor second.

## 13. Conflicts
- Doc 16 names `IVmBackend` as the only backend interface and `VmOs` as Windows or Linux. This doc extends both additively; it does not replace either.
- Doc 16 and `CLAUDE.md` say "the server does all compute". Section 6 adds a node role for hardware the primary server cannot be. Owner question 1 covers it.
- Doc 50 fixes the known host-requirement lists. Section 7 adds to them.
- Doc 59 lists three display sources. Work package 10 adds a fourth, `device`; doc 59 itself stays unedited here because its PR is open (#851).

## 14. Build status
Built (2026-11-19, PR #870): `Bazaar.Server.Vms.Devices.Abstractions`, `Bazaar.Server.Vms.Android` (backend, argv builders, host seam, fake-host tests), `VmOs.Android` with the vms migration `0002_device_guest_os.sql`, and the `android-emulator` host feature. Also built: `Bazaar.Server.Vms.MicroVm` backend on a fake monitor (save, checkpoint and restore report unsupported until spike S-MVM). Also built: the Android SDK adapter (WP4), the Firecracker and Cloud Hypervisor adapters (WP11a), `Bazaar.Server.VmDisplay.Devices` (WP10, `Device` display source and `IDeviceDisplay` port) and `Bazaar.Server.Vms.Devices.Setup` (WP11, validator and setup step). Also built (PROPOSED section 9.1): the `vms.devices.list` wire types in `Bazaar.Contracts`, `Bazaar.Server.Vms.Devices` (component `vms-devices`: owner-only handler, the `vms.*` settings contribution, the setup step registered, the component in `HostComponents.All`), `IDeviceProviderCatalogEntry` on the Android and microVM backends, and `Bazaar.Client.Devices` (list-only pane, hidden when no provider is enabled). Not built yet: the client heads do not supply the pane's wire source, nothing in the host registers the backends as catalogue entries, and the client screen view.
