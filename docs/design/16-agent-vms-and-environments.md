# Agent VMs and Environments

Status: DRAFT for owner review (2026-10-25) · Owner questions: 13
Depends on: `Bazaar.Soul.md` (topology, Passport, folders), `Bazaar.Server.md` (provider interfaces, service host), `Bazaar.md` (GC and governance rules) · Used by: agent runtime, egress and identity docs (not yet drafted), `Bazaar.Windows.md` (Studio handoff)

## Purpose
Defines the environment each agent works in: one Windows VM and one Linux VM, created from a versioned golden image and driven by the Bazaar.Server VM provider through a guest agent. Disks are disposable and rebuildable; identity, memory and credentials stay host-side.

## Decided inputs
- Two VMs per agent, Windows and Linux, matching the server OS. Headless by default; one VM may be fully headed on a more capable server. (`Bazaar.Soul.md` §2, owner 2026-10-24)
- Differencing disks off one golden image: Hyper-V differencing VHDX on Windows hosts, QEMU/KVM qcow2 backing files on Linux hosts. (`Bazaar.Soul.md` §2)
- Soul data lives outside the VM disk and reaches the VM as one scoped per-agent folder. (`Bazaar.Soul.md` §1–2)
- Passport may be mounted per agent; the owner accepted the risk. (`Bazaar.Soul.md` §1, owner 2026-10-24)
- No clipboard sharing, no host drive passthrough beyond the scoped folder, enhanced session mode off. (`Bazaar.Soul.md` §2)
- Roblox Studio runs only on the human's Bazaar.Windows desktop. Agents use headless Luau checks. Godot runs with `--headless`. (`Bazaar.Soul.md` §2, owner 2026-10-24)
- One OS-agnostic server build. Hyper-V is the first provider; all OS-specific code sits behind a provider interface. No Windows-only APIs outside a provider. (`CLAUDE.md`, owner 2026-10-24)

## Design

**Components**
- **VM provider** (`IVmBackend`): Hyper-V on Windows (first); KVM on Linux (later). Called only by the server, never from a guest. [PROPOSED]
- **Environment service**: owns the agent's VM pair, lifecycle, schedules and resource accounting. Serialises lifecycle operations per VM.
- **Guest agent**: one self-contained .NET 10 program, baked into each golden image, for both guest OSes. Runs commands and moves files. [PROPOSED]
- **Scoped mounts**: host-side shares, one per purpose, bound to one agent. [PROPOSED]
- **Egress**: enforced outside the guest (see Behaviour). Design owned by a separate doc.

**Lifecycle** [PROPOSED]: `Absent → Provisioning → Stopped ⇄ Running → Saved → Running`; `Stopped/Saved → Checkpointed → Restored`; `→ Retired`.
- *Create*: child disk from a pinned golden image version, static memory and vCPU, scoped mounts, boot, guest `Hello`.
- *Checkpoint*: only for stopped or saved VMs until running-checkpoint behaviour is benchmarked.
- *Restore*: rolls back in-VM work only. Soul data is host-side, so restore never rolls back memory or identity.
- *Retire*: stop, export audit rows, delete the child disk after retention. Soul data is kept.

**Golden image and rebase**
- Build from a recipe with pinned tool versions. Output: a versioned parent disk, a SHA-256 recorded in the database, the guest agent version recorded, and the parent set read-only.
- A child records its parent ID and hash. The provider refuses to start a child whose parent is missing or mismatched.
- Toolchain update = build version N+1. New agents use it. Existing agents move by a per-agent rebase: new child from N+1, Soul folder reattached, migration hook run. In-VM state is lost by design. Parent N is kept until its last child retires.
- Invalidation rule: the server never writes a parent after children exist. Enforced by ACL and hash check. The Hyper-V behaviour is unverified (primary page returned 404), so this is a design assumption.

**Guest protocol**
- Transport: Hyper-V sockets (`AF_HYPERV`, service GUID) for Windows guests on a Windows host; `AF_VSOCK` for Linux guests (Linux Integration Services). A Windows guest on a Linux host has no HvSocket path: **[OPEN]**.
- Framing: length-prefixed, versioned envelopes in `Bazaar.Contracts`. Overall wire format remains OPEN (no Protobuf).
- Messages: `Hello{agentVersion, vmId, nonce}`, `Exec{id, argv, cwd, envAllowlist, timeoutSec}`, `Output{id, stream, seq, bytes}`, `Cancel{id}`, `Exit{id, code, timedOut}`, `PutFile{path, size, sha256}` / `GetFile`, `Heartbeat`. Unknown types close the channel.
- Auth: per-VM key generated at create, pinned by the host. Guest accepts only host-signed requests. [PROPOSED]
- Limits [PROPOSED]: exec timeout default 15 min, max 2 h; output cap 10 MB per exec; 4 concurrent execs per VM; exec as a non-admin guest user; timeout kills the whole process tree.
- Guest-supplied paths are checked against the VM's mount list.

