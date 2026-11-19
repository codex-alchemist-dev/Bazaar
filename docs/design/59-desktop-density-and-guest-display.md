# Desktop density and guest display

Status: PROPOSED (owner request, 2026-11-19). Sections 3 to 6 are build design within doc 16's decided topology. Section 7 is DECIDED (owner card, 2026-11-19). Section 8 is PROPOSED build design with configurable defaults. Platform facts carry a tag: MEASURED (this repo's notes), SOURCED (primary source in the research notes) or UNVERIFIED (needs the spike in section 10).
Depends on: `16-agent-vms-and-environments.md` (topology, guest protocol, profiles, settings), `50-windows-sandbox-provider.md`, `14-identity-service-and-passport.md` (browser profiles, C22), `15-security-taint-and-approvals.md` (taint, audit, kill switch), `03-server-host-and-providers.md` (providers) · Used by: `17-coding-agent-harness.md` (tools), `06-agent-runtime.md` (tool catalogue), `26-fun-systems.md` (Playtest), `20-engine-integration-api.md`
Research: `docs/research/notes/desktop-density/` (measurements and sourced notes).

## 1. Purpose
The owner wants many Windows and Linux VMs running at once, each light, each with a working graphical desktop, and each agent able to use a browser and take screenshots. Doc 16 chose headless guests to save memory. This doc shows that "no viewer" and "no desktop" are different things, and designs the screenshot and input path, the light desktop profile, and the controls that let one host carry many VMs.

## 2. Findings that shape the design
1. **A desktop needs no display device and no window on the host.** A headed Chromium in a kiosk Wayland compositor with software rendering runs with no GPU and no display device; the compositor costs about 11 MB. (MEASURED)
2. **A hypervisor can screenshot and drive a VM that shows nothing anywhere.** QEMU with `-display none` answers `screendump` and `input-send-event` (MEASURED). Hyper-V exposes the same through `GetVirtualSystemThumbnailImage`, `Msvm_Keyboard` and `Msvm_SyntheticMouse` with no console window open (SOURCED: the thumbnail is raw RGB565 at a requested width and height and needs administrator rights on the host; `TypeText` takes up to 512 ASCII characters per call; the pointer takes guest pixel coordinates. Behaviour with Linux guests and the size ceiling are UNVERIFIED until the spike). Nothing runs in the guest for this, so it also works for a guest whose agent is down, and for Windows without a logged-on session helper.
3. **The browser is the cost, not the desktop.** One headed Chromium tab is about 195 MB of private memory plus about 220 MB of binary and library pages. One kernel shares those 220 MB between instances; separate VMs do not. Headless Chromium is about 61 MB private. (MEASURED)
4. **Windows Sandbox is light because it shares the host's OS files and memory, and it is limited to one instance.** That mechanism is not offered for many instances through a supported interface (doc 50; UNVERIFIED beyond that). It stays a one-off provider.
5. **Density is about running VMs, not existing VMs.** A saved or stopped VM costs disk, not memory. Doc 16 already saves idle VMs. With per-agent schedules (doc 09) only part of the company is awake at once.

So the design keeps guests "headless" in doc 16's sense (no viewer, no enhanced session, no clipboard) and still gives every agent a desktop it can see.

## 3. Display sources
A screenshot or an input event reaches a guest through one of three sources. All three sit behind one server contract, `IGuestDisplay`.

| Source | How | Works for | Needs in the guest | Notes |
|---|---|---|---|---|
| `hypervisor` | Host reads the VM's virtual framebuffer and injects synthetic keyboard and pointer events | Windows and Linux guests on Hyper-V and KVM | A video driver only | The trusted path. Shows boot screens, lock screens and crashes. Not available for Windows Sandbox (not a managed Hyper-V VM; UNVERIFIED). |
| `guest` | The guest agent runs a fixed capture tool and returns the file over the existing guest channel | Linux guests with the desktop profile; Windows Sandbox | Compositor, capture tool | Full resolution PNG. Works with no display device at all. |
| `browser` | Chrome DevTools Protocol over the existing debugging pipe (`Page.captureScreenshot`, `Input.*`) | Any browser session, headed or headless | Nothing new | Page only (no browser chrome). Cheapest. |

Selection: the caller may name a source; otherwise a browser session uses `browser`, a Linux guest uses `guest`, and a Windows guest uses `hypervisor`. If the preferred source is unavailable the next one is tried and the frame records which source produced it. If none is available the call fails with a reason; nothing is faked.

