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
- **Guest agent**: one self-contained .NET 10 program, baked into both guest images. Runs commands, moves files and hosts terminal sessions. [PROPOSED]
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
- These `Pty*` messages are the guest transport for agent terminals. Docs 17 and 30 point here. The Bazaar.Windows local terminal is not a guest channel (C31).
- Auth: per-VM key generated at create, pinned by the host. The guest accepts only host-signed requests. [PROPOSED]
- Limits: exec timeout, output cap and concurrent execs per VM are settings (`vm.exec.*`). Execs run as a non-admin guest user, and a timeout kills the whole process tree. [PROPOSED]
- Guest-supplied paths are checked against the VM's mount list.

**Network and egress** [PROPOSED]: default-deny at the host switch or bridge (mechanism to verify on the NUC). The only route is a host egress proxy with a per-agent domain allowlist, used by browser profiles too. DNS goes through the proxy. Guest firewalls are not trusted. Allowlist changes need approval (`vm.egress.allowlist_change_approval`).

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
11. Benchmark harness and report. (after 5, 6, 8)
12. KVM provider and Linux host spike. (after 3; later)
13. Browser profiles in the pair; third VM only if the headed and headless spike needs it. (after 6, 8, 9)

## Conflicts
1. **Soul folder mount mode vs cookie profile.** The mount mode (`scoped_mount.mode`) is not decided here. A read-only mount conflicts with Chromium writing its profile: either the profile is copied into the guest on attach and written back on detach, or the mount is read-write. Doc 15 must decide; no decision here.
2. **Egress has no owner.** This doc depends on an egress design, but the doc index has no egress entry. The owner must assign it (doc 15 or a new doc).
3. **Other docs must conform (not fixed here).** Doc 03 defines a different `IVmBackend` and must adopt this one. Doc 17 has a "Browser VM" role (C22) and a 10-minute command timeout (C36; the exec limits are now `vm.exec.*` settings here). Docs 14 (Q2) and 15 ("no-secrets VM") must drop the separate browser VM. Doc 15 must state that cookie profiles are mountable (C02).
4. **Superseded Soul deep-dive text.** `Bazaar.Soul.md` lines 26, 43 and 51 keep the WSL2 image, the unsourced "sub-second boot" claim and the Cloud Hypervisor question (C03, C22).
5. **Unverified platform facts**: the differencing-disk invalidation gotcha (no primary source), `AF_HYPERV` from .NET, Hyper-V management classes, saved-state sizes. Windows guest on a Linux host has no transport (OPEN, Question 1). Windows licensing gates deployment (Question 2).

## Questions for the owner
1. Defer the Windows guest transport until the KVM provider starts? *Recommend* yes; v1 is Windows-host only.
2. Get written answers on CALs and the Windows 11 host question before deploying agent Windows VMs? *Recommend* yes; this cost scales with agents.
3. Add PTY message types (`PtyOpen`, `PtyData`, `PtyResize`, `PtyClose`) to the guest protocol so the server can stream agent terminals (C31)? *Recommend* yes, under the same framing and limits. This is a wire-format choice, so it needs one answer.

MOVED TO SETTINGS: vm.windows.vcpu, vm.windows.memory_mb, vm.windows.disk_cap_gb, vm.linux.vcpu, vm.linux.memory_mb, vm.linux.disk_cap_gb, vm.idle_save_minutes.linux, vm.idle_save_minutes.windows, vm.checkpoint_while_running, vm.rebase_canary_agents, vm.headed_vm, vm.egress.allowlist_change_approval, vm.exec.default_timeout_min, vm.exec.max_timeout_h, vm.exec.output_cap_mb, vm.exec.max_concurrent (DECISIONS-LOG round 13)
