# 74. Hardware probe and resource ledger

Status: PLANNED. Design intent only; no code, migration or pane exists yet for either module. The owner's direction of 2026-11-19 to adopt Resibo's hardware detection and tiering, live resource ledger, live diagnostic and capability drift check into Bazaar as separate modules is DECIDED (as direction; module names below are working names). Every design detail is PROPOSED unless it restates a DECIDED item. Owner questions: 8 (section 16).
Depends on: 03-server-host-and-providers.md (provider seam, settings registry, configurability rule), 04-postgres-schema-and-migrations.md (conventions), 16-agent-vms-and-environments.md (VM sizing, which this doc feeds), 31-local-coworker-npu.md (execution-provider chain, placement verification), 40-core-feature-modules-audit.md and BAZAAR-COMPONENTS.md (module shape, soft dependencies, `Principal` in section 4.2), 45-onboarding.md (setup plan and steps), 46-kodo-health-module.md (probe catalogue, module health contract), `constellation-render-core.md` and `constellation-scale-and-lod.md` (renderer tiers and budgets), 43-telemetry-and-crash-reporting.md (fingerprint and issue sink).
Used by: 16, 31, 45, 46, the Constellation renderer budgets, `Bazaar.Server.Vms`, `Bazaar.Client.LocalInference`, the issue filer module (sibling adoption doc, number OPEN).

## 1. Purpose

Bazaar already makes several decisions that depend on what the machine can do, and each one currently has its own private answer or no answer:

- Doc 16 sizes agent VMs from fixed defaults and says the numbers are unverified; "VM sizing and SMB sharing are OPEN (benchmark)" in CLAUDE.md. Nothing stops a fifth running pair from starting on a 32 GB host.
- Doc 31 picks a local-inference execution provider with a fixed chain per OS and a fake "hardware probe" in its tests, but no real probe exists.
- `constellation-scale-and-lod.md` section 6 says tiers "are chosen from measured device capability at startup" and has no component that measures it. `Bazaar.Constellation.Vulkan.VulkanProbe` knows only the GPU.
- Kodo (doc 46) reads disk, memory and clock through `IHostReadings`, but has no notion of which hardware the install is supposed to have, so a GPU that vanished after a driver update is invisible to it.

Resibo (the sister program) solved the same four problems with four small, well-tested pieces: a static hardware profile with a tier recommendation (`services/setup/hardware`), a live VRAM reservation ledger with TTL leases, a continuous wall/CPU/RSS classification of real traffic, and a capability drift check (`core/health`). The owner directed on 2026-11-19 that Bazaar adopt what benefits it, over-engineer where useful, and split conflicting choices into separate modules.

This doc defines two server modules, each with an Abstractions project, plus the client and provider modules around them:

- **`Bazaar.Server.HardwareProbe`**: detects CPU, RAM, GPU, NPU, disks, network adapters and virtualization support, merges what several provider modules report into one `HardwareProfile`, and assigns tiers. Re-probes on a schedule and on triggers, and diffs profiles.
- **`Bazaar.Server.ResourceLedger`**: a durable, crash-safe ledger of leases on scarce resources (VRAM, RAM, vCPU, VM slots, NPU sessions), a live diagnostic that classifies operations as CPU-, IO-, network- or memory-bound, and a capability drift checker that compares expected and actual execution paths and the hardware profile over time.

Neither module contains the OS-specific reads. Those are provider modules.

## 2. Decided inputs

- Owner direction, 2026-11-19: incorporate everything from Resibo that can benefit Bazaar; where something conflicts with Bazaar's design, make it a separate module rather than choosing; account for everything. DECIDED.
- Providers are plain modules on Core, with no selector (owner, 2026-11-19). Shared libraries hold contracts and logic only, with no third-party dependencies; providers are separate modules (CONVERGENCE.md section on shared libraries, owner direction of the same date).
- Every feature is its own module `Bazaar.Server.<Name>` plus `.Abstractions`, with a `Bazaar.Client.<Name>` module when it has a client side. No code goes into `Bazaar.Server` or `Bazaar.Client.Core` (CLAUDE.md; BAZAAR-COMPONENTS.md section 2.1).
- Actor attribution is `Principal` (`kind:id`, BAZAAR-COMPONENTS.md section 4.2, owner 2026-11-19).
- One OS-agnostic server build; anything OS-specific sits behind a provider; first tested host is Windows (CLAUDE.md, owner 2026-10-24).
- Client-side exceptions to "the server does all compute" include NPU inference (DECISIONS-LOG round 4, C17). A client probes itself because only it can see its own GPU and NPU. That is a read, not compute.
- Configurability rule (DECISIONS-LOG round 13): every undecided value is an operator setting with a safe default; DECIDED items and security invariants are not settings; settings may tighten, never loosen below the floor.
- Forward-facing compatibility rule (CLAUDE.md): target the newest .NET, use current APIs, no obsolete code paths. This shapes the capability drift checks in section 8.
- Doc 31: the NPU tier is accepted only after placement verification; NPU tier is refused on listed Intel driver builds. Doc 16: static memory first; one lifecycle operation per VM at a time.
- Doc 46: unknown never counts as green; probes are non-critical unless the catalogue says otherwise; new probes are an update of the signed catalogue, not a setting.

## 3. What this doc does not decide

- The VM sizing numbers. Doc 16 owns them and they stay OPEN until the benchmark. This doc supplies the probe, the ledger and an advisor that proposes numbers; it does not set them.
- The Constellation budgets. `constellation-scale-and-lod.md` owns them. This doc supplies the tier that selects among them.
- The local inference chain per OS. Doc 31 owns it. This doc supplies facts and the lease, and adds only the rule that a failed lease or a drifted placement moves the session down the chain.
- The issue filer. A sibling adoption doc owns it; this doc only emits events it can consume (section 9). Its module id is OPEN.
- Tier coefficients. Resibo's own scorer says its coefficients are an interim heuristic that must be calibrated by bench data (`scoring.py`, `reasoned=False`). Bazaar keeps that honesty (section 5.3): shipped tables are labelled uncalibrated until a benchmark run sets them.
- Doc 59 (GUI VM density). It is not in this checkout (it is presumably in an open PR). Section 12 states what this doc assumes about it.

## 4. Modules and ids

| Module | Id | Kind | Role |
|---|---|---|---|
| `Bazaar.Server.HardwareProbe.Abstractions` | n/a | contracts | `IHardwareFacetProvider`, `IHardwareProfileReader`, `IHardwareTierSource`, profile and tier records, drift events |
| `Bazaar.Server.HardwareProbe` | `hardware-probe` | feature | merge, tiering, schedule, profile store, drift diff, admin endpoints |
| `Bazaar.Server.HardwareProbe.Windows` | `hardware-probe-windows` | provider | WMI, registry VRAM, DXGI, Windows ML device list, Hyper-V capability, SMART |
| `Bazaar.Server.HardwareProbe.Linux` | `hardware-probe-linux` | provider | `/proc`, `/sys`, `lspci`, DRM sysfs, `nvidia-smi` and `rocm-smi` when present, `/dev/kvm`, NPU devices |
| `Bazaar.Server.HardwareProbe.ReportImport` | `hardware-probe-report-import` | provider | CPU-Z and HWiNFO text report import, consent-gated, default off |
| `Bazaar.Server.HardwareProbe.Vulkan` | `hardware-probe-vulkan` | provider | adapts the existing `VulkanProbe` into GPU facets (reuse, section 13) |
| `Bazaar.Client.HardwareProbe` | `client-hardware-probe` | client feature | probes the device, reports it, caches the last tier locally |
| `Bazaar.Client.HardwareProbe.Android` | `client-hardware-probe-android` | client provider | Android memory class, Vulkan, NNAPI and QNN facts, thermal state |
| `Bazaar.Server.ResourceLedger.Abstractions` | n/a | contracts | `IResourceLedger`, `ILeaseHolder`, `IDiagnosticSink`, `ICapabilityCheck`, records |
| `Bazaar.Server.ResourceLedger` | `resource-ledger` | feature | pools, leases, reconcile, queue, live diagnostic, drift registry |

