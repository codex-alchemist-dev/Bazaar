# Windows Sandbox VM provider and host-compatibility metadata

Status: PROPOSED (owner request, 2026-11-16, via the Windows Sandbox thread). Facts about Windows Sandbox are unverified until spike S-WSB runs on the owner's PC (section 8).
Depends on: `16-agent-vms-and-environments.md` (VM contract, lifecycle, topology), `03-server-host-and-providers.md` (provider roles and selection), `41-client-gui-customization-and-dsl.md` (module manifest, section 8) · Used by: `42-deps-as-modules.md` (install checks), `49-gap-decisions-operations.md`

## Purpose
Adds Windows Sandbox as an agent-workspace provider behind the existing VM backend interface, and adds typed host-compatibility metadata to modules. Hyper-V and Windows Sandbox exist only on a Windows host with the feature enabled, and KVM only on Linux, so a module must declare which hosts it can run on.

## 1. Fit with `IVmBackend` (doc 16)
Windows Sandbox is a disposable Hyper-V guest that boots from the host's own Windows image. It has no parent disk, no differencing disk, no saved state and no checkpoints. The provider therefore maps the contract as follows:

| Contract call | Windows Sandbox behaviour |
|---|---|
| `CreateChildAsync(parent, spec)` | Accepts only the configured host base image id (`WindowsSandboxOptions.HostBaseImage`). Refuses Linux specs. Writes a `.wsb` config (memory, GPU, clipboard, audio, video input and printers off, networking off by default). Launches nothing. |
| `StartAsync` | Refused if this server already runs one, or if the host reports any running sandbox (single-instance limit, section 3). |
| `StopAsync(Graceful \| Force)` | Maps to a shutdown with or without a guest shutdown. Writable state is discarded either way. |
| `SaveStateAsync`, `CheckpointAsync`, `RestoreAsync` | Throw `NotSupportedException`. The lifecycle service records nothing for a refused call (tested). |
| `RetireAsync` | Force stop if running, delete the config, record `Retired`. |
| `GetStateAsync` | Reads the host. A sandbox closed by a user is recorded as `Stopped` and frees the slot. |

Every restart is a clean boot. "Restore" means boot again, not resume, so doc 16's idle save and checkpoint rules do not apply to this provider.

## 2. Capability report
`WindowsSandboxModule.Capabilities` is the report the status page and the lifecycle service read. Every value is backed by the backend's behaviour and tested.

| Capability | Value | Why |
|---|---|---|
| Save | no | Ephemeral; no saved state. |
| Checkpoint, Restore | no | No checkpoints. |
| Headless | no | The sandbox window always opens. Doc 16 wants headless VMs by default. |
| Max concurrent instances | 1 | Host-wide limit to verify (section 8). Doc 16 needs about 4 pairs, so this provider cannot host the topology alone. |
| Disk cap enforced | no | No child disk. `vm.windows.disk_cap_gb` cannot be honoured (owner question 2). |
| Parent content pinned | no | The parent is the host image; the provider pins an id, not a hash. |
| Linux guests | no | Windows guests only. |
| Ephemeral | yes | Writable state is discarded on close. |
| Memory configurable | yes | `MemoryInMB` in the config. |

## 3. Limits and what they mean
- **Single instance.** If one running sandbox per host is confirmed, Windows Sandbox cannot give each agent a Windows VM, and doc 16's pair topology (C03, DECIDED) fails on this host. The provider must not be selected for the VM role then. It can still serve a single-agent or a one-off workload if the owner accepts that.
- **No egress control yet.** Networking is off by default. A later spike must prove a proxy-only path before networking is ever on (doc 16, network and egress).
- **Guest agent delivery.** The Bazaar guest agent is baked into golden images (doc 16). A Windows Sandbox instance has no baked image, so the agent would arrive through a mapped folder and logon command at each boot. Its per-VM key and `Hello` pinning must then be re-designed. This is open.
- **Mounts.** Mapped folders are empty. Scoped mounts for the Soul folder and workspace (doc 16 WP8) need the same guest-delivery design.
- **Edition and feature.** Windows Pro, Enterprise or Education, with virtualisation on and the optional feature enabled (name to verify, section 8).

