# Spike S-WSB: Windows Sandbox as a VM provider

Branch: `agent/wsb-spike`. Design source: `docs/design/50-windows-sandbox-provider.md` (section 6 lists the eight measurements). Run on the owner's PC (Windows 11 Pro, build 26300, Intel Core Ultra 9 285K, 96 GB RAM) on 2026-11-16, local time +08:00.

## Answer first: single instance

**Verified: a second Windows Sandbox cannot start while one is running.** With one instance running, `wsb start` for a second one fails with `Application cannot be run more than once (0x800401F6 CO_E_APPSINGLEUSE)`, exit `-2147221002`. This happened twice, with the minimal config and the full config. The first start of each pair succeeded.

Not measured: whether the limit is per user or per host. The provider treats it as host-wide (see Scope decision below). The refusal came from the `WindowsSandboxServer` COM server, which may be per user session. Until a second account is tested, treat the limit as host-wide, which is the safe reading. This supports the doc 50 decision that `MaxConcurrentInstances = 1`.

## Environment

| Fact | Status | Evidence |
|---|---|---|
| Windows 11 Pro (Professional), build 26300 | verified | `Win32_OperatingSystem`, `EditionID` = Professional |
| Hypervisor present (`HypervisorPresent = True`) | verified | `Win32_ComputerSystem` |
| CPU firmware virtualisation flag reads `False` | verified as reported; not interpreted | `VirtualizationFirmwareEnabled = False` while the hypervisor is running. Windows often reports this as False when the hypervisor already owns VT-x. Sandbox runs, so virtualisation works. |
| `Containers-DisposableClientVM` optional feature enabled | verified | Elevated `Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM` returned `State : Enabled` on the owner's PC (read-only, 2026-11-16, owner-approved sudo). `WindowsSandbox.exe` is present. The non-elevated facts probe reports the feature from the executable's presence, and this read agrees with it. |
| `WindowsSandbox.exe` present in System32 | verified | `Test-Path` |
| `wsb.exe` present (app execution alias in `WindowsApps`) | verified | `Get-Command` shows `C:\Users\theof\AppData\Local\Microsoft\WindowsApps\wsb.exe` |
| Sandbox package version | verified | `wsb --version` = `0.8.114.0`; process path `MicrosoftWindows.WindowsSandbox_0.8.114.0_x64__cw5n1h2txyewy` |

## wsb.exe commands (verified from `--help`)

`StartSandbox/start` (`--id`, `-c/--config` takes XML text), `ListRunningSandboxes/list`, `Execute/exec` (`--id`, `-c`, `-d`, `-r ExistingLogin|System`), `ShareFolder/share` (`--id`, `-f`, `-s`, `-w`), `StopSandbox/stop` (`--id`), `ConnectToSandbox/connect`, `GetIpAddress/ip`. All accept `--raw` (JSON output).

## Facts on identity, stop and non-interactive control

- **Identity.** `wsb start` returns a GUID in JSON `{"Id": "..."}`. `wsb list --raw` returns `{"WindowsSandboxEnvironments":[{"Id":...}]}`. **Verified.**
- **Stop.** `wsb stop --id <guid>` exits 0 and removes the id from `list`. Stop took about 0.5 to 0.8 s. **Verified.** The CLI has no separate forced stop. `stop` is the only termination path, so the adapter cannot tell graceful from forced. **Verified from help text.**
- **Non-interactive.** Start, list, exec and stop all worked from a PowerShell process started by a tool, with no console prompt. This is an interactive user session, not a service account. A service account run is **not verified**.
- **Leftover process.** After the stop of the last instance, `WindowsSandboxServer.exe` (pid 61140) remained running. The `vmmemCmZygote` process also stays. **Verified.** Whether the server process ever exits is not known.

## Config schema (`.wsb` as XML text)

| Element | Result | Status |
|---|---|---|
| `MemoryInMB` | Guest reports `Total Physical Memory: 4,095 MB` for `MemoryInMB=4096`. Takes effect. | verified by effect |
| `Networking` = `Disable` | `wsb ip` returns `The network was not found (0x803B0001)`. No network. | verified by effect |
| `Networking` = `Bogus` | Rejected: `There is an error in XML document (1, 48)`. The enum is validated. | verified |
| `MappedFolders/MappedFolder` (`HostFolder`, `SandboxFolder`, `ReadOnly=false`) | Guest writes to `C:\Share` appear in the host folder. Read-write works. | verified by effect |
| `VGpu`, `ClipboardRedirection`, `AudioInput`, `VideoInput`, `PrinterRedirection` | Accepted, no effect was measured. | **not verified** |
| Unknown element `<Bogus>` | Accepted silently. The schema is not strict. Acceptance of an element name does not prove the name is right. | verified |
| `MemoryInMB` = `abc` | Rejected: `The configuration file was invalid.` | verified |
| `LogonCommand` | Accepted in the test config. Did **not** run: no `logon.txt` on the host after about 2 minutes, and a guest `exec -r ExistingLogin` fails with `A specified logon session does not exist (0x80070520)`. The guest has no interactive logon in this setup. | **not verified**, observed failure |

