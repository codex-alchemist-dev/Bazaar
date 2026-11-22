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
- **Guest agent delivery.** The Bazaar guest agent is baked into golden images (doc 16). A Windows Sandbox instance has no baked image, so the agent arrives at each boot. Spike S-WSB found that a logon command does not run in this setup, and that `wsb exec -r System` works about a second after start (section 9). Delivery is therefore a read-only mapped folder that carries the signed agent build, started by the host with `wsb exec -r System`. No logon command is used.
- **Mounts.** Mapped folders are empty. Scoped mounts for the Soul folder and workspace (doc 16 WP8) need the same guest-delivery design.
- **Edition and feature.** Windows Pro, Enterprise or Education, with virtualisation on and the optional feature enabled (name to verify, section 8).

## 4. Module shape (platform-neutral)
- `Bazaar.Server.Vms.WindowsSandbox`: `WindowsSandboxBackend` (implements `IVmBackend` and `IVmProvider`, kind `windows_sandbox`; also its own `IVmBackendSource`, a last-resort pair candidate, doc 62 section 9.2), `WindowsSandboxConfig` (pure XML), `WindowsSandboxOptions`, `WindowsSandboxModule` (identity, requirements, capability report).
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
9. **Guest channel (6b).** With the agent started by `wsb exec -r System` from a mapped folder, test the channels in section 9. Result so far (owner's PC, Windows 11 Pro 10.0.26300): stdio over the exec call is ruled out (`wsb exec --raw` returns only `{"ExitCode":N}`; there is no stdin option; the call blocks until the process exits). The Hyper-V socket is untested because registering a service GUID under `GuestCommunicationServices` needs an elevated shell. The Hyper-V socket was then tested with a temporary service GUID: a guest-to-host connection works with networking off, but only when the host listener is bound to the sandbox's own VM id (the HCS id, new on every start, read from an elevated `hcsdiag list`). A listener bound to the wildcard VM id was refused (WSAECONNREFUSED). The guest connects to `HV_GUID_PARENT` with the same service id. Registering the service GUID under `GuestCommunicationServices` needs HKLM write once, with no reboot; the listener itself needs no elevation. A bare exec round trip is 0.4 s and a PowerShell start about 1.5 s; the full exec, connect, send and ACK took 1.7 s. Remaining: the exact exec-to-`Hello` latency of the real agent.

## 7. Owner questions
1. Accept that Windows Sandbox serves at most one running instance per host, so it cannot carry the agent pair topology? *Recommend* yes, as a provider for single-agent or one-off work, with Hyper-V remaining the VM provider for the pair.
2. `vm.windows.disk_cap_gb` cannot be enforced with this provider. Accept that, or drop the setting for it? *Recommend* accept, and show "not enforced" on the status page.
3. How does the guest agent reach a Windows Sandbox instance, given there is no baked image? *Settled (section 9, PROPOSED):* mapped folder plus `wsb exec -r System`, with the channel chosen by the spike. Not an owner question any more.

## 8. Status and open items
- The module and its tests exist. The real host adapter exists (S-WSB, `docs/spikes/S-WSB.md`).
- The non-elevated facts probe (`WindowsSandboxHostFacts`) reports the `windows-sandbox` feature when `WindowsSandbox.exe` exists. The elevated check on the owner's PC confirmed `Containers-DisposableClientVM` is `Enabled` (S-WSB).
- The sandbox is registered in the Vm role with capacity 1 and Windows-only scope, with Hyper-V reserved first (PR #612). Auto never selects it for the agent pair.
- Still open: section 7 owner answers beyond the registration, and the per-user limit test (second account). Question 3 is settled in section 9 (PROPOSED), pending spike step 9.
- The install-time host check is a follow-up (section 5).
- DECISIONS-LOG has no entry for this yet. The main session writes one after the owner answers section 7.

## 9. Guest agent delivery and channel (PROPOSED, docs first)
Decided from doc 16 and the S-WSB results, so the owner does not have to answer section 7 question 3.

- **Delivery.** The host maps one read-only folder into the guest. It holds the signed guest agent build for this host's agent version (the same self-contained .NET 11 program that doc 16 bakes into images) and nothing else. The mapping is declared in the `.wsb` config at create time, so a sandbox without it has no agent and stays unusable for exec.
- **Start.** After the guest answers, the host runs the agent with `wsb exec -r System` (arguments as a list, never a shell string). The call blocks until the agent exits, so the host starts it without awaiting it and treats its exit code as the agent's end. Measured: exec works about 1 s after start; `-r ExistingLogin` fails (no logon session). The call is the only host-to-guest control path before `Hello`. A logon command is not used (S-WSB: it did not run).
- **Hyper-V socket details (spike step 9).** Address family AF_HYPERV (34), stream socket. The endpoint is a 36-byte sockaddr: family (2 bytes), reserved (2), VM id (16, `Guid.ToByteArray` order), service id (16). The service id is `<port as 8 hex digits>-FACB-11E6-BD58-64006A7986D3`. The host binds to the sandbox's VM id, so after `wsb start` it looks the id up (elevated `hcsdiag list`, the entry owned by `WindowsSandbox`) before it starts the agent; the lookup sits behind a seam, so a host without it fails closed. The service GUID is registered once at provisioning (admin), never at runtime.
- **Channel.** The agent speaks doc 16's guest protocol over an `IGuestTransport`. Spike step 9 ruled out stdin and stdout of the exec call: `wsb exec` has no stdin option, returns no output (only the exit code) and blocks until the process exits. The channel is therefore the Hyper-V socket, the doc 16 transport for Windows guests on a Windows host. The guest agent connects out to the host through a registered service GUID; the host listens. If the socket cannot be bound or does not connect, or the VM id lookup is unavailable, the provider reports `exec` as unsupported and stays fail closed: it stays usable for create and stop only, and the status page says so. No writable mapped folder is used as a message channel.
- **Identity.** There is no baked per-VM key. The host generates a fresh nonce and the VM id at start and passes them on the exec command line; `Hello{agentVersion, vmId, nonce}` must echo them or the host drops the channel. A guest that cannot echo them never receives a mount or a secret. Keys, Passport and provider credentials are never mapped (doc 16).
- **Mounts.** Scoped mounts for the Soul folder and the workspace use the same mapped-folder mechanism, declared in the `.wsb` config, each checked against the VM's mount list (doc 16). Write access is explicit per folder. The agent folder is always read-only.
- **Fail closed.** Missing agent folder, version mismatch (the agent reports a version other than the host's), a missed `Hello` nonce echo, or a channel timeout each leave the VM in `Provisioning` and stop it. Nothing is retried silently.
- **Elevation (spike step 9).** `wsb start` fails from an elevated process on the owner's PC (`Cancelled` after 30 s), and `hcsdiag list` fails from a non-elevated one (needs an administrator or Hyper-V Administrators membership). The server therefore stays non-elevated and runs only the VM id lookup through an elevation prefix (`ProcessHcsDiagRunner("sudo.exe", ["hcsdiag.exe"])`), or the operator adds the service account to Hyper-V Administrators. Without either, the lookup fails and the provider stays fail closed.
- **Connector.** `WindowsSandboxGuestConnector` ties it together: it reads the sandbox VM id (`hcsdiag`), creates an `HvSocketListener` bound to that id (doc 16 WP7 listener, option `BindVmId`), starts the agent with `wsb exec` (unawaited, `--transport hvsock --hvsock-service-id <guid> --vm-id <id> --nonce <n> --root <path>`), and opens the `GuestChannel` once the Hello matches. The agent exiting before the Hello, a timeout, a missing VM id or a bad Hello each throw, and the caller stops the VM.
- **Code that follows (separate PRs).** `IWindowsSandboxHost` gains an exec call (list-form arguments, system context), implemented in `WsbCliSandboxHost`. The `.wsb` builder gains the read-only agent mapping. The channel itself lands after the spike picks it.