**Network and egress** [PROPOSED]
- Default-deny at the host switch or bridge (Hyper-V port ACL or KVM bridge rules). Mechanism to verify on the NUC.
- Only permitted route: a host egress proxy with a per-agent domain allowlist. DNS goes through the proxy. No direct TCP.
- Guest firewalls are not trusted.

**Resources** [PROPOSED starting values, unsourced except the Windows estimate]

| VM | vCPU | Memory (static) | Child disk cap |
|---|---|---|---|
| Windows (headless Server Core) | 2 | 4 GB | 60 GB |
| Linux (headless) | 2 | 2 GB | 30 GB |

- Capacity on a 32 GB NUC (unverified on the box): reserve about 4 GB for the host, plus an unsized share for PostgreSQL and the server. About 24 GB remains, so about 4 running pairs (6 GB each). Saved pairs need host disk for saved memory [unverified]. Benchmark before any number is committed.
- Static memory first. Dynamic memory is untested.

**Licensing** (research, not advice; needs counsel or Microsoft)
- Windows host: Standard covers 2 Windows Server VMs per host (16-core minimum). Datacenter is $6,771 list for unlimited VMs. Crossover is about 11 Windows VMs. This design gives one Windows VM per agent, so cost scales with agent count.
- Linux host: each Windows VM is separately licensed. About $588 per VM at list (8-core minimum), eligibility unverified. The Linux VM carries no Microsoft licence.
- Windows 11 Pro as a 24/7 host for headless guests: not clearly allowed. CAL position for agent VMs: unanswered.

**Hyper-V control from .NET** [PROPOSED]
- Drive the Hyper-V PowerShell module from a long-lived, out-of-process runspace inside the Hyper-V provider. CIM `root\virtualization\v2` is the alternative (classes unverified). No NuGet wrapper is confirmed; check NuGet before adding one.
- Service account in Hyper-V Administrators only. Parameters passed as typed values, never string-built. Agent IDs validated.

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
-- outline only; migrations per the implementation plan
golden_image(id, os, version, sha256, guest_agent_version, state)    -- Building|Ready|Retired
environment_vm(id, agent_id, os, parent_image_id, backend_ref, state,
               vcpu, memory_mb, disk_cap_gb, created_at)