**No wire change.** The `guest` source uses doc 16's existing `Exec` and `GetFile` messages: the host runs a fixed argv (`grim <path>` on Linux) and fetches the file, hash-checked. Input is an `Exec` of a fixed tool with typed arguments built host-side. A dedicated streaming message is a later optimisation and would be a wire decision of its own.

**Windows guests.** A Windows service cannot capture or type into the interactive desktop (session 0 isolation). The `hypervisor` source avoids the problem. A session helper is not built.

## 4. Server module
`Bazaar.Server.VmDisplay` and `Bazaar.Server.VmDisplay.Abstractions`, a module pair on Bazaar.Core (component id `vm-display`). It does not reference `Bazaar.Server.csproj`. Hard dependencies: none. Soft: `vms`, `browser`, `security`, `audit`.

```csharp
public interface IGuestDisplay {
    Task<ScreenFrame> CaptureAsync(CaptureRequest request, CancellationToken ct);
    Task SendInputAsync(InputBatch batch, CancellationToken ct);
    IReadOnlyList<DisplaySourceKind> AvailableSources(DisplayTarget target);
}
public sealed record DisplayTarget(string AgentId, Guid VmId, DisplayGuestOs Os, string? BrowserSessionId = null);
public sealed record ScreenFrame(byte[] Png, int Width, int Height, DisplaySourceKind Source, DateTimeOffset CapturedAt, string Sha256);
// InputAction: PointerMove, PointerButton, PointerScroll, KeyStroke (W3C UI Events key codes), TypeText
```

Ports the host adapts (the module holds no hypervisor, guest or browser code of its own):
- `IHypervisorConsole`: read the framebuffer and send input for a backend reference. Implemented beside each VM backend (Hyper-V, KVM). The KVM adapter speaks QMP (`screendump` with PNG, QEMU 7.1 or newer, and `input-send-event` with an absolute tablet device) through the monitor, not `virsh screenshot`, which fails when the domain has no graphics element (SECONDARY). `screendump` with `-display none` is MEASURED on QEMU 8.2.
- `IDisplayGuestExec`: run a fixed argv and fetch a file in a VM. The host adapts the guest channel (doc 16 WP7).
- `IBrowserPageDriver`: screenshot and input for a browser session. The host adapts the browser module (doc 16 WP13).
- `IDisplayGate`: taint and kill-switch state (doc 15). `IDisplayAuditSink`: audit rows.

Inside the module: source selection and fallback, frame normalisation to PNG (RGB565 from Hyper-V, PPM from older QEMU, PNG pass-through after header validation), downscaling to the configured limit, the per-agent rate limiter, argv builders for the guest tools, and key-name mapping to each source's key codes. All of it is pure and unit tested.

## 5. Security rules
These are invariants, not settings.
1. **A screenshot is untrusted external content.** Pixels can carry instructions. A frame handed to a model taints the session the same way a fetched web page does (doc 15, hard taint for external content). The module tags every frame; it never decides trust itself.
2. **A screenshot may contain secrets.** Frames from an account browser profile or a VM with a Soul mount are never logged, never written to the Vault by this module, and are held only as long as the caller holds them. The audit row carries size, dimensions, source and SHA-256, not pixels.
3. **Input is an action.** Input to a hard-tainted agent's account browser session is refused. The kill switch refuses all input and capture for the agent. Input never carries a secret: `TypeText` is refused when the text matches a leased credential (the credential broker types secrets, doc 14).
4. **The guest never chooses paths or tools.** Capture paths and argv come from the server. Decoders reject frames above the size limit before allocating, and reject malformed headers (a compromised guest controls these bytes).
5. **One agent, one target.** A capture or input call names an agent and a VM, and the VM must belong to that agent. Cross-agent viewing is an owner feature of the clients (Observatory), not of this contract.
6. Every capture and input batch writes an audit row, rate-limited per agent.

