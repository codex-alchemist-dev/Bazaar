# Agent VMs and Environments

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: `03-server-host-and-providers.md` (provider interfaces, service host, settings registry), `04-postgres-schema-and-migrations.md` (conventions), `07-soul-system.md` (Soul folders), `14-identity-service-and-passport.md` (Passport, secret custody), `15-security-taint-and-approvals.md` (taint, detach, audit, approval tiers), `Bazaar.Soul.md` and `Bazaar.Server.md` (deep-dives) · Used by: `06-agent-runtime.md`, `17-coding-agent-harness.md`, `20-engine-integration-api.md`, `28-windows-client.md` (Studio handoff), `30-linux-client-and-packaging.md` (agent terminals), `33-backup-dr-and-export.md`, `34-testing-strategy.md`, `35-spikes-plan.md`

## Purpose
Defines the environment each agent works in: one Windows VM and one Linux VM per agent, created from versioned golden images and driven by the Bazaar.Server VM provider through a guest agent. Disks are disposable and rebuildable. Identity, memory and provider credentials stay host-side; only the scoped Soul folder (which carries the session-cookie profile) and the agent's workspace share reach a VM.

## Decided inputs
- Two VMs per agent, Windows and Linux, headless by default. One may be fully headed on a more capable server (setting `vm.headed_vm`, off by default). Spike S8 sizes the pair; it does not choose the topology. (`Bazaar.Soul.md` §2; owner 2026-10-24; DECISIONS-LOG round 1, C03)
- Differencing disks off one golden image per OS: Hyper-V differencing VHDX on Windows hosts, QEMU/KVM qcow2 backing files on Linux hosts. (`Bazaar.Soul.md` §2)
- The per-agent Soul folder holds metadata and a session-cookie profile. Hard taint detaches it. The owner accepted the risk. (C02, round 2)
- Provider keys, GitHub App keys and audit are never mounted; git pushes run host-side (C05, round 2).
- Browser: full Chromium with profiles inside the existing VMs. An anonymous profile reads the web; account profiles use the Passport. A third VM only if a spike needs it (C22, round 7).
- Agent terminals are server-side, streamed to clients (C31). Roblox Studio runs only on the human's Bazaar.Windows desktop; agents use headless Luau checks and Godot `--headless`. The Studio relay is a ratified client-side exception (C17). (`Bazaar.Soul.md` §2)
- No clipboard sharing, no host drive passthrough beyond scoped mounts, enhanced session mode off. (`Bazaar.Soul.md` §2)
- One OS-agnostic server build; Hyper-V is the first provider behind provider interfaces. (`CLAUDE.md`)
- This doc owns the environment tables below (C16, round 6).

## Design

**Components**
- **VM provider** (`IVmBackend`): Hyper-V on Windows (first); KVM on Linux (later). The only backend interface, called only by the server, never from a guest. [PROPOSED] internals.
- **Environment service**: owns the agent's VM pair, lifecycle, schedules and resource accounting. Serialises lifecycle operations per VM.
- **Guest agent**: one self-contained .NET 11 program, baked into both guest images. Runs commands, moves files and hosts terminal sessions. [PROPOSED]
- **Guest channel** (`IGuestChannel`): the only guest wire. `IWorkspace` (doc 17) is a facade over it.
- **Scoped mounts**, host-side and bound to one agent: a Soul folder (metadata and session-cookie profile, detached on hard taint) and one workspace share for coding worktrees (doc 17). The workspace share is the explicit exception to "one scoped folder" (C02, round 2); it is not the Soul folder. [PROPOSED] a separate share and guest OS account per agent; doc 15 designs their isolation and the canary credentials that C02 makes required.
- **Profiles**: presets of image, egress and mounts over the same two images. The browser profiles are the first use. [PROPOSED] restore and sandbox roles are also profiles on the same two images, not third VMs (C22 default).
- **Egress**: enforced outside the guest (see Behaviour). Its design has no entry in the doc index (see Conflicts).

**Lifecycle** [PROPOSED]: `Absent → Provisioning → Stopped ⇄ Running → Saved → Running`; `Stopped/Saved → Checkpointed → Restored`; `→ Retired`.
- *Create*: child disk from a pinned golden image version, static memory and vCPU, scoped mounts, boot, guest `Hello`.
- *Idle save*: a running VM with no exec for `vm.idle_save_minutes.linux` (default 15) or `.windows` (default 60) moves to Saved.
- *Checkpoint*: stopped or saved VMs only. Running checkpoints stay off (`vm.checkpoint_while_running`, default off) until benchmarked.
- *Restore*: rolls back in-VM work only. Scoped folders are host-side, so restore never rolls back memory, identity or cookies.
- *Retire*: stop, export audit rows, delete the child disk after retention. Soul data is kept.