Each is a component with a manifest; `hardware-probe` and `resource-ledger` are first-party, in-process, and soft-depend on `kodo` and `audit`. `resource-ledger` hard-depends on `hardware-probe` for pool capacities (it reads the profile through `IHardwareProfileReader`, never probes, which is Resibo's division of labour). Provider modules depend on `hardware-probe` only. Hard edges stay acyclic; Kodo and the issue filer are soft (BAZAAR-COMPONENTS.md section 4.1). Component ids follow the kebab-case rule; the working names are PROPOSED, in the same way doc 46 section 4 labels Kodo's.

Client modules hold no server code. The wire types (`HardwareProfileDto`, `TierReport`) live in `Bazaar.Contracts` under the namespace `Bazaar.Contracts.HardwareProbe`, so the client module never references a server module.

## 5. Hardware probe design

### 5.1 Facets, not one detector per OS

Resibo selects one `HardwareDetector` per OS at registry init (`detect.py`, `default_detector`). The owner direction here is "providers are plain modules, no selector". So Bazaar models the probe as **facet providers**. Every enabled provider module registers an `IHardwareFacetProvider`; the module runs all of them and merges the claims. A provider that does not apply to the running OS reports `NotApplicable` and contributes nothing, which is not an error and not a selection.

```csharp
// Bazaar.Server.HardwareProbe.Abstractions
public enum FacetKind { Cpu, Memory, Gpu, Npu, Disk, Network, Virtualization, Power, Thermal }

/// One fact about one device, from one provider, with how much to trust it.
public sealed record FacetClaim(
    FacetKind Kind,
    string DeviceKey,            // stable key: e.g. "gpu:pci:10de:2684", "disk:<serial hash>", "cpu:0"
    string Attribute,            // e.g. "vram_bytes", "vendor", "discrete", "driver_version", "model"
    string Value,                // canonical invariant text
    string Source,               // provider id, e.g. "windows.registry", "vulkan.heap"
    ClaimConfidence Confidence); // Authoritative > Reported > Heuristic > Imported

public enum ClaimConfidence { Imported = 0, Heuristic = 1, Reported = 2, Authoritative = 3 }

public interface IHardwareFacetProvider
{
    string ProviderId { get; }
    ProbeApplicability Applicability { get; }          // Applicable | NotApplicable (wrong OS or missing tool)
    Task<FacetResult> ProbeAsync(ProbeContext context, CancellationToken ct);
}

public sealed record FacetResult(IReadOnlyList<FacetClaim> Claims, IReadOnlyList<ProbeNote> Notes);
public sealed record ProbeNote(string Code, string Detail);   // e.g. "lspci.missing", "wmi.timeout"
```

Merge rule (carried from Resibo, generalised): for each `(DeviceKey, Attribute)` the claim with the highest `Confidence` wins outright; claims are never averaged and a lower-confidence claim is never used as a tiebreak. This is exactly Resibo's lesson that `AdapterRAM` is a capped 32-bit value while the registry's `HardwareInformation.qwMemorySize` is correct (a real 12 GB card reported about 2 GB; `detect.py` `_resolve_vram_gb`). The Windows provider reports the registry value as `Authoritative` and `AdapterRAM` as `Heuristic`; Vulkan's device-local heap is `Reported`. Disagreement between two claims at different confidences is not an error but is recorded as a `ProbeNote` (`claim.conflict`) so Kodo and the admin view can show it. A `None` is never guessed: an attribute with no claim stays absent, and consumers treat absent as unknown (Resibo: "never guess a number").

Device identity: Resibo identifies GPUs by name string. Bazaar uses a stable `DeviceKey` (PCI vendor and device ids where available, otherwise a hash of the name plus index) so that drift detection can tell "driver update renamed the adapter" from "adapter removed". Names are display only.

### 5.2 What is probed

| Facet | Facts | Windows provider | Linux provider |
|---|---|---|---|
| Cpu | model, physical cores, logical threads, base clock, ISA flags that matter (AVX2, AVX-512, AMX), hypervisor present | WMI `Win32_Processor`; flags via .NET `System.Runtime.Intrinsics` | `/proc/cpuinfo` unique `physical id` and `core id` pairs, falling back to thread count when absent (Resibo `_read_proc_cpuinfo`) |
| Memory | total, available, speed, swap or pagefile | WMI `Win32_ComputerSystem` | `/proc/meminfo` |
| Gpu | vendor, discrete or integrated, VRAM, compute API family, driver version | WMI plus registry VRAM plus DXGI | `lspci` class lines, `/sys/class/drm/*/device/mem_info_vram_total`, vendor tools when installed |
| Npu | present, vendor, driver version, driver on the doc 31 refused list | Windows ML and OpenVINO device enumeration; WMI has no standard NPU class (Resibo left `npus=()` for this reason) | sysfs accel nodes (`/sys/class/accel`) |
| Disk | volumes, free and total, medium (NVMe, SSD, HDD), SMART health | `Get-PhysicalDisk` | `/sys/block`, `smartctl` when present |
| Network | adapters, link speed | CIM | `/sys/class/net` |
| Virtualization | CPU virtualization extensions on, hypervisor role or `/dev/kvm` usable, nested virtualization, Windows Sandbox feature | Hyper-V feature state, `HypervisorPresent` | `/dev/kvm`, `/proc/cpuinfo` flags |
| Power and Thermal | on battery, UPS present, thermal throttling | WMI battery, thermal zone | `/sys/class/power_supply`, thermal zones |

Resibo's discrete versus integrated rule is kept (`_classify_discrete`): "Arc" in an Intel adapter name means discrete, a bare "Intel(R) Graphics" is integrated, because Arrow Lake integrated GPUs report that generic name. This is the kind of fact that needs a regression test with the real string (section 14).

The Resibo PowerShell gotcha is also kept as a provider-internal test: `ConvertTo-Json` unwraps a one-element array into a bare object, so the WMI parser normalizes dictionary-or-list for every collection. In Bazaar the Windows provider should prefer CIM calls from .NET over a PowerShell subprocess where a managed API exists, and use the subprocess only as a fallback; doc 16 already calls a PowerShell runspace for Hyper-V control. Which is used is a provider internal and OPEN (question 5).

Subprocess rule (security): provider subprocess calls use fixed argument vectors, a timeout (Resibo uses 30 s for PowerShell and 10 s for `lspci`), no shell string building, and a missing tool degrades to "no facts" plus a `ProbeNote`, never a failed probe (Resibo: "degrade to zero detected GPUs rather than failing the whole profile").

### 5.3 Tiers and vocabulary

Resibo's three labels (`lightweight`, `balanced`, `maximum_accuracy`) exist to name OCR presets, and its scorer uses one heuristic over threads, RAM and best-GPU VRAM. Bazaar has more consumers, so it needs more than one answer. PROPOSED vocabulary:

- **Tier names**: `Floor`, `Light`, `Standard`, `Heavy`, `Max`. `Floor` is what an unknown or absent fact yields. Names are stable identifiers; display strings are localised.
- **Axes**: a profile is tiered per axis, because "good CPU, no GPU" is the normal case: `Compute` (CPU), `Memory`, `Graphics` (GPU for rendering), `Accelerator` (GPU and NPU for inference), `Storage`, `Virtualization` (VM capacity). Each consumer reads the axis it needs. A single `Overall` tier is the minimum of the axes the consumer lists, never a hidden average.
- **Device class** (not a tier): `Server`, `Desktop`, `Phone`, `Tablet`. The Constellation budget table's "PC tier" and "phone tier" are the pair (class, Graphics tier); the mapping is in section 12.2.

The tier table is **data**: a versioned JSON document `tier-table.json` shipped in the module and validated at start (thresholds per axis per tier, with a `calibration` field of `uncalibrated` or `bench:<report id>`). Resibo's scorer is a seam (`HardwareScorer`, `RecommendedTier.reasoned`) with the honest flag that its heuristic is not bench-calibrated; Bazaar keeps both: `IHardwareTierSource` is the seam and `TierReport.Calibrated` is false for the shipped table. The first shipped table is a reasoned heuristic in the same spirit as Resibo's three rules (a thread-and-RAM floor, then a RAM-plus-VRAM ceiling), with numbers marked uncalibrated. Calibration is a benchmark task (section 15), and calibrating a table is a new table version, not a code change.

**Overrides.** An operator may pin an axis tier per device (`hardware.tier.override.<axis>`) because the heuristic can be wrong. An override changes defaults and advice only. It never raises a ledger capacity (section 7.2); it can lower one. Every override change is an audit event with a `Principal`.

### 5.4 Reports for accuracy (Resibo section 5.3)

Resibo reads a CPU-Z or HWiNFO text report from `Documents` to correct GPU shader counts that WMI misreports, with consent asked in the wizard (`report_import.py`). Bazaar adopts this as the separate provider module `hardware-probe-report-import` with these changes:

- It is off by default and enabled only through the setup plan or an audited setting, never silently. Resibo's developer-mode silent scan is not adopted; Bazaar has one consent path (privacy default, configurability rule).
- It reads only the directories the operator lists (`hardware.report_import.paths`), not `Documents` by default.
- Its claims have `Imported` confidence, the lowest, and set `Source = os_probe+external_report` style provenance in the profile so a profile always says how it was derived.
- The parser is best-effort and never throws; an unparseable report changes nothing.

### 5.5 When probing runs

- At install, as a setup step (doc 45 `ISetupStep`, `hardware.profile`, order after `database.schema`, before VM provisioning). It is idempotent; a re-run replaces the current profile with a new version.
- At every server start, a quick probe that compares against the stored profile (drift check, section 8).
- On a schedule (`hardware.reprobe_hours`, default 24) and on triggers: a VM backend reports a device error, a local inference session degrades, the owner presses re-detect, or the OS reports a device change.
- Clients probe at start and when the app or driver version changes, and report over the authenticated connection. The server stores one profile per device.

Probing is cheap but not free. It runs in Kodo's `slow` bulkhead pool where Kodo is present, and inside a hard timeout otherwise.

### 5.6 Client probing

Only a client can see its own GPU, NPU and thermal state, which doc 31 and the Constellation renderer need. `Bazaar.Client.HardwareProbe` runs the same facet model on the client with client-side provider modules (Windows and Linux heads reuse the server provider code through shared facet code only if that is free of server references; otherwise they are separate small modules, OPEN, question 6). It sends a `HardwareProfileDto` through the wire; the server stores it and returns a `TierReport`. The client caches the last report so a cold start without a connection still has a tier. With no cached report the client uses `Floor` for the Graphics and Accelerator axes, so it fails small and safe, and the first frame is not blocked on the network.

The client never sends content. A profile contains hardware descriptions, which are personal-adjacent (a device fingerprint). It is stored per device under the privacy rules, reported to the owner and to no one else, and redacted by default in telemetry (section 11).

## 6. Resource ledger design

### 6.1 Resources and pools

A **pool** is a named capacity on a device. A **lease** is a claim on a pool by a holder, with a TTL.

```csharp
// Bazaar.Server.ResourceLedger.Abstractions
public enum ResourceKind { VramMb, RamMb, VCpu, VmSlot, NpuSession, DiskMb }

public sealed record PoolId(ResourceKind Kind, string DeviceKey);     // "vram_mb" on "gpu:pci:8086:e20b"

public sealed record PoolState(PoolId Id, long CapacityUnits, long HeldUnits, long ReservedHeadroomUnits,
                               int ActiveLeases, int Waiters, string CapacitySource);

public sealed record LeaseRequest(
    Principal Holder,                 // who holds it, in kind:id form (agent:charles, service:vm-lifecycle)
    PoolId Pool,
    long Units,
    string Purpose,                   // short label, never content (e.g. "vm.start", "local.session")
    string IdempotencyKey,            // retried reserve returns the same lease
    LeaseMode Mode = LeaseMode.TryNow,
    TimeSpan? Ttl = null,
    int Priority = 0);

public enum LeaseMode { TryNow, Queue }

public sealed record Lease(Guid LeaseId, PoolId Pool, long Units, Principal Holder, long Fence,
                           DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt, LeaseState State, string? EndReason);

public enum LeaseState { Active, Released, Expired, ForceReleased, Unconfirmed }

public abstract record LeaseOutcome;
public sealed record LeaseGranted(Lease Lease, PoolState Pool) : LeaseOutcome;
public sealed record LeaseRejected(RejectReason Reason, PoolState? Pool) : LeaseOutcome;     // a normal answer, not an error
public sealed record LeaseQueued(Guid TicketId, int Position) : LeaseOutcome;

public enum RejectReason { InsufficientCapacity, UnknownPool, InvalidRequest, PolicyDenied, LedgerUnavailable }

public interface IResourceLedger
{
    Task<LeaseOutcome> ReserveAsync(LeaseRequest request, CancellationToken ct);
    Task<Lease> RefreshAsync(Guid leaseId, long fence, CancellationToken ct);   // throws LeaseLapsedException on a lapsed lease
    Task<Lease> ReleaseAsync(Guid leaseId, string reason, CancellationToken ct); // idempotent
    Task<Lease?> GetAsync(Guid leaseId, CancellationToken ct);
    Task<PoolState> GetPoolAsync(PoolId pool, CancellationToken ct);
    Task<IReadOnlyList<Lease>> ListActiveAsync(CancellationToken ct);
}
```

Kinds map to Bazaar's scarce things: `VramMb` for GPU inference and any server-side GPU use; `RamMb` for host RAM committed to running VMs and managed Postgres; `VCpu` for VM processors; `VmSlot` for a counted number of concurrently running VM pairs (a pair counts as two Windows plus Linux units or one pair unit, see section 12); `NpuSession` for the single-session-per-device NPU rule of doc 31; `DiskMb` for child-disk growth reservations against a volume. The kind list is closed in v1 (a new kind is a code change plus a migration of the check constraint) because kinds carry semantics; pools are open (any device).

### 6.2 Semantics carried from Resibo

- **A rejection is a normal answer** (Resibo `reserve()` returns an outcome, nothing raises on the rejection path). The caller falls back (CPU, smaller VM, queue) rather than failing. `LeaseRejected` has no error code.
- **Unknown pool means reject, fail closed.** Resibo's `NoHardwareProfile` reader publishes nothing, so every reservation is `UNKNOWN_DEVICE`; granting against an unknown ceiling is the unsafe answer and outranks degrade-gracefully. Bazaar keeps this: with no probe profile, every `Reserve` is `UnknownPool`. The VM lifecycle must treat that as "cannot start" unless the operator pinned capacities (`ledger.static_capacity`, the analogue of Resibo's `StaticHardwareProfile`, which is a real deployment shape: an operator pinning ceilings).
- **TTL with heartbeat.** A lease expires unless its holder refreshes. Default TTL `ledger.lease_ttl_seconds` 120 (Resibo's starting value, which its own bench checklist still marks as unmeasured). Holders refresh at one third of the TTL.
- **A lapsed lease is never revived.** `Refresh` on an expired lease throws `LeaseLapsedException`; the holder discovers it lost the claim and aborts or re-reserves. Silently extending would hand the same memory to two holders (Resibo section 11).
- **Release is idempotent.** Releasing a released lease returns the stored record, so a retry after a dropped response cannot fail a cleanup path.
- **Sweep before decide.** Every reserve sweeps expired leases first inside the same transaction, so a crashed holder's stale claim never refuses a live one. A periodic sweeper also runs for quiet systems (Resibo hands this to its background workers; Bazaar runs it as a hosted service in the module).

### 6.3 Crash safety (what Resibo's in-memory ledger does not give)

Resibo's ledger is an in-memory dictionary behind a lock in the Health process. If that process restarts, every lease is forgotten while the VRAM it described is still in use. Bazaar's server restarts too (updates, crashes; doc 46 designs for crash-only operation), and a VM that is still running after the server restarted is a real hazard. So the Bazaar ledger is **durable**:

1. **Postgres is the source of truth.** Leases live in `resource_lease` (section 6.5). `Reserve` is one transaction: lock the pool row (`SELECT ... FOR UPDATE`), sweep expired leases for that pool, compute held units, insert the lease or return the rejection. The idempotency key has a unique index, so a retried call returns the same lease.
2. **Fencing token.** Each pool has a monotonic `fence` counter, incremented on every grant. A lease carries its fence. Consumers that perform the guarded action present the fence (for example, the VM lifecycle passes it to the backend start call and records it in `environment_vm`), and a stale holder can be recognised after the fact. This follows the fencing pattern of doc 46 section 7.6.
3. **Restart reconcile.** On server start, all `Active` leases become `Unconfirmed` and hold their capacity (so nothing new is over-granted) for `ledger.reconcile_grace_seconds` (default equal to the TTL). Each holder module re-confirms its leases through `IResourceReconciler` (the VM lifecycle compares against actual backend state, as doc 16's "host restart: reconcile database state against backend state" already requires). A lease that is re-confirmed returns to `Active` with a fresh TTL. A lease that is not confirmed in time is `Expired`, and if the holder is a VM that is in fact still running, the VM lifecycle quarantines it (doc 16: "a VM with no record is quarantined, never auto-deleted") instead of the ledger deleting anything. The ledger never kills a holder; it only stops counting its claim, and reports an `orphan.suspected` finding to Kodo so a human sees it.
4. **Clock safety.** TTLs are computed from the database clock (`now()`), not from caller clocks, and tests inject a clock. A forward clock jump would expire leases early; the sweeper therefore refuses to expire more than `ledger.max_expire_burst` leases in one pass without raising a finding (a mass expiry is a clock fault until proven otherwise, and Kodo has a clock drift probe already).
5. **Holder death while the server is fine.** The common case: a local inference session or a guest process dies. Its heartbeat stops and the TTL lapses, exactly Resibo's design. The VM lifecycle in particular does not rely on a heartbeat alone: it releases the lease in the same transition that moves the VM to `Stopped`, `Saved` or `Retired` (doc 16 lifecycle), and the TTL is the backstop.
6. **Ledger unavailable.** If Postgres is down, `Reserve` returns `LedgerUnavailable`, which callers treat as a rejection (fail closed). Leases are not cached in memory as a substitute; a degraded in-memory mode was considered and rejected because it reproduces Resibo's restart hole.

Queueing (`LeaseMode.Queue`) is an addition Resibo does not have (Resibo callers fall back to CPU or queue on their own). It is useful for VM start storms: waiters are ordered by priority then arrival, each has a deadline, and a grant wakes the next waiter through Postgres NOTIFY (the channel `ledger_pool`, in line with doc 04 and CLAUDE.md's NOTIFY-for-fan-out rule). It is off by default (`ledger.queue_enabled` false) because a queue needs fairness policy that nobody has decided (question 4).

### 6.4 Capacity, headroom and overcommit

Pool capacity comes from the profile, never from probing in the ledger: `CapacityUnits = measured total - headroom`. Defaults (PROPOSED, all settings):

| Pool kind | Headroom default | Overcommit | Note |
|---|---|---|---|
| `VramMb` | 10 percent of device VRAM, minimum 512 MB | 1.0 | no overcommit; an out-of-memory on the GPU is a crash, not slowness |
| `RamMb` | 4096 MB plus the managed Postgres budget | 1.0 | 4 GB is doc 16's own unverified host reserve; the Postgres share is OPEN in doc 16 |
| `VCpu` | 2 logical threads | 2.0 | vCPUs can be shared; 2.0 is a starting value, not measured |
| `VmSlot` | none | 1.0 | derived: see 12.1 |
| `NpuSession` | none | 1.0 | one session per model per device (doc 31 scheduling) |
| `DiskMb` | per volume free-space floor of Kodo's `host.disk.free` | 1.0 | a lease must keep the volume above Kodo's threshold |

Settings may raise headroom or lower capacity freely (tightening) but may not set capacity above the measured total, and may not set overcommit above the floor for `VramMb`, `RamMb`, `NpuSession` (hard-limits, security-adjacent invariants: those are crashes). Overcommit of `VCpu` above a configured ceiling needs an owner-tier approval change (doc 15 tiers) because it changes agent behaviour under load.

### 6.5 Data tables

Namespace `resource-ledger`, folder `src/Bazaar.Server.ResourceLedger/Data/Sql/resource-ledger/`, numbering local (BAZAAR-COMPONENTS.md section 5). No foreign keys into another component's tables; `holder` is text in `kind:id` form.

```sql
-- 0001_pools_and_leases.sql
create table resource_pool (
  kind            text   not null check (kind in ('vram_mb','ram_mb','vcpu','vm_slot','npu_session','disk_mb')),
  device_key      text   not null,
  capacity_units  bigint not null check (capacity_units >= 0),
  capacity_source text   not null,            -- 'profile:<version>' | 'static' | 'operator'
  overcommit      numeric(4,2) not null default 1.0,
  fence           bigint not null default 0,
  updated_at      timestamptz not null default now(),
  primary key (kind, device_key)
);

create table resource_lease (
  lease_id     uuid primary key,
  kind         text   not null,
  device_key   text   not null,
  holder       text   not null,                -- Principal text form, e.g. 'service:vm-lifecycle'
  units        bigint not null check (units > 0),
  purpose      text   not null,
  idem_key     text   not null,
  fence        bigint not null,
  state        text   not null check (state in ('active','released','expired','force_released','unconfirmed')),
  granted_at   timestamptz not null default now(),
  expires_at   timestamptz not null,
  ended_at     timestamptz,
  end_reason   text,
  foreign key (kind, device_key) references resource_pool (kind, device_key),
  unique (idem_key)
);
create index resource_lease_active on resource_lease (kind, device_key) where state in ('active','unconfirmed');
create index resource_lease_expiry on resource_lease (expires_at) where state in ('active','unconfirmed');

-- 0002_diagnostic.sql: rolled-up findings only, not raw samples (raw samples live in memory, section 7)
create table diagnostic_baseline (
  service text not null, operation text not null,
  expected_class text not null, source text not null,    -- 'declared' | 'bench:<id>'
  primary key (service, operation)
);
create table diagnostic_finding (
  finding_id uuid primary key, service text not null, operation text not null,
  baseline_class text not null, observed_class text not null, sample_count int not null,
  degraded boolean not null, detected_at timestamptz not null default now()
);

-- 0003_hardware_and_drift.sql  (owned by the hardware-probe component; shown here for review only)
-- hardware_profile(device_id, version, detected_at, source text, profile jsonb)
-- hardware_drift_event(event_id, device_id, from_version, to_version, kind, severity, detail jsonb, detected_at)
-- capability_finding(check_id, subject, expected_path, actual_path, outcome, detail, checked_at)
```

The hardware profile tables belong to `hardware-probe` in its own namespace `hardware-probe` (`0001_profile.sql`, `0002_drift.sql`); the third block above only shows their shape. Ownership: `resource-ledger` reads profiles through `IHardwareProfileReader`, never the tables. `capability_finding` belongs to `resource-ledger` (namespace `resource-ledger`, `0004_capability.sql`).

### 6.6 Settings registry rows

Keys follow doc 03 conventions, written to the audit log on change (doc 15). "Tighten only" means the setting may be moved in the safer direction freely and in the unsafe direction only with owner-tier approval.

| Key | Type | Default | Scope | Notes |
|---|---|---|---|---|
| `ledger.lease_ttl_seconds` | int | 120 | install | Resibo's starting value; unmeasured |
| `ledger.reconcile_grace_seconds` | int | 120 | install | section 6.3 |
| `ledger.max_expire_burst` | int | 5 | install | mass-expiry guard |
| `ledger.queue_enabled` | bool | false | install | section 6.3 |
| `ledger.static_capacity` | json map | empty | install | pins ceilings when no profile exists; may only lower a measured capacity once a profile exists |
| `ledger.headroom.<kind>` | int | table above | install | tighten only below the table values |
| `ledger.overcommit.vcpu` | decimal | 2.0 | install | ceiling needs owner-tier approval to raise |
| `hardware.reprobe_hours` | int | 24 | install | 0 disables schedule, not start or trigger probes |
| `hardware.tier.override.<axis>` | enum | none | per device | audited |
| `hardware.report_import.enabled` | bool | false | install | consent; section 5.4 |
| `hardware.report_import.paths` | string list | empty | install | |
| `hardware.drift.notify_severity` | enum | warn | install | lowest severity sent to channels |
| `diag.window_size` | int | 64 | install | Resibo `DEFAULT_WINDOW_SIZE` |
| `diag.min_samples` | int | 5 | install | below this the class is `Indeterminate` |
| `diag.cpu_bound_ratio` | decimal | 0.75 | install | Resibo `CPU_BOUND_RATIO` |
| `diag.waiting_ratio` | decimal | 0.35 | install | Resibo `WAITING_RATIO` |
| `diag.network_latency_ms` | int | 250 | install | Resibo `NETWORK_LATENCY_MS` |
| `diag.memory_bound_rss_mb` | int | 256 | install | Resibo `MEMORY_BOUND_RSS_MB` |
| `capability.check_interval_hours` | int | 6 | install | Resibo `periodic_check_interval_hours` |
| `capability.check_on_start` | bool | true | install | not loosenable (always on at start; the setting only controls the periodic job) |

## 7. Live diagnostic

Resibo's `LiveDiagnostic` runs the bench suite's wall-time, CPU-time and RSS classification against real traffic to catch **soft degradation**: an engine that is normally CPU-bound behaving as if network-bound (a fallback to a slower path, a throttling GPU) passes every up or down check (`live_diagnostic.py`; deep-dive section 3).

### 7.1 Adopted as is

- Classes: `CpuBound`, `IoBound`, `NetworkBound`, `MemoryBound`, `Negligible`, `Indeterminate`.
- Median over a bounded window (default 64), not a mean, so one cold start cannot reclassify an operation. Fewer than 5 samples is `Indeterminate`, never a guess.
- Baselines are **declared**, not learned. If the detector learned its own baseline, a slow degradation would become the new normal, which is the exact failure it exists to catch (Resibo docstring).
- A clean finding is returned too (`degraded=false`), so an operator can tell "checked and fine" from "the detector stopped running".
- `Record` never throws and never blocks on more than its own lock. It is called from hot paths of other modules.
- Classification order: negligible wall time, then memory-bound by RSS delta, then CPU ratio against the two thresholds, with wall time separating IO from network. Thresholds are the settings in 6.6.

### 7.2 Changed for Bazaar

- **Samples are an in-memory ring per operation, findings are persisted.** Raw samples are high volume and carry no need for durability; rolled-up findings go to `diagnostic_finding` and to Kodo.
- **Measuring CPU time per operation in .NET is not free.** Resibo measures a Python function. In .NET an `async` operation hops threads, so process-wide CPU deltas over-attribute and `Thread` CPU time under-attributes. Bazaar therefore defines the sample source per caller: in-process callers measure with `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread` as a proxy for RSS delta, and mark the sample `Approximate`; out-of-process holders (the guest agent measuring an exec's process tree, a sidecar) report exact CPU and RSS. The classifier treats an approximate sample set as lower confidence in its finding detail. Whether the approximation is good enough is OPEN (question 7); the benchmark harness decides.
- **Baselines can come from two places and the two never mix.** `declared` baselines are Resibo's behaviour and the default. Kodo's learned per-install baselines and drift (doc 46 section 13.3) track numeric series (latency, queue depth). They answer a different question and this module does not duplicate them. A finding from this module is a categorical flag (class changed) and goes into Kodo as an observation; Kodo's predictor handles trends. There is no overlap to remove, but the doc states it so nobody adds a learned baseline here.
- **What gets recorded** (initial operation set, each registered by its owning module in its manifest): VM exec (`vms.exec`), VM start and save timings (`vms.start`), local inference tasks per `LocalTaskKind` (`local.chip_summary` and so on), gateway calls (`gateway.call`, expected network-bound), Constellation tile build (`constellation.tile_build`), Postgres hot queries on request. Each module declares its baseline in its manifest `diagnostics` block; the module with the work owns the number, matching Resibo's rule that the expensive part belongs to whichever API does the work, not to Health.
- **No content, ever.** A sample carries service, operation, three numbers, a timestamp and an optional `Principal` for the agent whose work it was (so a slow agent VM can be found). No arguments, output or paths.

```csharp
public enum WorkloadClass { CpuBound, IoBound, NetworkBound, MemoryBound, Negligible, Indeterminate }

public sealed record DiagnosticSample(string Service, string Operation, double WallMs, double CpuMs,
                                      double RssDeltaMb, bool Approximate, DateTimeOffset At, Principal? Subject);

public sealed record DiagnosticFinding(string Service, string Operation, WorkloadClass Baseline,
                                       WorkloadClass Observed, int SampleCount, bool Degraded, string Detail);

public interface IDiagnosticSink   { void Record(DiagnosticSample sample); }          // never throws
public interface IDiagnosticReader { DiagnosticFinding Finding(string service, string operation);
                                     IReadOnlyList<DiagnosticFinding> Findings(); }
public interface IDiagnosticBaselines { void Register(string service, string operation, WorkloadClass expected); }
```

## 8. Capability drift

Resibo's drift check is specific: after the interpreter is upgraded, is the process still on the fallback branch of a shim (the PyPI `frozendict` instead of the builtin; `py-spy` configured when Tachyon is available)? Its two rules are the part worth adopting: a clean result is reported and not omitted, and a check that could not run reports nothing rather than clean. Every future shim registers its own probe in the PR that introduces the shim (deep-dive section 4.1).

### 8.1 Two drift families, both in the ledger module

**Capability drift** (Resibo's idea, generalised). A registry of `ICapabilityCheck`s, each comparing an **expected path** with the **actual path** a running component takes:

```csharp
public enum CheckOutcome { Clean, Drifted, NotRun }       // NotRun is recorded, never counted clean

public sealed record CapabilityFinding(string CheckId, string Subject, string ExpectedPath, string ActualPath,
                                       CheckOutcome Outcome, string Detail);

public interface ICapabilityCheck
{
    string CheckId { get; }
    Task<CapabilityFinding?> RunAsync(CapabilityContext context, CancellationToken ct);   // null only when not applicable
}
```

Resibo returns nothing for an unrunnable probe. Bazaar changes that to an explicit `NotRun` finding that is stored and shown, because Kodo's rule is that `unknown` is never green and an operator should see which checks are not running. `null` is reserved for "this check does not apply here" (for example a Windows-only check on Linux).

Bazaar's initial checks (all PROPOSED), each an example of a graceful fallback that can stay degraded forever:

| Check id | Expected | Actual read from |
|---|---|---|
| `local.execution_provider` | the best provider the profile says is available (doc 31: NPU, then GPU, then CPU) | the running session's `SessionInfo` and its placement report (95 percent of nodes on the NPU, no attention or KV op on CPU) |
| `renderer.backend` | Vulkan where the profile shows a usable Vulkan device (`constellation-render-core.md`) | the renderer in use; Skia fallback reported when Vulkan was available |
| `runtime.dotnet_version` | the target runtime from `global.json` or newer (CLAUDE.md forward-facing rule) | the running runtime version; an install on an older runtime is drifted |
| `runtime.api_path` | no obsolete code path active | a module-declared shim flag, registered in the PR that adds a shim |
| `vm.virtualization` | hardware virtualization on when the VM backend is configured | the profile's `Virtualization` facet versus the backend's reported mode |
| `npu.driver` | driver not on the doc 31 refused list | the profile's NPU driver version |
| `gpu.driver_window` | driver within the tested window recorded in the tier table | the profile's driver version |

CLAUDE.md's forward-compatibility rule says never to add code paths for older runtimes; the `runtime.*` checks therefore detect an install that is running on an older runtime, not shims in code. A shim registration is accepted only where a platform genuinely forces a fallback (an NPU path, a Vulkan path, a driver band).

**Hardware drift** (new; Resibo has no equivalent because its hardware profile is written once). On each probe the new profile is diffed against the stored one by `DeviceKey`:

| Drift kind | Example | Default severity |
|---|---|---|
| `device.added` | a second GPU appears | info |
| `device.removed` | the GPU is gone after a driver update | warn, error if a pool depends on it |
| `attribute.changed` | VRAM 12 GB to 2 GB; RAM 32 GB to 16 GB (a DIMM failed) | error when the value shrank, info when it grew |
| `driver.changed` | NPU or GPU driver version moved | info; warn if it enters a refused list |
| `virtualization.changed` | hardware virtualization turned off in firmware | error when a VM backend is configured |
| `source.changed` | a profile derived differently (report import added or removed) | info |
| `probe.degraded` | a provider that used to return facts now returns none | warn |

A shrinking capacity triggers the **ledger rebase**: `resource_pool.capacity_units` is recomputed from the new profile. If held units now exceed capacity, nothing is revoked (the ledger never kills a holder); new grants are refused and a `pool.oversubscribed` finding is raised, and the VM lifecycle's own policy (not this module) decides whether to save a VM to get under the line.

### 8.2 Events

```csharp
public sealed record DriftEvent(Guid EventId, string DeviceId, DriftKind Kind, DriftSeverity Severity,
                                string Subject, string Detail, DateTimeOffset At, string Fingerprint);

public interface IDriftEventPublisher { Task PublishAsync(DriftEvent e, CancellationToken ct); }
```

`Fingerprint` is built with the existing `Bazaar.Diagnostics` fingerprinting (device class, drift kind and subject, never serials or hostnames) so the issue filer can dedupe. Sinks:

1. **Kodo**: each event updates probe observations (section 10). Kodo owns state, alerts and incidents; this module never changes a health state itself.
2. **Audit log**: one entry per event with the acting `Principal` (the scheduled job is `system:hardware-probe`, an owner re-detect is `user:<id>`).
3. **Issue filer** (sibling module, soft dependency): receives `DriftEvent`s at or above `hardware.drift.notify_severity` for filing under its own consent and dedupe rules. Hardware descriptions are device fingerprints, so the filer redacts them with doc 43's redactor first. This module does not file issues, so the issue filer's consent mode (off, ask, auto, doc 43) governs.
4. **Operator channels**: the Monitor pane through Kodo's stream, and the setup dashboard on the owner's client.

Resibo surfaces drift in a distinct high-visibility log tier (`ATTENTION`) and through notifications, not an ordinary warning (deep-dive section 4.3). In Bazaar that role is played by the Kodo alert plus a dedicated `drift` topic; no new logging level is added by this module (OPEN whether Annales-style tiers exist in Bazaar's logging, question 7).

## 9. Setup and onboarding

Doc 45 builds the setup plan from many entry paths. This doc adds:

- A setup step `hardware.profile` (idempotent, order after database setup) that runs the probe, stores profile version 1 and tier report, and writes a plan fragment `hardware.detected` (read-only: facts, not choices).
- A wizard screen showing the detected profile and the recommended tier per axis, with `reasoned` shown honestly ("estimate, not benchmarked"). Resibo's setup wizard shows three labels in Step 9; Bazaar shows axes with one sentence of effect each.
- The report import consent prompt (section 5.4) as an optional step.
- The headless plan can pin capacities (`ledger.static_capacity`) and tier overrides, so an operator on a VM or CI box with no usable probe still gets a working install. A plan that pins nothing on a machine where the probe finds no facts fails validation with exit code 2 ("held, not refused"), not a silent start.

## 10. Kodo health contract

Both modules implement `IModuleHealth` (doc 46 section 10) and contribute probes. New probes belong to the signed Kodo catalogue, so adding them is a Kodo update PR (work package 8), not a setting. All are non-critical except where noted.

| Probe id | Critical | Cadence | Pool | Method |
|---|---|---|---|---|
| `hardware.profile.fresh` | no | 1 h | local | profile age against `hardware.reprobe_hours` times 2 |
| `hardware.probe.providers` | no | 1 h | slow | every enabled provider returned facts or a stated `NotApplicable` |
| `hardware.drift.open` | no (error severity makes the state amber) | 5 min | local | count of unacknowledged drift events by severity |
| `hardware.virtualization` | yes when a VM backend is configured and agents are enabled | 1 h | slow | virtualization facet usable |
| `ledger.pool.<kind>.<device>` | no | 30 s | db | utilisation and waiters; red above 95 percent for longer than grace |
| `ledger.reconcile` | yes if unconfirmed leases outlive the grace | 30 s | db | leases in `unconfirmed` past `ledger.reconcile_grace_seconds` |
| `ledger.orphans` | no | 1 min | db | expired leases whose holder still appears alive |
| `ledger.sweeper` | no | 1 min | local | sweeper last run |
| `diag.degraded.<service>.<operation>` | no | 1 min | local | latest finding degraded |
| `capability.drift.<check>` | no | check interval | local | latest finding; `NotRun` counts amber, never green |

Module health contract:

- **Liveness**: no I/O; the sweeper and scheduler tasks are running.
- **Readiness**: `hardware-probe` is ready when a profile exists (or static capacity is pinned) and the store is reachable from Kodo's cached state. `resource-ledger` is ready when the database is reachable and the pool table has been reconciled with the profile at least once; it declares `hardware-probe` as a hard dependency.
- **Self-test, Quick**: parse fixture WMI JSON (single-GPU bare-object case) and a Linux `lspci` fixture; classify a fixed sample set; no I/O. **Full** (sandbox transaction, rolled back): create a pool, reserve, refresh, expire by injected clock, assert capacity returns, and assert `LeftNoResidue`.
- Dependencies declared: `hardware-probe` hard for the ledger; `kodo`, `audit`, the issue filer soft.

Doc 46 section 9 also lists `agents.vm.<vm>.*` probes. They stay VM-provider probes; the ledger adds the pool view alongside, not in place.

## 11. Security and privacy

- **A hardware profile is a device fingerprint.** It contains model strings, serial-derived keys, MAC-derived data and a driver inventory. It is stored under the install's data protection rules, shown to roles with the `hardware.view` permission (owner by default), and excluded from telemetry unless the owner enables hardware context; where included it passes through the doc 43 redactor, with serials, MAC addresses, hostnames and user names removed. Disk serials are stored as a keyed hash. Redaction is deterministic and never model-based (doc 43 and doc 46 decided input).
- **Probe commands run as the host service account** with fixed argument lists, timeouts, no shell composition and no user input in the command line. The `ReportImport` provider reads text only, caps file size, never executes, and never follows links outside its configured paths.
- **The ledger is not a security boundary against a hostile module** (an in-process module can exhaust the host without leasing). It is a cooperative control with a fail-closed default for unknown pools. Agent-driven actions go through `agent:<id>` principals so a runaway agent shows up by holder. A real isolation limit remains the VM resource caps in doc 16 and Kodo's bulkheads.
- **Holders cannot forge others.** `Refresh` and `Release` check `holder` against the caller's `Principal`; another principal cannot release a lease except through the owner-gated `ForceRelease` action (`ledger.force_release`, a high-impact action under doc 15, audited, step-up required).
- **Capacity is never raised by a holder.** Only the probe, operator pins that lower, or the owner-approved overcommit ceilings change capacity.
- **Untrusted input.** Probe output (a driver-supplied name string, a report file) is untrusted text. It is length-limited and escaped before display and is never fed to a model; no model sees profiles unless the owner enables model explanations in Kodo, which is separately labelled "unverified diagnosis" (doc 46 section 13.5).
- **No secrets.** Neither module reads or writes secrets.
- **Audit events** (kind, payload, `Actor` text is the canonical `Principal` form):

| Kind | Actor example | Payload fields |
|---|---|---|
| `hardware.profile.recorded` | `system:hardware-probe` | device id, version, source, tier summary |
| `hardware.drift.detected` | `system:hardware-probe` | kind, subject, severity, fingerprint |
| `hardware.redetect.requested` | `user:<id>` | device id |
| `hardware.tier.override_set` | `user:<id>` | axis, device id, from, to |
| `hardware.report_import.consent` | `user:<id>` | enabled, paths count (paths themselves are not logged) |
| `ledger.lease.granted` | holder `Principal` | pool, units, purpose, fence |
| `ledger.lease.rejected` | holder `Principal` | pool, units, reason |
| `ledger.lease.expired` | `system:resource-ledger` | lease id, holder, pool, units |
| `ledger.lease.force_released` | `user:<id>` | lease id, holder, reason |
| `ledger.pool.capacity_changed` | `system:resource-ledger` or `user:<id>` | pool, from, to, source |
| `ledger.orphan.suspected` | `system:resource-ledger` | lease id, holder |
| `capability.drift.detected` | `system:resource-ledger` | check id, expected, actual |

Grant and release events are high volume. They are written to the audit log only for non-routine cases (rejection, expiry, force release, capacity change); routine grant and release are counted in metrics and kept in `resource_lease` for the retention window (`ledger.lease_history_days`, default 30). That keeps the hash-chained audit log from filling with heartbeat noise, a decision to confirm (question 3).

## 12. Interaction with existing modules

### 12.1 VM sizing (doc 16, OPEN)

Doc 16 states defaults (Windows 2 vCPU, 4 GB; Linux 2 vCPU, 2 GB) and "a 32 GB NUC (unverified) fits about 4 running pairs". Neither the sizing nor the pair count is decided. This doc does not decide them. It supplies three mechanisms to `Bazaar.Server.Vms` and the setup wizard:

1. **Slot capacity derived from the profile.** `VmSlot` capacity is `floor((RamTotal - ram_headroom) / (windows_mb + linux_mb))` over running pairs, additionally bounded by `VCpu` capacity and the disk pool. For doc 16's numbers, 32 GB with 4 GB headroom gives `(32768 - 4096) / 6144 = 4`, matching doc 16's estimate, with the formula visible instead of folklore. The result is advisory input to the pool and is capped by a setting `vm.max_concurrent_pairs` (operator, tighten only; default unset means the derived value).
2. **Leases before start.** `VmLifecycleService.StartAsync` takes a `RamMb` lease (the VM's static memory) and a `VCpu` lease for the VM before calling `IVmBackend.StartAsync`, and a `VmSlot` lease for the first VM of a pair. If the ledger rejects, the transition does not start; the VM stays `Stopped` or `Saved` and the caller receives a typed "no capacity" result, which agent scheduling treats as "wait" (doc 06). Release happens in the same transition as `Stop`, `SaveState` or `Retire`. Leases carry the VM id in `purpose`, and the fence is stored in `environment_vm` (an additive column in `Bazaar.Server.Vms`'s namespace, a work package there).
3. **A sizing advisor**, `IVmSizingAdvisor` in `HardwareProbe.Abstractions`: `Advise(profile, desiredPairs) -> VmSpecAdvice` returning a recommended `VmSpec` per OS and a maximum pair count, with the reasoning and `Calibrated=false` until the S8 benchmark exists. The setting `vm.sizing.mode` is `fixed` (default; doc 16's numbers apply unchanged) or `advised` (apply the advisor's output). The owner can leave doc 16 untouched and still see what the advisor would say. When doc 16's benchmark runs, its report becomes a new `tier-table` calibration, not a new design.

**Doc 59 assumption.** Doc 59 (GUI VM density) is not in this checkout. This doc assumes only what doc 16 already says: one headed VM at most (`vm.headed_vm`), headless otherwise. If doc 59 sets different per-VM costs for a headed GUI VM (GPU memory or a larger RAM budget), those become a different `ResourceKind` demand in the lease request (a headed VM requests `VramMb` as well) with no change to the ledger. The drift check `vm.virtualization` and `hardware.virtualization` probe need no change. Update this section when doc 59 lands.

Existing Windows Sandbox provider (`Bazaar.Server.Vms.WindowsSandbox`) declares one instance per host. The ledger models that as a `VmSlot` capacity of 1 for that backend's pool, so the "agent pair never selects it" rule is also visible as pool arithmetic. It does not replace the fail-closed rule in `ProviderSelector`.

### 12.2 Constellation renderer (`constellation-render-core.md`, `constellation-scale-and-lod.md`)

Section 6 of the scale doc already says tiers come from measured device capability and the operator can tighten but not loosen above the floors. This doc provides the measurement and maps it:

| Renderer budget set | Selected when |
|---|---|
| PC tier (2M instances, 1.5 GB resident GPU) | device class Desktop or Server and `Graphics` tier at least `Standard`, GPU device-local heap at least the cap with the 10 percent headroom |
| Phone tier (300k instances, 256 MB resident GPU) | device class Phone or Tablet, or `Graphics` tier `Light` |
| Floor (Skia fallback or reduced caps) | `Graphics` tier `Floor`, no usable Vulkan device, or client has no cached report |

The renderer adds a **runtime adaptive frame budget**: it reports its GPU frame time p95 to the diagnostic sink (`constellation.frame`, expected `CpuBound` or `Negligible` on CPU side, with GPU time as the measured value); if p95 exceeds 14 ms (60 fps target) for the window it steps down one cap (instance count, then resident tiles) and records a `renderer.tier_stepdown` finding. Stepping up again requires a profile or manual action, not a timer, so the renderer does not oscillate. This reuses the scale doc's own measured numbers (for example 1.49 GiB resident at 100M nodes on the Arc B580) as tier-table entries. The Skia-fallback case is a capability drift finding (`renderer.backend`) when Vulkan was available.

`Bazaar.Constellation.Vulkan.VulkanProbe` already enumerates devices and reports mesh shader support. The module `hardware-probe-vulkan` wraps it as a facet provider; nothing is duplicated and `VulkanProbe` is unchanged. The Vulkan provider adds `Reported` GPU facets (name, device type, API version, mesh-shader flag); VRAM from Vulkan heaps needs a small extension of the probe (read device-local memory heaps), listed as a work package in the Constellation Vulkan project rather than a rewrite.

### 12.3 Local inference (doc 31, `Bazaar.Client.LocalInference`)

Doc 31 fixes the chain per OS (Windows: OpenVINO NPU, then OpenVINO GPU, then ORT CPU; Android: QNN HTP, QNN GPU, CPU) and rejects silent CPU fallback by placement verification. The probe adds facts, not a new chain:

- `IExecutionProviderSelector` (already in `LocalInferenceContracts.cs`) takes an `IHardwareProfileReader` instead of testing for devices itself. Doc 31's own test plan already uses "a fake hardware probe"; this is that probe.
- Per-device runtime choice: the selector filters the chain by facts: NPU present and driver not on the refused list, GPU present with usable VRAM for the model's footprint, battery saver (the existing `IPowerState`) and thermal state. It takes a `NpuSession` or `VramMb` lease on the client's pool (client-local ledger, below), loads the model, verifies placement, and on failure releases the lease and moves down the chain.
- Where the ledger lives for a client: the client has no Postgres. `Bazaar.Client.HardwareProbe` embeds a small in-memory pool set; leases are process-local and expire with the process, which is correct because a client process crash frees its own GPU memory. The client ledger is a separate implementation of the same `IResourceLedger` contract in `Bazaar.Client.HardwareProbe` (no durable store needed). The doc 31 rule "one session per model per device" becomes an `NpuSession` pool of capacity 1.
- Compiled NPU blob cache key (model hash, EP, driver version, runtime version) already includes driver version; the drift check `npu.driver` adds the missing detection of "driver changed under a cached blob" at the profile level, so the cache can be invalidated proactively instead of failing at first use.
- A degraded placement is a capability drift finding (`local.execution_provider`), not just a UI badge.
- The server never runs local inference; none of this moves compute to the server.

### 12.4 Kodo

- Kodo's `IHostReadings` provides disk, memory and clock to its own probes. The hardware probe does not replace those. `hardware.profile.*` is the install's identity; `host.disk.free` and `host.memory.pressure` are live. Both read the same OS facts but through separate interfaces, so Kodo stays free of this module. An adapter may later implement `IHostReadings` from the facet providers to avoid reading twice (work package 9, optional).
- Drift events enter Kodo as observations through the probe additions in section 10. Doc 46 requires a catalogue update for that; the work package names it.
- Kodo's score weights (host 10, models 5, ...) are unchanged. These probes land in the `Host` category with the default weights; whether hardware deserves its own category is a Kodo decision (question 2).

### 12.5 Other modules

- **Gateway (doc 05)**: no interaction; gateway calls are only a diagnostic source.
- **Setup (`Bazaar.Server.Setup`)**: adds the `hardware.profile` step via `ISetupStep`; no change to the existing steps.
- **Agents (doc 06)**: the agent scheduler may read `PoolState` of `VmSlot` to avoid dispatching work that cannot get a VM. It reads through `IResourceLedger`; it does not take leases itself.
- **Diagnostics (doc 43)**: reuse `Fingerprint`, `Redactor`, consent settings. Not duplicated.
- **Updates (doc 49)**: a rollout ring can use `hardware.drift.open` as a cohort signal (a driver update causing drift is exactly what the halt rule wants). Kodo owns the halt; this doc only provides the probe.

## 13. What is taken from Resibo, and what is changed

| Resibo item | Where | Taken | Changed for Bazaar and why |
|---|---|---|---|
| Static `HardwareProfile` and `GpuInfo`, platform-dispatch detection | `services/setup/hardware/detect.py`, deep-dive 11 section 5.1 | CPU/RAM/GPU/NPU facets, parse-separate-from-I/O, graceful degradation per tool | Per-OS selection replaced by facet provider modules (no selector, owner direction); stable device keys instead of names; added disks, virtualization, power, thermal |
| VRAM gotcha: registry beats `AdapterRAM` | `detect.py` `_resolve_vram_gb`, deep-dive 11 section 5.2 | the rule, as `ClaimConfidence` ordering | generalised to all attributes, conflicts recorded as notes |
| Discrete versus integrated classification, compute API mapping | `detect.py` `_classify_*` | Arc versus generic "Intel(R) Graphics" rule, vendor mapping | `compute_api` becomes a facet with Windows ML and Vulkan values; DirectML is not an NPU path (doc 31) |
| Single-object-or-list normalisation | `detect.py` `_normalize_to_list` | taken as a provider-internal test case | prefer managed CIM over PowerShell where available |
| External report import (CPU-Z, HWiNFO) | `report_import.py`, deep-dive 11 section 5.3 | best-effort parse, merge across files, provenance in `source` | separate provider module, off by default, one consent path, operator-listed paths only |
| Tier recommendation | `scoring.py`, deep-dive 11 section 5.4 | seam plus honest `reasoned` flag, calibrate from bench data | per-axis tiers and a data table instead of one three-label function; Bazaar-specific vocabulary |
| Live VRAM ledger with TTL | `core/health/resource_ledger.py`, deep-dive 20 section 5 | outcome-not-error rejection, fail closed on unknown device, TTL with heartbeat, no revival of a lapsed lease, idempotent release, sweep before decide, injected clock | durable in Postgres, fencing token, restart reconcile, `Principal` holders, more resource kinds, optional queue, headroom and overcommit; because Bazaar restarts and runs VMs that outlive a process |
| Live diagnostic | `core/health/live_diagnostic.py`, deep-dive 20 section 3 | classes, medians, declared baselines, indeterminate under 5 samples, never-throw `record`, clean findings reported | in-memory sample rings, persisted findings, approximate-sample flag for .NET async, no content in samples |
| Capability drift check | `core/health/capability_drift.py`, deep-dive 20 section 4 | registry of checks, a clean result is reported, "register your check in the PR that adds a shim" | explicit `NotRun`, Bazaar checks (EP, renderer, runtime, driver), plus hardware drift diff which Resibo lacks |
| Pre-stable bench validation | `docs/PRE_STABLE_BENCH_VALIDATION.md` | the discipline: provisional defaults are listed, labelled, and closed by measurement before a Stable tag; Part 2 items (TTL default, scoring coefficients) apply here | Bazaar's lanes are hardware-only; there are no receipt scans. The "never fabricate input" rule is restated as: never invent benchmark numbers; the owner's PC and an Android device supply them (section 15 package 10) |

Not taken: Resibo's per-service venv provisioning (Python-specific), the TUI wizard (Bazaar's setup is doc 45), and its in-memory-only ledger.

## 14. Testing (xUnit v3; Avalonia.Headless for UI)

- **Facet merge**: conflict resolution by confidence, no averaging, absent stays absent, the Resibo 12 GB card case (registry 12.78 GB beats AdapterRAM 2.0 GB), claim conflict note emitted.
- **Parsers**: synthetic WMI JSON including the single-GPU bare-object case and the zero-GPU case; `/proc/cpuinfo` with and without `physical id`; `lspci` fixture; the Arrow Lake "Intel(R) Graphics" integrated string and an "Intel Arc" discrete string; report import fixtures including a block where name and count appear in either order (Resibo found this was a real bug).
- **Providers**: each provider module runs against a fake command runner; real hardware lanes (Windows NUC, Arc B580 desktop, one Android device) are manual or nightly, recorded not gated, like doc 31's.
- **Tiering**: table validation, boundary values, `Floor` for missing facts, override audit, tier never above what the ledger can grant.
- **Ledger** (real Postgres lane plus an in-memory fake with the same contract tests): capacity arithmetic, rejection as a value, unknown pool rejection, idempotent reserve under a concurrent retry, concurrent reserves never oversubscribe (parallel stress with N holders), TTL expiry with an injected clock, refresh of a lapsed lease throws, release is idempotent, sweep-before-decide, restart reconcile (leases `Unconfirmed` hold capacity, confirm and expire paths), mass-expiry guard, fence monotonic, `LedgerUnavailable` is a rejection, holder mismatch on refresh refused, force release needs the permission.
- **Crash tests** (the doc 46 style): kill the process between lease insert and the VM start call; on restart the lease is reconciled against a fake backend and either confirmed or expired with the VM quarantined, never double-granted.
- **Diagnostic**: each class from synthetic samples, median robustness to one outlier, indeterminate under minimum samples, no degradation when either side is indeterminate, never-throws under concurrent `Record`.
- **Drift**: both directions matter (Resibo's own testing note): a clean environment reports clean, a seeded drift reports drifted; `NotRun` is never clean; hardware diff for each drift kind; ledger rebase when capacity shrinks leaves holders alone.
- **Kodo contract**: inherit `ModuleHealthContractTests<TModule>` from doc 46 section 10.3 for both modules.
- **Architecture tests**: neither module references `Bazaar.Server`, `Bazaar.Client.Core`, or another module's implementation; providers reference Abstractions only; no third-party packages in the shared Abstractions projects.
- **UI**: headless test of the setup wizard hardware screen and the admin hardware view: every drift kind rendered with word and colour, `NotRun` shown distinct from clean, keyboard-only walk.
- **Benchmark harness** (not CI): calibrates the tier table, measures reservation hot-path latency (Resibo calls its own TTL value unmeasured), probe cost, and the .NET diagnostic approximation error against exact process-tree numbers.

## 15. Work packages

Each is a small PR with its own tests and files; dependencies in parentheses.

1. `Bazaar.Contracts.HardwareProbe` wire records and `HardwareProbe.Abstractions` contracts, with serialization tests. (none)
2. `ResourceLedger.Abstractions` contracts and the shared contract-test base for any `IResourceLedger` implementation. (1 for `Principal`; Core already has `Principal`)
3. `hardware-probe` core: merge, tier table loader and evaluator, profile store, drift diff, `hardware-probe` migrations. (1)
4. Provider modules in parallel, one PR each: `hardware-probe-windows`, `hardware-probe-linux`, `hardware-probe-vulkan` (plus the VRAM-heap addition to `VulkanProbe`), `hardware-probe-report-import`. (1)
5. `resource-ledger` core: pools, leases, reconcile, sweeper, migrations, in-memory fake. (2, 3)
6. Live diagnostic and baseline registry in `resource-ledger`. (2)
7. Capability drift registry and the initial checks. (2, 3)
8. Kodo catalogue additions and the `IModuleHealth` implementations for both modules. (3, 5, and Kodo's catalogue work)
9. Setup step `hardware.profile` and the wizard screen. (3, doc 45 setup module)
10. VM integration: leases in `VmLifecycleService`, `environment_vm` fence column, `IVmSizingAdvisor`, `vm.sizing.mode`. (5, doc 16 lifecycle)
11. Client modules: `Bazaar.Client.HardwareProbe` (client ledger, report upload, cached tier) and the Android provider. (1, 2)
12. Renderer tier mapping and adaptive frame budget in Constellation (a Constellation PR). (11)
13. Local inference selector using the profile and client ledger. (11, doc 31 selector)
14. Drift event publisher wiring: audit, Kodo and the issue filer sink. (7, issue filer module)
15. Benchmark and tier calibration lane. (3, 5, 10); needs the owner's PC and an Android device.

## 16. Open questions

1. **Module names and ids.** `Bazaar.Server.HardwareProbe` and `Bazaar.Server.ResourceLedger` come from the owner's task; ids (`hardware-probe`, `resource-ledger`) and the provider project names are working names.
2. **Kodo category.** Do hardware and ledger probes join `Host` with its weights, or get a `Capacity` category? Kodo owns the answer.
3. **Audit volume for lease events.** Proposed: audit only non-routine lease events and keep routine ones in `resource_lease`. Owner to confirm, since the audit log is the hash chain.
4. **Queueing fairness.** `LeaseMode.Queue` is shipped off. Priority rules between agents, owner requests and background work are not decided.
5. **Windows provider technique.** Managed CIM versus a PowerShell subprocess for WMI and registry reads; doc 16 already picks a PowerShell runspace for Hyper-V, so reuse is possible. Needs a spike on the owner's PC.
6. **Shared probe code between client and server.** Whether the Windows and Linux client heads can reuse the provider facet code without a server reference, or the client gets its own small modules. This needs an architecture-test check.
7. **Diagnostic accuracy in .NET.** Whether the approximate in-process CPU and RSS samples are good enough to separate CPU-bound from IO-bound, and whether a distinct high-visibility log tier (Resibo's `ATTENTION`) exists in Bazaar. Benchmark decides the first.
8. **Doc 59 reconciliation.** Re-check section 12.1 when the GUI VM density doc lands.