## 4. Module shape (platform-neutral)
- `Bazaar.Server.Vms.WindowsSandbox`: `WindowsSandboxBackend` (implements `IVmBackend` and `IVmProvider`, kind `windows_sandbox`), `WindowsSandboxConfig` (pure XML), `WindowsSandboxOptions`, `WindowsSandboxModule` (identity, requirements, capability report).
- `IWindowsSandboxHost` is the only seam to the host. The real adapter (`WsbCli/WsbCliSandboxHost`, driving `wsb.exe`) exists since spike S-WSB, which verified start, list, stop, exec and the single-instance refusal on the owner's PC (`docs/spikes/S-WSB.md`). The provider is still not registered in any host composition, so `auto` never selects it, until the owner answers section 7.
- `IHostFactsSource` reports OS, architecture and features for the requirements check. Its Windows implementation belongs in `Bazaar.Server.Providers.Windows` (Windows-only APIs stay behind a provider).
- Tests (`Bazaar.Server.Vms.WindowsSandbox.Tests`, 22) use in-memory fakes. Cloud CI makes no Windows Sandbox calls.

## 5. Host-compatibility metadata (owner, 2026-11-16)
Modules declare the hosts they can run on. The declaration is typed and versioned.

- `Bazaar.Contracts/Modules/HostRequirements.cs`: `HostRequirements(SchemaVersion, OperatingSystems, Architectures, RequiredFeatures)`. Schema version 1. Known values are fixed lists: operating systems `windows`, `linux`, `android`; architectures `x64`, `arm64`; features `hyperv`, `windows-sandbox`, `kvm`. OS and architecture lists must be non-empty, because an empty list would match nothing.
- `HostCompatibility.ValidateRequirements` checks the declaration (schema, vocabulary, duplicates). `HostCompatibility.Check(requirements, facts)` returns the reasons a host cannot run the module: `os-unsupported`, `arch-unsupported`, `feature-missing`. Both are pure.
- `ModuleManifest` gains a required `Host` field. `ModuleManifestValidator` reports declaration problems under `/host/…`. The client module test factory declares Windows and Linux, x64.
- **Provider selection:** the Windows Sandbox probe runs `Check` against `WindowsSandboxModule.Requirements` and reports the reasons in `ProviderStatus.Reason`. `auto` skips an unusable provider, and an explicit kind fails with that reason (existing `ProviderSelector` behaviour).
- **Install check (follow-up):** the module install path should run the same `Check` before it accepts a package. No server-side module manifest type exists yet (`ModulePackage` and `DependencyManifest` carry no host field), so this needs its own small change after this one.

## 6. Spike S-WSB: what the owner's PC must measure
Run on the owner's Windows PC, on a branch of its own, with the results written to `docs/spikes/S-WSB.md` (not yet written). Fix the pass criteria before the first run.

1. **Feature and edition.** The optional feature name, the edition check, and whether enabling it needs a reboot.
2. **Single instance.** Start two sandboxes. Record whether the second is refused, queued or allowed, and whether the limit is per user or per host.
3. **Launch and stop.** Launch from a `.wsb` file. Record how a specific instance is identified and stopped, and whether a forced stop is possible from a non-interactive service account. Check whether `wsb.exe` exists on this build and what it can do.
4. **Config schema.** Confirm the element names in `WindowsSandboxConfig`, the `Networking` value `Disable`, and that `MemoryInMB` takes effect (read the guest's total memory).
5. **Boot-to-ready.** Time from launch to a guest signal, cold and warm. Compare with doc 16's 120 s.
6. **Guest delivery.** Test a mapped folder and a logon command: can a guest agent start, and can it reach the host (networking is off)?
7. **Coexistence.** Run one sandbox next to a Hyper-V pair. Record host free RAM and any refusal.
8. **Headless.** Confirm that no headless mode exists and what the window does on a server desktop.

## 7. Owner questions
1. Accept that Windows Sandbox serves at most one running instance per host, so it cannot carry the agent pair topology? *Recommend* yes, as a provider for single-agent or one-off work, with Hyper-V remaining the VM provider for the pair.
2. `vm.windows.disk_cap_gb` cannot be enforced with this provider. Accept that, or drop the setting for it? *Recommend* accept, and show "not enforced" on the status page.
3. How does the guest agent reach a Windows Sandbox instance, given there is no baked image (mapped folder and logon command, or another route)? Needed before scoped mounts or exec work. *Recommend* deciding after S-WSB step 6.

## 8. Status and open items
- The module and its tests exist. The real host adapter exists (S-WSB, `docs/spikes/S-WSB.md`).
- The non-elevated facts probe (`WindowsSandboxHostFacts`) reports the `windows-sandbox` feature when `WindowsSandbox.exe` exists. The elevated check on the owner's PC confirmed `Containers-DisposableClientVM` is `Enabled` (S-WSB).
- The sandbox is registered in the Vm role with capacity 1 and Windows-only scope, with Hyper-V reserved first (PR #612). Auto never selects it for the agent pair.
- Still open: section 7 owner answers beyond the registration, and the per-user limit test (second account).
- The install-time host check is a follow-up (section 5).
- DECISIONS-LOG has no entry for this yet. The main session writes one after the owner answers section 7.