**Golden image and rebase** [PROPOSED]
- A recipe with pinned tool versions builds a versioned parent disk. Its SHA-256 and guest agent version are recorded, and the parent is set read-only. A child records its parent ID and hash; the provider refuses a missing or mismatched parent.
- Toolchain update = version N+1. Existing agents move by per-agent rebase, starting with `vm.rebase_canary_agents` canary agents. New child, Soul folder reattached, migration hook run. In-VM state is lost by design.
- Parent N is kept until its last child retires. This protects rollback and is a design invariant, not a setting.
- The server never writes a parent after children exist (ACL and hash check). Hyper-V behaviour here is unverified.

**Guest protocol**
- Transport: Hyper-V sockets (`AF_HYPERV`, service GUID) for Windows guests on a Windows host; `AF_VSOCK` for Linux guests. A Windows guest on a Linux host has no transport: **[OPEN]**, deferred (Question 1).
- Framing: length-prefixed, versioned envelopes in `Bazaar.Contracts`. The overall wire format stays OPEN (no Protobuf).
- Messages: `Hello{agentVersion, vmId, nonce}`, `Exec{id, argv, cwd, envAllowlist, timeoutSec}`, `Output{id, stream, seq, bytes}`, `Cancel{id}`, `Exit{id, code, timedOut}`, `PutFile{path, size, sha256}` / `GetFile`, `Heartbeat`. [PROPOSED] terminals: `PtyOpen{id, cols, rows}`, `PtyData{id, seq, bytes}`, `PtyResize`, `PtyClose` (Question 3). Unknown types close the channel.
- [PROPOSED, 2026-11-18, requested by the coding-harness thread] Workspace file operations, protocol version 3 (the codec still reads versions 1 and 2): `DeleteFile{path}` answered by `DeleteFileResult{path, ok, error}`; `ListFiles{dir}` answered by `ListFilesResult{dir, entries[{name, size, isDirectory}], error}` (non-recursive; `error` is null on success; a directory too large for one frame is an error); `ResolveRealPath{path}` answered by `ResolveRealPathResult{path, realPath, error}`. All three take paths **relative to the worktree root** (the guest's configured allowed root, the first entry of its roots), unlike `PutFile` and `GetFile`, which keep guest paths checked against the mount list. The guest itself rejects absolute paths, drive or UNC prefixes and any `..` segment (the host's jail is not trusted for this). `DeleteFile` removes one file or one symlink itself (never a link's target) and refuses a directory. `ResolveRealPath` follows symlinks inside the root and returns `realPath` (root-relative, `/` separators) or null; it returns null, failing closed, when the target leaves the root, a link loop or an unresolvable link is hit, or the path is refused (then `error` says why). The target need not exist, so the harness can check a path before it writes it. The host serialises these with `PutFile` and `GetFile` (one file request in flight) and applies the same answer deadline. Rules in force:
  - *Path rules* (`GuestPathRules`, shared by guest and host): the guest refuses a path that is rooted, has a drive or UNC prefix, has a `..` segment, contains NUL or `:` anywhere, or is over 4096 characters; on Windows it also refuses segments that end in a dot or space or name a reserved device (CON, NUL, COM1 and so on). Answers echo the path cut to 4096 characters.
  - *Resolution* follows realpath(3): it walks segment by segment, applies `..` to the directory resolved so far (never to the text of the path, including inside link targets), and caps link hops at 40. Any file-system error while resolving counts as a refusal (fail closed). PutFile and GetFile use the same resolver.
  - *Host validation of answers:* a `realPath` that is not a safe root-relative path is treated as null. A listing whose entry names are missing, empty, `.`, `..`, or contain a separator, NUL or `:` faults the channel. A symlink entry is listed as a non-directory of size 0 and never followed.
  - *Versioning:* each frame carries the lowest protocol version that defines its tag, so version 2 guests still read every older message. `Hello` gains an optional `protocolVersion` (absent reads as 0). The host throws `NotSupportedException` for these three requests against a guest that announced less than 3, instead of sending a tag that would close the channel.
  - *Known gap (TOCTOU):* .NET exposes no `openat`/`unlinkat`, so the guest resolves the path and then operates on the resolved path. It re-resolves immediately before the delete and after a listing and refuses when the result changed, which narrows the window but does not close it. A process in the guest that can swap a directory for a symlink inside that window can still redirect a delete within the guest's own permissions. Closing it needs no-follow handles through a platform provider (Linux `openat2` with `RESOLVE_BENEATH`, Windows handle-relative opens) and is not delivered.
- These `Pty*` messages are the guest transport for agent terminals. Docs 17 and 30 point here. The Bazaar.Windows local terminal is not a guest channel (C31).
- Auth: per-VM key generated at create, pinned by the host. The guest accepts only host-signed requests. [PROPOSED]
- Limits: exec timeout, output cap and concurrent execs per VM are settings (`vm.exec.*`). Execs run as a non-admin guest user, and a timeout kills the whole process tree. [PROPOSED]
- Guest-supplied paths are checked against the VM's mount list.

**Network and egress** [PROPOSED; build design below]: default-deny at the host switch or bridge (mechanism to verify on the owner's PC). The only route is a host egress proxy with a per-agent domain allowlist, used by browser profiles too. DNS goes through the proxy. Guest firewalls are not trusted. Allowlist changes need approval (`vm.egress.allowlist_change_approval`).

**Resources** [PROPOSED starting values; the Windows estimate is unsourced]

| VM | vCPU | Memory (static) | Child disk cap |
|---|---|---|---|
| Windows (headless Server Core) | 2 | 4 GB | 60 GB |
| Linux (headless) | 2 | 2 GB | 30 GB |

- These are the defaults of the `vm.windows.*` and `vm.linux.*` settings.
- A 32 GB NUC (unverified) fits about 4 running pairs at 6 GB each, after about 4 GB for the host and an unsized share for PostgreSQL and the server. Saved pairs need host disk for saved memory [unverified]. Benchmark before committing any number.
- Static memory first. Dynamic memory is untested.

**Licensing** (research, not advice; needs counsel or Microsoft)
- Windows host Standard covers 2 Windows Server VMs; Datacenter is $6,771 list for unlimited VMs. Linux host: each Windows VM is separately licensed, about $588 per VM at list, eligibility unverified. Windows 11 Pro as a 24/7 host: not clearly allowed. CAL position unanswered (Question 2).
- Design direction (C23): Linux guests carry no Microsoft licence, so work that does not need Windows runs in the Linux VM. Windows VM cost still scales with agents (C03).

**Hyper-V control from .NET** [PROPOSED]: a long-lived, out-of-process PowerShell runspace inside the provider (CIM `root\virtualization\v2` is the alternative, unverified). The service account is in Hyper-V Administrators only; parameters are typed, never string-built; agent IDs are validated.

**KVM control from Linux** [PROPOSED, owner-delegated implementation detail, 2026-11-19]: module `Bazaar.Server.Vms.Kvm` (WP12). Provider kind `kvm`, host requirements Linux, x64, feature `kvm` (`/dev/kvm` present). All host access goes through `IKvmHost`; the real adapter runs the `virsh` and `qemu-img` binaries with argument lists (never a shell string) and every name is generated by the backend (`bazaar-<guid N>`, checkpoint `bazaar-cp-<guid N>`) and validated again in the adapter.
- Child disk: `qemu-img create -f qcow2 -b <parent.qcow2> -F qcow2 <child>`; parent is `<ImageDirectory>/<version id N>.qcow2`.
- Domain XML is built with `XmlWriter` (no string concatenation): KVM domain type, static memory, no network interface (egress default deny), virtio disk, and a `<vsock model='virtio'>` device with an auto-assigned CID, the guest transport for Linux guests (`AF_VSOCK`, see Transports above).
- Save is `virsh managedsave`; a start resumes it. Checkpoints are internal snapshots of a shut-off domain (`virsh snapshot-create-as`; restore runs `managedsave-remove` then `snapshot-revert`, so a stale memory image never resumes on a reverted disk). Retire is `destroy` if running, then `undefine --managed-save --snapshots-metadata --nvram` and deleting the child disk.
- Windows guests are refused: a Windows guest on a Linux host has no transport (Question 1, OPEN). The domain declares exactly three PCIe root ports and a watchdog with action none; the SATA controller and PS/2 input are built into q35 and cannot be removed (verified on libvirt 10.0.0). `DiskCapEnforced` is false, as for Hyper-V: the child can grow to the parent's virtual size, so the host volume needs its own quota (security review, 2026-11-19).
- Enabled by `Bazaar:Vms:Kvm` (`ImageDirectory`, `VmDirectory`, optional `ConnectionUri`, default `qemu:///system`). Not yet run against a real KVM host: a `Category=Kvm` integration run and the Linux host spike remain open.

## Build design for WP7 to WP9 (PROPOSED, 2026-11-18)
Each part is its own module pair on Bazaar.Core: `Bazaar.Server.<Name>` plus `Bazaar.Server.<Name>.Abstractions`. None references `Bazaar.Server.csproj`; they meet only through abstractions, `Bazaar.Contracts` and the `vms` tables. The host composes them (doc 40 lists them). The cloud builds and tests them against fakes and loopback; the Hyper-V and KVM checks run later from the spike runbooks (S8, S9, S26) on the owner's PC.

**Transports (WP7): `Bazaar.Server.VmTransport`**
- The guest dials the host, never the reverse. The host listens, so a guest cannot be reached from outside.
- Host on Windows: one `AF_HYPERV` listener per service GUID (operator setting `vm.transport.service_guid`, declared in the settings registry by `Bazaar.Server.VmTransport`; the host registers it and reads the stored value on the listener's first wait with the fixed shipped default `HvSocketAddress.DefaultServiceId`, the GUID template for the default port; the value must be a non-empty GUID, and an invalid stored value falls back to the default and is reported). A non-default GUID must also be registered under `GuestCommunicationServices` on the host and passed to the guest agent, which already takes `--hvsock-service-id`; delivering that value to guests automatically is an open follow-up. It accepts only the VM ID the hypervisor reports for the peer, matched to `environment_vm.backend_ref`, so a guest cannot claim another VM. Both Windows and Linux guests use it; a Linux guest sees it as `AF_VSOCK` (Hyper-V transport of the Linux `vsock` stack).
- Host on Linux (KVM, later): `AF_VSOCK` on the host CID and a fixed port, peer CID matched to the VM. A Windows guest on a Linux host has no transport and stays deferred (Question 1).
- .NET has no endpoint type for either family, so the module builds the socket address by hand behind `IGuestConnectionListener`. Platform checks sit in the listener, never at call sites. A loopback listener (in-memory duplex) is the test implementation and is also what the guest-agent and channel tests use.
- `GuestChannel` (the host's `IGuestChannel`, doc 17 `IWorkspace` facade) speaks the existing `GuestFrameCodec` frames over one accepted connection: the first frame must be `Hello` with the expected `vmId` and the nonce the server issued at create; anything else closes the channel. Unknown types close the channel. Heartbeat misses are counted (3 intervals, then `Lost`).
- Exec limits come from the `vm.exec.*` settings. Output beyond `vm.exec.output_cap_mb` is dropped and flagged. The server, not the guest, stores output.
- Guest side, in `Bazaar.Guest.Agent` (the guest binary, not a server module): `HvSocketGuestTransport` (Windows guest, `AF_HYPERV` to the parent partition) and `VsockGuestTransport` (Linux guest, `AF_VSOCK` to CID 2) implement the existing `IGuestTransport`. `--transport stdio|hvsock|vsock` selects one; `stdio` stays the default for tests and Windows Sandbox.
- Per-VM key: doc 16 asks for host-signed requests. The signing envelope is a wire change, so it is **not** part of this build. Until the owner decides, identity rests on the hypervisor-authenticated peer ID plus the `Hello` nonce. This is recorded as a known gap, not a hidden one.
- Hostile-guest limits: a guest `PutFile` header over `MaxFileBytes` faults the channel at once (no draining); one file read has an overall `FileTransferTimeout` (default 30 minutes) so a trickling guest cannot hold the file gate. The guest agent takes its nonce from `--nonce-file` (deleted after reading), then `BAZAAR_GUEST_NONCE` (removed from the environment), then `--nonce`; `--nonce` is visible to every process in the guest, so boot units should use the file. The nonce is never passed to an exec, whatever the allowlist says.

**Scoped mounts (WP8): `Bazaar.Server.VmMounts`**
- Resolves Conflict 1: **the Soul mount is read-only on the host share, and the cookie profile is a working copy.** On attach the manager copies the profile into the guest's local disk through the guest channel (hash-checked `PutFile`); on detach and at clean session end it reads it back (`GetFile`, hash-checked), validates it, and writes it host-side. Hard taint detaches without write-back. This keeps doc 15's read-only rule and lets Chromium write its profile.
- The workspace share is read-write, on its own share and its own guest OS account, separate from the Soul share. Both VMs of one agent write it; single-writer lanes per subtree stay as stated above.
- The manager owns `scoped_mount` rows through `IScopedMountStore`. It never mounts a path that is outside the agent's own root, holds a key file, or sits under the audit or secrets paths (a deny list checked after path normalisation, symlink resolution and case folding). Guest paths come from the server, never from the guest.
- Canary credentials are planted in the Soul share on attach. Any read-use is reported through `ICanarySink`, which the agent-safety module turns into the page-and-freeze action (doc 15, PROPOSED there too).
- The OS-specific work (SMB share and ACL on Windows, mount and bind on Linux) sits behind `IMountProvider`. The cloud build ships a fake and a directory-based provider for tests.
- Every attach, detach and write-back writes an audit row through the existing audit abstraction.
- **Workspace share transport (PROPOSED, 2026-11-19; answers the owner's question on the shared folder between the Windows and Linux VM):** one host directory per agent, exposed to both VMs, behind one `IMountProvider` per transport so no feature code knows which is in use.
  - **SMB** is the default on Hyper-V, because it is the one transport both guests mount natively (Windows by `net use`, Linux by `cifs`). The share exists only on the agent's virtual switch address, is bound to a per-agent local account on the host, and requires SMB signing and encryption. No SMB traffic leaves the host-only switch, and the egress rules (WP9) do not allow it to any other address.
  - **virtiofs** is the default on KVM for the Linux guest, with SMB for the Windows guest. It is not used on Hyper-V, which has no virtiofs.
  - The transport per backend is an operator setting (`vm.share.transport`, shipped default as above, audited changes). Neither transport is a security boundary on its own: the path jail and the deny list in this section are enforced host-side before any directory is exposed.
  - Which transport is faster for builds and Godot exports is **not decided here**; the SMB throughput benchmark in the benchmark list settles the default, and the setting lets an operator override it without a code change.
- **Hard taint and kill switch (PROPOSED, from the security review):** detaching the share does not remove the guest-local profile copy. So a hard-taint or kill-switch detach also asks the host to pause the VM and discard or revert its disk (`IGuestScrubber`, wired to the VM lifecycle). Detach never waits on guest I/O and never writes back after taint. Canary fingerprints are stored, not forgotten, so a canary used after detach or a restart still pages. The profile folder is locked per agent, because both VMs of one agent share it.

**Egress (WP9): `Bazaar.Server.VmEgress`**
- Resolves Conflict 2: the egress owner is doc 15 ("Egress and ingress", `IEgressPolicy`); this doc owns the VM side (switch rules and the proxy). Doc 15 WP7 and this WP9 are one deliverable.
- Layers: (1) default-deny at the host virtual switch or bridge, allowing only the guest to the host proxy address and port; (2) a per-agent host proxy that checks each CONNECT and each plain HTTP request against that agent's domain allowlist, resolves names itself, and refuses private, loopback and link-local addresses after resolution; (3) the guest firewall is not trusted and is not used.
- (PROPOSED) The proxy also refuses the host's own interface addresses, so an allowlisted name that points at this server's public IP cannot reach host services, and on a CONNECT to a name it reads the first TLS ClientHello and requires the SNI to equal that name (any mismatch, missing name or encrypted_client_hello is refused), so a tunnel to an allowlisted name cannot be pointed at another site on the same CDN by its TLS server name (fronting by the HTTP Host header inside the TLS stream is not stopped, because the proxy does not intercept TLS); other protocols on an allowlisted host and port are relayed unchanged, and a protocol where the server speaks first must have the client send something within the header timeout.
- DNS: the guest has no resolver route. Proxy-unaware software gets nothing, by design; browser and tool profiles are set to the proxy. A DNS stub is not built (PROPOSED: not needed while the proxy resolves names).
- `IEgressPolicy.ApplyAsync(agent, rules)` sets the allowlist; `BlockAllAsync(agent)` is the kill-switch call and drops the proxy listener and the switch allow rule together. Allowlist growth goes through the approval gate (`vm.egress.allowlist_change_approval`, floor Tier 2); a shrink applies at once.
- (PROPOSED) The adapters from the gate's ports to the owner approvals service are the module `Bazaar.Server.VmEgress.Approvals.Security`. A growth request is filed as tool `vm.egress.allowlist_change`, target the additions key, so the owner's card is bound to the exact additions. Egress growth has no turn session, so the approval's session id is derived per agent (first 16 bytes of SHA-256 over `egress-allowlist|<agent id>`). Open tickets are indexed in memory per agent and additions key: a restart files a fresh card and the old card expires on its own. Taint is read through Security's `IAgentSessionTaintReader` (worst taint across the agent's active sessions, doc 15 "Reads for modules"), which the agent-turns component registers; without it the gate denies.
- Rule generation is pure and tested: Hyper-V extended port ACLs and an nftables ruleset are built from `EgressRules`. Executing them needs the Hyper-V runspace (PR #688) or a root helper, so it sits behind `IEgressEnforcer`; the cloud build ships a recording fake.
- (PROPOSED) The real enforcers are the module `Bazaar.Server.VmEgress.Enforcers` (`AddBazaarVmEgressEnforcers` replaces the unavailable default, either call order; it picks `HyperVEgressEnforcer` on Windows and `NftablesEgressEnforcer` on Linux, and stays fail closed, throwing at call time, when the host registers no `IEgressTargetResolver`). The host supplies that port: agent id to VM names (Hyper-V) and the guest bridge port (Linux, one per agent). Execution goes through a small `IHostCommandRunner` (allow-listed tools at fixed absolute paths, argv only, output cap, timeout, process-tree kill) and, for nft, an `IRootCommandRunner` with an argument allow-list (`-f -` and `list table bridge bazaar_egress_<tag>` only).
  - **Ordering guarantee.** Hyper-V: one constant script per VM adds the deny-all rules first, then removes rules that are not wanted, then adds the missing allow rules, so a replace never passes through a state with no rules and unchanged rules are not removed and re-added. nftables: the generated text is `add table`, `delete table`, body, loaded as one `nft -f -` transaction, so the swap is atomic. Calls for one agent are serialised.
  - **Read-back verification.** After applying, the enforcer reads the rules back (a second constant script on Hyper-V, `nft list table bridge` on Linux), compares them with the generated rules, and throws on any difference or runner failure; success is never reported on trust. `BlockAllAsync` applies deny-all, both directions, both families, with the same check, and tries every VM before reporting failures.
  - **Injection.** VM names and rules reach PowerShell only as JSON in an environment variable, never in script text or argv; VM names, the interface name and the table tag are re-validated with end-anchored patterns (the generators' own `$` anchors accept a trailing newline).
  - **UNVERIFIED.** No script or ruleset has run on a live host. Open facts for the NUC `Category=Hyperv` run (and a Linux run): how Hyper-V prints wildcard protocol, port and address in `Get-VMNetworkAdapterExtendedAcl`; that extended ACLs have no tag, so "the agent's ACLs" is every ACL on the agent's VM adapters; whether `Add-` rejects a duplicate rule; how nft prints the bridge hook priority (the comparison folds `filter` to `-200`); one guest interface per agent on Linux. Until that run, treat enforcement as proposed, not decided.
- Model traffic goes only to the gateway, which the default allowlist names. Credentials never cross the proxy in plain text from the guest: cookie injection at the proxy (doc 15) is a hook (`IProxyRequestFilter`), built later with the credential broker.
- Every allow, deny and rule change is an audit row (rate-limited per agent so a flood cannot swamp the log).

**Host composition (PROPOSED, 2026-11-22).** `Bazaar.Server.Host` composes the VM security modules in `AddBazaarVmSecurity`, right after the Vms module and before agent safety, through each module's own registration.
- Wired: egress policy and proxy host (`AddBazaarVmEgress`), the approval-backed egress gate and its approvals-service adapters, the guest transport (vsock listener on Linux; on Windows a Hyper-V socket listener that reads `vm.transport.service_guid` from the settings store on its first wait, falls back to the default for an invalid stored value and logs it, and does not cache the default when the store is down), the `vm.transport.service_guid` setting contribution, and agent safety's `NetworkAndMountContainment` and `VmGuestScrubber`, which resolve against these registrations.
- Scoped mounts are composed only when the `vm-mounts` component is not off, `VmMounts:AgentsRoot` is set, and the host has registered `IMountProvider`, `IMountGuestChannel` and `ICanarySink`. None has an implementation yet, so today the mount manager is absent and the kill switch's mount leg fails loudly. Registering the manager without them would make it unresolvable and break containment resolution.
- Fail closed until the executors land: `IEgressEnforcer` stays the unavailable enforcer (no Hyper-V or nftables executor), the egress allowlist store is in memory only, the taint reader (`IAgentSessionTaintReader`) comes from agent-turns, and egress growth is denied if it is absent, `IGuestScrubber` and `IVmPauser` stay unavailable unless a VM backend is configured (`Bazaar:Vms:WindowsSandbox`), and the egress options keep their shipped values because no setting contribution exists for `security.egress.default_allowlist` or `vm.egress.allowlist_change_approval`.

**Browser profiles (WP13)** wait for these three. They are one follow-up module, `Bazaar.Server.VmBrowser`, after WP7 to WP9 merge. The Windows Sandbox guest delivery question (doc 50 section 7) is separate and stays open.

## Data and interfaces

```csharp
public interface IVmBackend {
    Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct);
    Task StartAsync(VmHandle vm, CancellationToken ct);
    Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct);      // Graceful | Force
    Task SaveStateAsync(VmHandle vm, CancellationToken ct);
    Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct); // stopped or saved only
    Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct);
    Task RetireAsync(VmHandle vm, CancellationToken ct);
    Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct);
}

public interface IGuestChannel {
    Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct);
    Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct);
    Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct);
}
```

```sql
-- outline only; migrations per doc 04 conventions; this doc owns these tables (C16)
golden_image(id, os, version, sha256, guest_agent_version, state)    -- Building|Ready|Retired
environment_vm(id, agent_id, os, parent_image_id, backend_ref, state,
               vcpu, memory_mb, disk_cap_gb, created_at)
environment_checkpoint(id, vm_id, backend_ref, label, created_at)
scoped_mount(id, agent_id, vm_id, kind, host_path, guest_path, mode, detached_at)
               -- kind: soul | workspace; mode: ro | rw (open, see Conflicts 1)
exec_log(id, vm_id, started_at, argv_hash, timeout_s, exit_code, bytes_out)
               -- exec metadata only; security audit goes to doc 15's audit_log
```

### Settings
Registry owned by doc 03 [PROPOSED]. Changes are written to `audit_log` (doc 15).

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `vm.windows.vcpu` | int | 2 | install | operator | No (audit) |
| `vm.windows.memory_mb` | int | 4096 | install | operator | No (audit) |
| `vm.windows.disk_cap_gb` | int | 60 | install | operator | No (audit) |
| `vm.linux.vcpu` | int | 2 | install | operator | No (audit) |
| `vm.linux.memory_mb` | int | 2048 | install | operator | No (audit) |
| `vm.linux.disk_cap_gb` | int | 30 | install | operator | No (audit) |
| `vm.idle_save_minutes.linux` | int | 15 | install | operator | No (audit) |
| `vm.idle_save_minutes.windows` | int | 60 | install | operator | No (audit) |
| `vm.checkpoint_while_running` | bool | false | install | operator | No (audit); on only after benchmark |
| `vm.rebase_canary_agents` | int | 1 | install | operator | No (audit) |
| `vm.headed_vm` | enum: none, windows, linux | none | install | operator | No (audit); windows is the recommended choice |
| `vm.egress.allowlist_change_approval` | tier (doc 15) | Tier 2 | install | operator, may tighten only | Yes at the floor; initial per-agent list approved by owner |
| `vm.exec.default_timeout_min` | int | 15 | install | operator | No (audit) |
| `vm.exec.max_timeout_h` | int | 2 | install | operator | No (audit) |
| `vm.exec.output_cap_mb` | int | 10 | install | operator | No (audit) |
| `vm.exec.max_concurrent` | int | 4 | install | operator | No (audit) |

`agent_id` references the agent table owned by doc 06 (C04).

## Behaviour and edge cases
- **Ordering**: one lifecycle operation per VM at a time. Start waits for a completed Stop.
- **Host restart**: reconcile database state against backend state. A VM with no record is quarantined, never auto-deleted.
- **Guest hang**: heartbeat missed for 3 intervals (30 s each [PROPOSED]) leads to a force stop. Open execs are reported `Lost`.
- **Save during exec**: the server, not the guest, stores output, so only the in-flight chunk is at risk.
- **Pair skew**: the Windows and Linux VMs can drift apart after one is restored. Restore both together, or record the skew.
- **Workspace share**: both VMs of one agent write the inter-VM share. No cross-VM locking. [PROPOSED] single-writer lanes per subtree.
- **Hard taint** (doc 15): detaches the Soul folder from both VMs. Account browser profiles lose their cookies. The anonymous profile is unaffected.
- **Disk full**: the child cap holds. Exec returns an error and an alert is raised.
- **Security**: guests send only typed messages. Mount paths are fixed by the server. Audit and provider or App keys are never under a mounted path (C02, threat model P0). Pushes run host-side (C05).

## Testing
- **Unit (no Hyper-V)**: lifecycle state machine with a fake `IVmBackend`; image hash and parent checks; frame fuzzing; timeout and cancel ordering; hard-taint detach; PTY framing round trip.
- **Guest agent**: loopback transport abstraction on Linux CI; the Windows guest on a Windows runner.
- **Integration (real Hyper-V, NUC only, `Category=Hyperv`)**: create, exec, file round trip with hash check, save and restore, checkpoint and restore, egress only through the proxy, Chromium headed and headless in the pair (spike first).
- **Benchmark (not CI)**: idle and loaded RAM, boot-to-ready, save, restore and checkpoint times, SMB throughput, Godot headless export, Linux build time, parallel pairs.

## Work packages
1. `Bazaar.Contracts`: guest messages incl. PTY, framing, round-trip tests. (after contracts WP1)
2. Environment tables, repositories, real-Postgres tests. (after WP2)
3. Lifecycle state machine, per-VM lock, `IVmBackend` fake. (after provider-interface WP3)
4. Golden image recipe, hash and read-only verification. (after 3)
5. Hyper-V provider: runspace host, create, start, stop, save, checkpoint. (after 3)
6. Guest agent: exec, PTY streams, cancel, file transfer. (after 1)
7. Transports: spike HvSocket and AF_VSOCK, then implement. (after 6)
8. Scoped mounts: Soul folder (cookie profile, detach on taint) and workspace share, ACLs, guest mount. (after 2, 5, doc 15 isolation)
9. Egress enforcement at the switch. (after 5 and the egress design)
10. Startup reconciliation and audit rows. (after 2, 3)
11. Benchmark harness and report. (after 5, 6, 8) Module pair `Bazaar.Server.VmBench` (doc 40): runner, statistics, Markdown and JSON report, run-to-run comparison over an `IVmBenchHost`; the Hyper-V host adapter lands with WP5 to WP8.
12. KVM provider and Linux host spike. (after 3; later)
13. Browser profiles in the pair; third VM only if the headed and headless spike needs it. (after 6, 8, 9)

## Browser module build notes (WP13) [PROPOSED]
- Own module pair `Bazaar.Server.Browser` and `Bazaar.Server.Browser.Abstractions` (doc 40). It owns launch, session registry and cookie hand-off; Identity (doc 14) keeps profile storage, encryption and leases.
- Chromium runs in the agent's existing VM pair (`BrowserGuestOs`), headless by default. Argv is built host-side: profile directory in the guest, `--proxy-server` to the per-agent egress proxy, `--host-resolver-rules` so DNS only resolves through the proxy, `--remote-debugging-pipe` (no TCP debug port), never `--no-sandbox`.
- Account profiles get cookies over the guest channel by CDP `Network.setCookies` after the process starts (the cookie never goes on argv or disk in the guest beyond Chromium's own profile). Anonymous profiles never call the cookie source.
- Hard taint calls `DetachAccountSessionsAsync`: account sessions stop and cookies drop; anonymous sessions are unaffected. No egress route means no browser (fail closed).
- Conflict 1 (mount mode) stays open for doc 15; this module uses a guest-local profile directory and does not depend on the answer.

## Conflicts
1. **Soul folder mount mode vs cookie profile.** The mount mode (`scoped_mount.mode`) is not decided here. A read-only mount conflicts with Chromium writing its profile: either the profile is copied into the guest on attach and written back on detach, or the mount is read-write. PROPOSED resolution (2026-11-18): read-only share plus a guest-local working copy of the profile, written back on detach; see the build design above. Doc 15 confirms.
2. **Egress has no owner.** This doc depends on an egress design, but the doc index has no egress entry. PROPOSED resolution (2026-11-18): doc 15 owns the policy, this doc owns the VM-side enforcement; see the build design above. The owner confirms.
3. **Other docs must conform (not fixed here).** Doc 03 defines a different `IVmBackend` and must adopt this one. Doc 17 has a "Browser VM" role (C22) and a 10-minute command timeout (C36; the exec limits are now `vm.exec.*` settings here). Docs 14 (Q2) and 15 ("no-secrets VM") must drop the separate browser VM. Doc 15 must state that cookie profiles are mountable (C02).
4. **Superseded Soul deep-dive text.** `Bazaar.Soul.md` lines 26, 43 and 51 keep the WSL2 image, the unsourced "sub-second boot" claim and the Cloud Hypervisor question (C03, C22).
5. **Unverified platform facts**: the differencing-disk invalidation gotcha (no primary source), `AF_HYPERV` from .NET, Hyper-V management classes, saved-state sizes. Windows guest on a Linux host has no transport (OPEN, Question 1). Windows licensing gates deployment (Question 2).

## Questions for the owner
1. Defer the Windows guest transport until the KVM provider starts? *Recommend* yes; v1 is Windows-host only.
2. Get written answers on CALs and the Windows 11 host question before deploying agent Windows VMs? *Recommend* yes; this cost scales with agents.
3. Add PTY message types (`PtyOpen`, `PtyData`, `PtyResize`, `PtyClose`) to the guest protocol so the server can stream agent terminals (C31)? *Recommend* yes, under the same framing and limits. This is a wire-format choice, so it needs one answer.

MOVED TO SETTINGS: vm.windows.vcpu, vm.windows.memory_mb, vm.windows.disk_cap_gb, vm.linux.vcpu, vm.linux.memory_mb, vm.linux.disk_cap_gb, vm.idle_save_minutes.linux, vm.idle_save_minutes.windows, vm.checkpoint_while_running, vm.rebase_canary_agents, vm.headed_vm, vm.egress.allowlist_change_approval, vm.exec.default_timeout_min, vm.exec.max_timeout_h, vm.exec.output_cap_mb, vm.exec.max_concurrent (DECISIONS-LOG round 13)