environment_checkpoint(id, vm_id, backend_ref, label, created_at)
scoped_mount(id, agent_id, vm_id, host_path, guest_path, mode)      -- ro|rw
exec_log(id, vm_id, started_at, argv_hash, timeout_s, exit_code, bytes_out)
```

## Behaviour and edge cases
- **Ordering**: one lifecycle operation per VM at a time. Start waits for a completed Stop.
- **Host restart**: reconcile database state against backend state. A VM with no record is quarantined, never auto-deleted.
- **Guest hang**: heartbeat missed for 3 intervals (30 s each [PROPOSED]) leads to a force stop. Open execs are reported `Lost`.
- **Save during exec**: the server, not the guest, stores output, so only the in-flight chunk is at risk.
- **Pair skew**: a Windows VM and a Linux VM can drift apart after one is restored. Restore both together, or record the skew.
- **Shared folder**: both VMs of one agent write the same share. There is no cross-VM locking. [PROPOSED] single-writer lanes per subtree.
- **Disk full**: the child cap holds. Exec returns an error and an alert is raised.
- **Security**: guests send only typed messages. Mount paths are fixed by the server. Passport and audit are never under a mounted path (threat model P0).

## Testing
- **Unit (no Hyper-V)**: lifecycle state machine with a fake `IVmBackend`; image hash and parent checks; frame encode/decode with malformed-input fuzzing; timeout and cancel ordering.
- **Guest agent**: run over a loopback transport abstraction on Linux CI. Windows guest tested on a Windows runner with the same abstraction.
- **Integration (real Hyper-V, NUC only, tagged `Category=Hyperv`)**: create from a test image; exec; file round trip with hash check; save and restore; checkpoint and restore; egress allowed only through the proxy.
- **Benchmark (not CI)**: RAM at idle and under load, boot-to-ready, save and restore time, checkpoint time, SMB throughput, Godot headless export, Linux build time, pairs running in parallel.

## Work packages
1. `Bazaar.Contracts`: guest message types, framing, round-trip tests. (after contracts WP1)
2. Environment tables, migrations and repositories, real-Postgres tests. (after WP2)
3. Lifecycle state machine, per-VM lock, `IVmBackend` fake. (after provider-interface WP3)
4. Golden image recipe, hash and read-only verification. (after 3)
5. Hyper-V provider: runspace host, create, start, stop, save, checkpoint. Windows-only integration tests. (after 3)
6. Guest agent: exec, stream, cancel, file transfer over the transport abstraction. (after 1)
7. Transports: spike HvSocket and AF_VSOCK first, then implement. (after 6)
8. Scoped mounts: host SMB share per agent, ACLs, guest mount. (after 2, 5)
9. Egress enforcement at the switch. (after 5 and the egress doc)
10. Startup reconciliation and audit rows. (after 2, 3)
11. Benchmark harness and report. (after 5, 6, 8)
12. KVM provider and Linux host spike. (after 3; later)

## Conflicts
1. **Passport mount vs threat model.** Soul §1 (owner) allows a per-agent Passport mount. The threat model's P0 says never mount it, since a compromised agent reads all its own credentials. The owner accepted this. Not resolved here; see Question 1.
2. **"One scoped folder" vs two shares.** Soul §2 allows one folder. This design needs the Soul folder and the inter-VM share, which makes two. Not resolved; see Question 2.
3. **Superseded text still tagged DECIDED.** Soul §2 keeps the Server Core + nested WSL2 design, and its unsourced "sub-second boot" Cloud Hypervisor claim. Neither should carry weight. Recommend removing both.
4. **Differencing-disk invalidation** is a DECIDED gotcha with no primary source found.
5. **Windows guests on a Linux host** have no HvSocket path, so the protocol transport is undefined for that combination.
6. **Windows necessity.** Godot and Luau checks may not need Windows. The Windows VM is justified by Windows-only tooling that is not yet listed. Cost driver: a Windows VM per agent.
7. **Cloud Hypervisor** has no documented Windows-host path. Firecracker is Linux-only. Neither is a Windows-host option.
8. **Research caveats**: nested virtualization performance is unmeasured (no longer in this design). Hyper-V management classes, `AF_HYPERV` from .NET, and saved-state sizes are unverified.

## Questions for the owner
1. **Passport mount.** Keep the per-agent mount, or default to host-side brokering with short-lived tokens and make the mount opt-in? *Recommend* brokering by default: it limits what one compromised agent can read.
2. **Two mounts per agent.** Accept the Soul folder (read-only memory) plus one workspace share as an explicit exception to "one folder"? *Recommend* yes, both listed in `scoped_mount`.
3. **Both VMs for every agent?** Or create the Windows VM only for agents with Windows-only tooling? *Recommend* both as decided, with the Windows VM saved by default when idle.
4. **Starting sizes.** Confirm 2 vCPU, 4 GB (Windows) and 2 GB (Linux), plus the disk caps, as benchmark starting points? *Recommend* yes.
5. **Idle saves.** Save state after idle periods (Linux 15 min, Windows 60 min)? *Recommend* yes, values to be benchmarked.
6. **Checkpoints.** Stopped or saved VMs only until running checkpoints are benchmarked? *Recommend* yes.
7. **Rebase.** Per-agent rebase with a one-agent canary, keeping old parents until the last child retires? *Recommend* yes.
8. **Egress approvals.** Who approves each agent's domain allowlist? *Recommend* the owner approves the initial list, and changes are Tier 2 approvals.
9. **Windows on a Linux host.** Defer the Windows guest transport until the KVM provider starts? *Recommend* yes; v1 is Windows-host only.
10. **Licensing.** Get written answers on CALs and the Windows 11 host question before deploying agent Windows VMs? *Recommend* yes; this cost scales with agents.
11. **Headed VM.** Which VM may be headed on a bigger server? *Recommend* Windows, off by default.
12. **Exec limits.** Confirm the 15 min default, 2 h max, 10 MB output cap, 4 concurrent execs.
13. **Benchmark scope.** Drop the single-VM nested comparison, since two VMs are decided? *Recommend* yes, benchmark two VMs only.

## Length note
About 2,100 words including code sketches, above the brief's 500 to 1,300 range. The Lifecycle, Licensing and Testing sections are the first candidates to trim.