**Input format.** `wsb start -c` takes the XML text itself. A file path fails with `The configuration file was invalid.` The current `WindowsSandboxConfig.Build` returns XML text, so it matches. The adapter must read the file and pass its contents.

**Gap in the repo config.** `WindowsSandboxConfig.Build` emits `MappedFolders` empty and no `LogonCommand`. That matches the doc 50 plan (scoped mounts come later). Note it, but it is not a bug.

## Boot-to-ready

- `wsb start` returned in about 0.8 to 0.9 s.
- The first successful `wsb exec -r System` came 1.1 s after start (one attempt).
- The guest's `Win32_OperatingSystem.LastBootUpTime` matches the start moment to within a few seconds.

So a warm start from the zygote is fast, about 1 s to exec-ready. **Verified, one cold-looking start, one measurement.** Doc 16's 120 s budget is far above this. Caveat: this is a single run, and the zygote may already hold a pre-booted image. The "cold" run is not truly cold, because the host had run sandboxes earlier the same day.

## Not measured in this run

- **Coexistence with a Hyper-V pair** (step 7). Not run. No Hyper-V pair exists on this PC to test against.
- **Headless** (step 8). No top-level window appeared for `WindowsSandboxServer` (`MainWindowHandle = 0`), and no client window was opened. A headless mode does not exist in the CLI help. Whether the client window opens on a server desktop is **not verified**.
- **Per-user limit** (see the answer above). Needs a second account.
- **Service-account stop** (step 3). Needs a service run.
- **Logon command** (step 6). Failed to run, see the table.

## Incidents during the spike

- A probe of `WindowsSandbox.exe /?` **started a sandbox**. The switch is not help. The instance (`a885a9b6-46e4-4f2b-8a5b-b682acbb0253`) was stopped with `wsb stop` after checking its start time (19:33:42, which matched the probe). Do not run `WindowsSandbox.exe` with arguments to get help.
- A negative-schema probe (`<Bogus>` element) also started a sandbox. It was stopped with `wsb stop` (`254f2707-...`).

## Implications for doc 50

1. **Single instance is confirmed on this host.** The `MaxConcurrentInstances = 1` capability and the refusal in `Probe` are correct.
2. The adapter can use `wsb.exe` for start, list, stop and exec. `wsb list` gives the running count. The `IWindowsSandboxHost` contract fits, with one gap: the CLI has no forced stop.
3. Boot-to-ready is about 1 s, far below 120 s.
4. Guest delivery by a logon command is not confirmed. Use `wsb exec` (the guest needs `-r System`) or a mapped folder as the delivery route, once the guest-agent design is settled (doc 50 section 7 question 3).
5. Config element names are confirmed by effect only for `MemoryInMB`, `Networking` and `MappedFolders`.

## Adapter check (step 2)

The real `IWindowsSandboxHost` adapter is in `src/Bazaar.Server.Vms.WindowsSandbox/WsbCli/` (`WsbCliSandboxHost`, `ProcessWsbCommandRunner`). `VmsCompositionExtensions` registers it when the `Bazaar:Vms:WindowsSandbox` section names `HostBaseImage` and `ConfigDirectory`, and `WindowsProviders.VmCandidates` lists it with an empty base image; whether `auto` selects it is not checked here. The opt-in live test `Live_round_trip_runs_only_when_explicitly_enabled` ran on this PC with `BAZAAR_WSB_LIVE=1`: it wrote a config, started a sandbox, saw it in `list`, stopped it, and deleted the config. The full suite passed 38 of 38 with it, and no sandbox was left running.

## Scope decision and remaining items

- **Limit scope (decided, host-wide).** Whether the one-instance limit is per user or per host is not measured. The provider treats it as host-wide: `MaxConcurrentInstances = 1` per host, enforced in the backend and in the selector. This is the fail-closed reading, and it holds whichever way the limit really works. A per-user measurement could only loosen it later, through a documented change to the declared capacity. No second-account test is needed to ship the provider.
- **Service-account stop.** Not needed for this provider. The VM role runs from the interactive server user, and no service-hosted sandbox path exists. Revisit it only if a service host starts sandboxes.
- **Still open:** logon command (`LogonCommand` did not run), headless window, and Hyper-V coexistence. None of them affects the capacity rule, and none is needed while the sandbox is the only VM candidate.