**Tool tiers (PROPOSED 2026-11-19; agreed with the Security area as fitting the tier model of doc 15).** The invariants above are the floor. Within them, the tier of each tool is an operator setting, and the shipped default is the strictest:
- `screen.capture` is a read-class capability. Its result always hard-taints the session (rule 1); that part is not a setting.
- `screen.input` is an action. By default it is refused once the session is hard-tainted when the target is an account browser session (rule 3), and allowed for the agent's own VMs and for anonymous pool browsers. An operator may tighten this (for example refuse all input under hard taint, or require approval), never loosen it below rule 3.
- The gate reads the kill switch, the session taint and the leased-credential matcher through `Bazaar.Server.Security.Abstractions` (PR #743 and its follow-ups). Until those contracts exist the gate stays unconfigured and refuses everything.

## 6. Desktop profile for guests
A profile in doc 16's sense: a preset over the same golden image, not a third VM.

**Linux (`kiosk`).** `cage` (wlroots kiosk compositor) with the headless backend and the pixman software renderer, started on demand by the guest agent as the non-admin exec user; `grim` for capture; `wtype` and a virtual-pointer tool for input; Chromium with `--ozone-platform=wayland`. No display manager, no desktop environment, no GPU. When the VM has a virtual display device and the hypervisor source is wanted, the compositor uses the DRM backend instead, so both sources show the same picture. The compositor does not run until a headed session is asked for; an idle Linux guest stays as light as today.

**Windows.** Windows always has a framebuffer, including Server Core, so the hypervisor source needs no profile. Whether headed Chromium runs acceptably on Server Core (with the App Compatibility feature) or needs Desktop Experience is UNVERIFIED and goes to the spike; the answer sets the Windows memory default.

**Headed or headless browser.** `browser.display_default` stays `headless` (doc 16). Headless plus the `browser` source already gives screenshots and input at a third of the memory. Headed is for work that needs the real window: extensions, file dialogs, downloads UI, pop-ups, sites that fingerprint headless, and playtests.

## 7. Where browsers run (DECIDED, owner card 2026-11-19)
C22 decided: Chromium runs inside the agent's existing VMs. The owner asked (2026-11-19) whether browsers could instead run outside the VMs, sandboxed by Bazaar.

| Placement | Memory per extra browser | What an escaped browser exploit reaches | Screenshots | Fit with decided items |
|---|---|---|---|---|
| A. In the agent's own VM (today) | About 195 MB private plus 220 MB unshared pages headed; about 61 MB plus unshared pages headless; plus a running VM | That agent's VM only | All three sources | C22 as decided |
| B. On the host, sandboxed by Bazaar | About 61 MB headless, 195 MB headed; binary shared | The server host, which holds Identity keys, the database and every agent's data | `browser` source only | Changes C22. Breaks doc 16's rule that egress is enforced outside the workload. |
| C. A shared browser VM ("browser pool") | Same as B, plus one VM for all | The pool VM: other agents' browser sessions in it, not the host | `browser` and `guest` | Uses C22's "third VM only if a spike needs it", as one shared VM instead of one per agent |

Chromium's own sandbox is strong, but it is the only wall in option B, and agents open arbitrary pages all day. Chromium's own threat model assumes a compromised renderer and expects outside containment (SOURCED). Hosted browser services put each browser in its own VM or microVM, not on a shared host (SOURCED: Browserbase documents one VM per session). Option C keeps a hypervisor wall in front of the host and still shares the browser's pages on one kernel.

**Decided (owner, 2026-11-19): shared browser VM.** Anonymous profiles (no credentials, the bulk of browsing) run in the pool VM, each in its own OS user, namespace sandbox and profile directory, with egress through the per-agent proxy as today. Account profiles (Passport cookies) stay in the agent's own VM as C22 decided, so a pool escape never reaches a logged-in session. Setting `browser.placement.anonymous` = `agent_vm` | `pool_vm`, default `pool_vm` once the pool VM ships and `agent_vm` until then; `browser.placement.account` is fixed to `agent_vm`. Host placement (B) is not offered.

**From discovery to an account (owner, 2026-11-19).** The owner wants agents to find sites, triage them and later make their own accounts. The placement above is that path, and the approval rules already exist:
1. *Discover and triage* happens in an anonymous profile (the pool VM if question 1 is answered that way). No credential is present, so a hostile site gains nothing. Screenshots from triage are untrusted content (section 5).
2. *Verify terms.* A site with no verified `ServiceTerms` record is Deny for any action (doc 15, doc 36). Triage can propose a record; a platform's rules are checked before the first post (CLAUDE.md).
3. *Approve.* New account creation is Tier 2, only for services on `identity.signup_whitelist` (doc 14). The approval comes from the owner or, since 2026-11-19, from senior peers (doc 15, "Peer approval of account creation"). CAPTCHA, phone and ID steps stay with a person; sign-ups that forbid automation are Deny (doc 15, doc 22). The agent may accept the site's terms itself once two senior peers have approved (doc 15).
4. *Sign up* runs in the agent's own VM, in a new account profile, with the agent's external mailbox for the verification mail (doc 22). The account never exists in the pool VM.
5. *Use.* The site joins that profile's allowlist and the agent's egress allowlist; the Passport holds the session (doc 14).

Nothing here changes those decisions. The only new piece is step 1's placement and the hand-over from an anonymous session to an account profile, which is a new launch in the agent's VM, never a migration of a running browser.

**Peer triage and vote before a site is allowlisted (owner idea and card, 2026-11-19; the effect of a vote is DECIDED, the motion mechanics are PROPOSED).** The Council (doc 10) has no motion for sites today; allowlist growth is an owner approval (doc 15 Tier 2, `vm.egress.allowlist_change_approval`). Proposed addition, reusing the Council as it is:
- A new motion kind `site_allowlist` on the existing approve/reject vote type. The subject is the site's registrable domain. The motion carries the triage report: what the site is for, the proposed `ServiceTerms` record (doc 36), the scope asked for (read only, or account), and screenshots from the anonymous session as evidence.
- Triage evidence is untrusted content. Voters read it in reader sessions (doc 15), so a site cannot talk its way onto the list through its own page text. The proposer cannot vote (doc 10 eligibility).
- Sealed ballots, reveal, one follow-up and escalation work as doc 10 says. The Council emits `council.decision`; the egress and Identity modules apply it. The Council writes no allowlist itself.
- **Decided (owner card, 2026-11-19):** a unanimous vote allowlists the site for login-free reading only (anonymous profiles), with no owner step. A split or tied vote goes to the owner. A new account or logged-in use is a separate Tier 2 approval with the vote record attached, given by the owner or by senior peers (doc 15).
- Limits that no vote can lift: a site with no verified terms record or terms that forbid automation stays Deny; CAPTCHA, phone and ID stay with a person; account creation needs `identity.signup_whitelist`.

## 8. Density controls
Memory is the limit; these are the levers, most effective first.

1. **Run less.** The density governor (new, `Bazaar.Server.VmDensity`): an admission controller in front of the lifecycle service. It knows each VM's assigned memory and the host's free memory. A start that would cross `vm.density.host_reserve_mb` first saves the least recently used idle VM (doc 16's Saved state), and queues the start if nothing can be saved. It never stops a VM with a running exec, an open terminal or a live browser session. Order under pressure: idle Windows VMs, then idle Linux VMs, then queue. This turns "about 4 pairs fit" into "about 4 pairs run at once, and the rest wake in seconds".
2. **Start only what is used.** `vm.pair.start_policy` = `both` | `on_demand` (PROPOSED default `on_demand`): an agent's Windows VM is created with the pair but started on first use. Most work runs in the Linux VM (doc 16, C23).
3. **Right-size.** A Linux guest with the kiosk profile and one headed browser fits in about 1 GB (MEASURED stack, UNVERIFIED in a VM). Defaults change only after the benchmark.
4. **Give memory back.** Hyper-V Dynamic Memory for Linux and Windows guests; on KVM, the balloon with free page reporting. Setting `vm.memory.mode` = `static` | `dynamic`, default `static` until benchmarked (doc 16 said the same).
5. **Share what is identical.** One read-only base image for all children (decided). On KVM, optionally map the base image so guests share its page cache, and optionally enable KSM. KSM deduplicates across agents and has a known cross-VM side channel, so `vm.kvm.ksm` defaults to off and setup explains it. Hyper-V has no page sharing between VMs (SOURCED, from a 2010 Microsoft design note; the benchmark confirms).
6. **Windows Sandbox** remains a single-instance provider for one-off work (doc 50).

Not proposed: several agents in one Windows VM as separate sessions. It is the lightest way to give many agents a Windows desktop, but it replaces the VM wall between agents with a user-account wall and needs Remote Desktop licensing. It contradicts C03 (a Windows VM per agent). Listed so the owner knows it exists.

## 9. Settings
Registry per doc 03. Changes are audited.

| Key | Type | Default | Who may change | Notes |
|---|---|---|---|---|
| `vm.display.enabled` | bool | true | operator | Off removes the tools from the catalogue. |
| `vm.display.max_width`, `vm.display.max_height` | int | 1920, 1200 | operator | Larger frames are downscaled. Hard ceiling 3840 x 2160. |
| `vm.display.captures_per_minute` | int | 60 | operator | Per agent. |
| `vm.display.input_batches_per_minute` | int | 120 | operator | Per agent. |
| `vm.display.guest_capture_timeout_s` | int | 10 | operator | |
| `vm.linux.desktop_profile` | enum: none, kiosk | kiosk | operator | `none` leaves only the `browser` and `hypervisor` sources. |
| `browser.placement.anonymous` | enum: agent_vm, pool_vm | pool_vm (agent_vm until the pool VM ships) | operator | Section 7, decided. |
| `vm.pair.start_policy` | enum: both, on_demand | on_demand | operator | Section 8. |
| `vm.density.host_reserve_mb` | int | 4096 | operator | Free host memory the governor keeps. |
| `vm.density.max_running` | int | 0 (no fixed cap) | operator | Optional hard cap besides the memory rule. |
| `vm.memory.mode` | enum: static, dynamic | static | operator | Dynamic only after the benchmark. |
| `vm.kvm.ksm` | bool | false | operator | Cross-VM side channel; setup warns. |

## 10. Benchmark and spike (owner's PC, then a KVM host)
Extends the VmBench module (doc 16 "Benchmark") with scenarios: `DesktopIdleRam` (kiosk profile up, no browser), `BrowserRam` (headed and headless, one tab), `ScreenshotLatency` per source, `InputLatency`, and `DensityRamp` (start pairs until the governor queues; record count and host free memory). Pass criteria are fixed before the first run and recorded with the report.

Spike S-DSP on the owner's PC (nothing on C:, tools under V:\TOOL, a separate WSL distro if WSL is needed): Hyper-V thumbnail capture and synthetic input for a Windows and a Linux guest with no console window; maximum thumbnail size and pixel format; Dynamic Memory with a Linux guest; headed Chromium on Server Core; whether a Windows Sandbox instance is visible to the Hyper-V management classes.

## 11. Work packages
1. `Bazaar.Server.VmDisplay` and `.Abstractions`: contracts, source selection, frame codecs, rate limit, gate, audit, argv builders, key maps, fakes. (no dependency)
2. Hypervisor console adapters: Hyper-V (thumbnail and synthetic input through the provider's PowerShell runner) and KVM (QMP through virsh). (after 1; beside PRs #688 and #840)
3. Guest and browser adapters in the host composition: guest channel (after WP7, PR #802) and browser module (after WP13, PR #832, which gains `Page.captureScreenshot` and `Input.*`).
4. Linux kiosk profile: image recipe entries, on-demand compositor start, capture and input tools, tests on a Linux runner with the real tools.
5. `Bazaar.Server.VmDensity`: governor, pressure order, queue, settings, tests against a fake backend. (no dependency)
6. Agent tools `screen.capture` and `screen.input` in the tool catalogue with the taint tag. (after 1, doc 06)
7. VmBench scenarios and the S-DSP spike. (after 1, 2, 4; PR #842)
8. Browser pool VM: one light Linux VM, a per-agent OS user and namespace sandbox per session, per-agent egress proxy, kiosk profile, pool recycling. Own module `Bazaar.Server.BrowserPool`. (after 1, 4; PR #832)

### Build status (2026-11-19)
- Work package 1: PR #860. Guest typing uses `wtype` with a leading `-k Shift_L`.
- Work package 4, pointer part: in PR #860. `bazaar-pointer` (project `Bazaar.Guest.Pointer`) is a small managed tool that speaks the Wayland virtual-pointer protocol with absolute coordinates. The guest source uses it once a guest capture has told the server the output size; before that, guest pointer input is still refused. Still to do: ship the tool in the guest image, and convert coordinates when an agent works from a downscaled frame (work package 6).
- Work package 2: PR #867. The KVM adapter is tested against a real QEMU. The Hyper-V scripts are unverified until spike S-DSP.
- Work package 5: PR #857.
- Work package 8: PR #868 (session manager) and PR #898 (Linux host adapter, firewall health check, and the DevTools pipe through `bazaar-pool-spawn`). A real Chromium answered over the pipe through the whole sandbox chain. PR #950 adds `Bazaar.Server.BrowserPool.Display`, the page driver that takes screenshots and sends pointer and keyboard input over that pipe using six fixed DevTools methods; a real Chromium was clicked, typed into and captured through it. PR #950 also makes the Linux host adapter and the page driver loadable components (`browser-pool-linux-host`, `browser-pool-display`). PR #957 adds `Bazaar.Server.VmDisplay.Audit`, the audit sink that writes capture, input and refusal rows (sizes and hashes only, never content) to the audit log and fails closed. Still blocked: the real display gate, the tool policy entries for `screen.capture` and `screen.input`, and taint marking of screenshot results. The policy engine, taint store and kill switch types live inside `Bazaar.Server`, which a module may not reference, and no doc gives the tier of the two tools. Still to do: the pool VM image, a native AOT build of the helper, and a probe packet in the health check.
- Work package 5, host side: PR #896 (Linux and Windows memory probes). The adapter to the VM lifecycle waits for an abstractions project in `Bazaar.Server.Vms`.
- Agent tools `screen.capture` and `screen.input`: PR #878. They stay refused until the server's tool policy lists them.
- Section 7 rules (peer approval of account creation, what a site vote opens): PR #889, module `Bazaar.Server.AccountApproval`. State is in memory for now. The shipped list of senior peers is empty, so every request goes to the owner until the operator names them. A vote counts as unanimous only with at least `MinUnanimousVoters` voters (module option, default 2, minimum 1; it has no settings key yet).

### Browser pool isolation: what holds and what does not (measured, PR #868)
Each session has its own slot: a Linux uid, an empty profile, `setpriv` (no new privileges, empty capability sets), a `bwrap` sandbox that hides `/root`, `/var`, `/home`, `/etc/passwd` and the other slots, Chromium's own sandbox left on (renderers under seccomp), and a per-uid nftables rule that allows only that agent's egress proxy. A real Chromium in this sandbox sent its page request to the proxy and nothing else was reachable by path.

Known limits, in order of weight:
1. Network: closed by default since the second commit of PR #868. Each slot gets its own network namespace (`browser.pool.network_isolation = per_slot_namespace`, the default) joined to the VM by a veth pair; one nftables table lets a slot reach only its agent's proxy through a DNAT and drops everything else arriving from a slot. Proven with real processes: the slot reaches its proxy, cannot reach another port, another address, another slot, or an abstract UNIX socket of the VM. The older mode (`shared_with_uid_rule`) stays for hosts that cannot create namespaces and keeps the abstract-socket gap. For a proxy that is not on the pool VM, the operator names the VM's network interface (`browser.pool.egress_interface`) and the pool turns on forwarding for that one interface; the host adapter must render the rules with the options overload.
2. Slots share one kernel. A kernel exploit reaches every slot; the VM wall protects the host and the agent VMs. This is the reason account profiles stay out of the pool (section 7).
3. Confinement to the proxy rests on the host adapter applying the rendered rules. The adapter must ship a health check that proves a slot uid reaches nothing but its proxy, and the pool refuses to open sessions when the check fails.
4. No per-slot disk or CPU cap yet; memory and task caps need `systemd-run`.
5. Chromium sends background requests to Google hosts through the proxy even with background networking disabled. The proxy allowlist refuses them.
6. Stock Ubuntu 24.04 restricts unprivileged user namespaces, so the pool image needs an AppArmor profile for `bwrap`. Without it sessions fail closed. `--no-sandbox` is never the fix.
7. Egress endpoints are IPv4 literals only for now.

## 12. Questions for the owner
1. **Where do anonymous browsers run?** ANSWERED (owner card, 2026-11-19): a shared browser VM for anonymous profiles; account profiles stay in each agent's own VM (section 7).
2. **Start the Windows VM only on first use?** *Recommend* yes (`vm.pair.start_policy = on_demand`). This is a default, so it follows the configurability rule and is not blocked on an answer.

3. **What may a passed peer vote on a site do without the owner?** ANSWERED (owner card, 2026-11-19): a unanimous vote opens the site for login-free reading only; anything split, and every account, still comes to the owner with the vote attached.

4. **How many senior peers must approve a new account?** ANSWERED (owner card, 2026-11-19): two, falling back to the owner when two are not available or one refuses.
5. **Who accepts a site's legal terms during sign-up?** ANSWERED (owner card, 2026-11-19): the agent, after the two-peer approval (doc 15). Counsel reviews before the first public release.

## 13. Conflicts and notes
- Doc 16 says "headless by default" and has `vm.headed_vm`. This doc does not change either: no viewer is attached and enhanced session stays off. `vm.headed_vm` keeps its meaning (a VM with an attached interactive console on a capable server).
- Doc 16's resource table is unchanged until the benchmark. Windows Server 2025 lists 2 GB as its minimum for both Core and Desktop Experience (SOURCED), so the Windows guest is unlikely to go below that; Microsoft publishes no idle figures.
- Linux guests on Hyper-V use the `hyperv_drm` video driver; `hyperv_fb` is being removed upstream (SECONDARY).
- Windows licensing for guest editions is unchanged from doc 16 (research only; counsel before release).
- Doc 50 capability report gains a "display source: guest only" line when work package 3 lands.
