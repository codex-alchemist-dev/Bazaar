# Hyper-V Per-Agent Virtual Workstations: Topology and .NET Automation (as of Oct 2026)

Scope: Windows-only server host, .NET everywhere, headless agent VMs. Compares the "one Windows Server Core + WSL2 VM per agent" design (Bazaar.Soul.md, Section 2, DECIDED) against the unratified "two VMs per agent (Windows + Linux) with host-mediated SMB" proposal. Covers automation, guest communication, storage, differencing disks, alternatives, and Roblox Studio.

Verification note: the search quota was exhausted after five queries, so most claims below come from fetching Microsoft Learn and GitHub primary pages directly. Four pages failed to fetch (differencing-disk doc, checkpoint doc, Roblox requirements page returned 404/403). Anything not traceable to a fetched source is listed under Gaps and is not treated as established. Where a claim is my own reasoning, it is labelled as an inference.

## Key Question 1: Is WSL2 inside a Hyper-V VM supported, and does WSL2 run on Server Core?

### Takeaway
Both are documented as supported: WSL2 can run inside a nested Hyper-V VM, and on Windows Server 2025 (including Server Core) `wsl.exe --install` is the documented install path. The performance and stability cost of the nested stack is not documented by Microsoft and has not been measured here.

### Cited Findings
- Microsoft lists "Running WSL2 in a Hyper-V VM running nested on Hyper-V" as a supported scenario. — [Microsoft Learn: Nested virtualization](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/nested-virtualization)
- Enable nested virtualization per VM with `Set-VMProcessor -VMName <VMName> -ExposeVirtualizationExtensions $true`. — [Microsoft Learn: WSL FAQ, "Can I run WSL 2 in a virtual machine?"](https://learn.microsoft.com/en-us/windows/wsl/faq)
- "Windows Server 2022 and 2025 support a simple WSL installation using `wsl.exe --install`" and the page says this works on "Server Core on 2025." Earlier Server Core (2019+) uses the manual `Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux, VirtualMachinePlatform` path. Page last updated 2026-07-30. — [Microsoft Learn: Install WSL on Windows Server](https://learn.microsoft.com/en-us/windows/wsl/install-on-server)
- WSL 2 "uses a *subset* of Hyper-V architecture"; WSL's utility VM is lightweight and "will automatically be shut down" when no open file handles to Windows processes remain. — [Microsoft Learn: WSL FAQ](https://learn.microsoft.com/en-us/windows/wsl/faq)
- Idle-shutdown knobs: `.wslconfig` `[general] instanceIdleTimeout` defaults to 15000 ms (set -1 to disable auto-shutdown); `[wsl2] vmIdleTimeout` defaults to 60000 ms. — [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- WSL VM memory defaults to 50% of host memory and processors to all logical processors unless set in `.wslconfig` `[wsl2]` (`memory`, `processors`). `swap` defaults to 25% of memory. — [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- `[experimental] autoMemoryReclaim` defaults to `dropCache`. `nestedVirtualization` in `.wslconfig` is marked as Windows 11 only. — [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- Nested-virt caveats from the Microsoft page: nested Hyper-V VMs cannot change memory while running (dynamic memory "doesn't fluctuate"); "Nested Virtualization isn't suitable for Windows Server Failover Clustering, and performance sensitive applications"; non-Microsoft hypervisors inside a Hyper-V VM "aren't supported and are likely to fail." — [Microsoft Learn: Nested virtualization](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/nested-virtualization)
- WSL's own FAQ says it "requires fewer resources (CPU, memory, and storage) than a full virtual machine," and lists production differences: auto-shutdown, Windows executable interop from Linux, automatic Windows drive access. — [Microsoft Learn: WSL FAQ](https://learn.microsoft.com/en-us/windows/wsl/faq)
- Secondary source, conflicting: a Ranorex support article and search snippets claim nested virtualization is Intel-only. The Microsoft page fetched above does not state a CPU-vendor restriction. Treat the Intel-only claim as unconfirmed. — [Ranorex support: Enable Nested Virtualization](https://support.ranorex.com/hc/en-us/articles/43262514405009-Enable-Nested-Virtualization-for-Subscription-License-Manager)
- Hyper-V requirements (host): SLAT is required for the hypervisor; VT-x/AMD-V and DEP must be enabled in firmware; "Plan for at least 4 GB of RAM. More memory is better." — [Microsoft Learn: Host hardware requirements](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/host-hardware-requirements)

### Inferences
- The Soul design's core premise (Server Core + WSL2) is a documented Microsoft configuration, not an unsupported hack. The WSL-on-Server-Core install path is clean as of late 2026.
- Two WSL-specific operational risks follow from the docs. First, the idle-shutdown behaviour means a background agent service inside WSL will not stay up unless idle shutdown is disabled (`instanceIdleTimeout = -1`) or a process holds a handle. Second, `.wslconfig` memory must be set explicitly per agent VM, because the 50%-of-host default is wrong for a multi-VM host.
- Because the Hyper-V VM's memory is effectively fixed while nested Hyper-V runs, the per-agent memory budget must be set statically. Dynamic memory cannot reclaim RAM from a nested-WSL agent VM.

### Gaps
- No Microsoft-published numbers on nested-virtualization CPU, I/O, or network overhead for WSL2. Performance and stability cannot be stated from primary sources; a benchmark is needed.
- No authoritative statement on whether `wsl.exe` launched from PowerShell Direct (non-interactive session) works reliably for long-running distros on Server Core. Not verified.
- Whether nested-VM support differs between Intel and AMD hosts is unresolved (conflicting secondary sources).

## Key Question 2: Two VMs per agent vs one: resource cost, boot, checkpoints, concurrency

### Takeaway
No primary source here gives RAM or boot-time numbers, so a resource comparison cannot be sourced. The verified facts are: nested Hyper-V memory is static, Windows Server Datacenter grants unlimited VM rights per licensed host, and the Hyper-V checkpoint documentation could not be fetched.

### Cited Findings
- Hyper-V on Windows Server Datacenter "provid[es] unlimited virtual machine rights" with Hyper-V included in Windows Server. — [Microsoft Learn: Hyper-V overview](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/overview)
- Nested VMs cannot adjust memory while running; the parent VM must be turned off. — [Microsoft Learn: Nested virtualization](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/nested-virtualization)
- WSL `.wslconfig` caps total WSL VM RAM and CPU; `wsl --shutdown` is documented as the fast restart path, and config changes take about 8 seconds to apply after a full stop. — [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- Hyper-V overview says checkpoints let you "snapshot virtual machines before making changes." Details of standard vs production checkpoints were not retrievable (the checkpoint page returned 404). — [Microsoft Learn: Hyper-V overview](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/overview)

### Inferences
- One VM per agent (Windows Server Core with WSL2 inside) means one boot, one memory budget, one set of checkpoints, and no cross-VM file transport. It also means a nested stack whose cost is unmeasured.
- Two VMs per agent (Windows and Linux) avoids nesting and uses the mainstream supported topology. The cost is roughly double the guest OS footprint per agent, two boot sequences, and a second management channel. The Linux VM cannot use PowerShell Direct (Windows guests only; see Question 4), so it needs vsock or SSH.
- Concurrency arithmetic on a 32 GB host is my own illustration, not a sourced figure. With each agent's VM capped at a fixed size, the host RAM budget minus host OS and Hyper-V overhead divided by per-agent size gives the ceiling. Under the one-VM design, the WSL memory cap sits inside the agent's RAM budget, so the WSL cap and the VM size have to be set together.
- Licensing (from the Soul doc and the Hyper-V overview) favours Datacenter over Standard once more than two Windows VMs run per host. A Linux guest in the two-VM design would not be a Windows VM, so under the two-VM design the Windows-licence count is unchanged. This is an inference from the Soul doc's licence model, not a Microsoft source fetched here.

### Gaps
- No sourced RAM, CPU, boot-time, or saved-state figures for either topology.
- Hyper-V checkpoint behaviour (standard vs production, saved-state semantics for running VMs with nested WSL) could not be verified.
- The maximum number of simultaneously active agents is not sourced. It depends on the RAM budget set above, which needs a benchmark.

## Key Question 3: Automating Hyper-V from .NET

### Takeaway
Not verified in this session. The Hyper-V PowerShell module is the documented management surface; PowerShell Direct and Hyper-V sockets are documented for the guest side. Calling the module from .NET via the PowerShell SDK, or calling the WMI/CIM provider, are both plausible routes but neither was fetched or confirmed.

### Cited Findings
- Microsoft documents the Hyper-V PowerShell module and the Hyper-V Manager as management options; the module "enables advanced scripting and automation scenarios." — [Microsoft Learn: Hyper-V overview](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/overview)
- Examples in the PowerShell Direct page use `Get-VM` and `Enter-PSSession -VMName`, which confirms the module is present and usable from a Hyper-V host. — [Microsoft Learn: PowerShell Direct](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/powershell-direct)
- Hyper-V sockets require a service GUID registered under `HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices`. Registration gives "WMI management for enable, disable, and listing available services." — [Microsoft Learn: Make your own integration services](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service)

### Inferences
- The lowest-risk .NET path is to drive the Hyper-V PowerShell module from a .NET service running as a Hyper-V administrator on the host, using the PowerShell SDK. This keeps the surface the Microsoft docs describe and avoids undocumented internals.
- Differencing VHDX creation (`New-VHD -ParentPath ... -Differencing`) and VM create, start, stop, save, and memory/processor caps are all reachable through the module. The exact cmdlets were not verified in this session.
- Hyper-V sockets are a Windows-socket address family (`AF_HYPERV`), and the docs present it through Winsock/C. From .NET this needs P/Invoke or a custom `SocketAddress`. Not verified whether `System.Net.Sockets` exposes this family directly.

### Gaps
- WMI/CIM namespace `root\virtualization\v2` class names and methods (`Msvm_ComputerSystem`, `Msvm_VirtualSystemManagementService`) were not fetched. Use is plausible but unconfirmed here.
- Host Compute Service (HCS) API and the .NET libraries that wrap it (for example any `Microsoft.*` NuGet packages) were not verified. Do not assume a NuGet package exists without checking NuGet directly.
- Whether the PowerShell SDK or `System.Management` is the better .NET surface for long-lived automation was not measured.

## Key Question 4: Host-to-guest communication (HvSocket, PowerShell Direct, SSH/WinRM, in-guest .NET agent, passing wsl.exe)

### Takeaway
Hyper-V sockets are the documented, network-free channel for a Windows guest and a Linux guest (AF_VSOCK). PowerShell Direct is documented for Windows guests only and does not need networking. For a Linux VM in the two-VM design, PowerShell Direct is unavailable, so vsock or SSH is the channel. Passing `wsl.exe` through is plausible via PowerShell Direct or an in-guest agent but has not been verified end to end.

### Cited Findings
- Hyper-V sockets "run without using networking and all data stays on the same physical memory." Supported host: Windows 10+ and Server 2016+. Supported guests: Windows 10+, Server 2016+, and Linux with Linux Integration Services, which needs `CONFIG_VSOCKET=y` and `CONFIG_HYPERV_VSOCKETS=y`. — [Microsoft Learn: Make your own integration services](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service)
- Addressing: Windows uses `AF_HYPERV` with a VM ID GUID and a service GUID; Linux guests use `AF_VSOCK` with `svm_cid` and `svm_port`. Listeners can bind wildcard VM IDs. Capabilities: "Data stream only"; no block memory. — [Microsoft Learn: Make your own integration services](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service)
- PowerShell Direct requirements: host Windows 10 / Server 2016+ with Hyper-V; guest Windows 10 / Server 2016+; the VM must run locally on the host, be running, have at least one configured user profile; the caller must be a Hyper-V administrator and supply guest credentials. "Use PowerShell Direct regardless of network configuration." — [Microsoft Learn: PowerShell Direct](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/powershell-direct)
- PowerShell Direct supports `Invoke-Command -VMName` (single command) and `New-PSSession`/`Copy-Item` (persistent session, file transfer) in builds 14280+. Older builds (before 14500) had a bug needing explicit `-Credential`. — [Microsoft Learn: PowerShell Direct](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/powershell-direct)
- WSL can run Windows executables from Linux, and the `[interop]` section of `wsl.conf` controls this (`enabled`, `appendWindowsPath`). — [Microsoft Learn: WSL FAQ](https://learn.microsoft.com/en-us/windows/wsl/faq); [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)

### Inferences
- Recommended control plane for the Windows guest: a small .NET agent inside the guest listening on an `AF_HYPERV` service GUID registered on the host. The host connects to the VM ID. This avoids the network and works with the documented registration model. The .NET-side socket implementation needs P/Invoke or raw Winsock; not verified as available in the BCL.
- For the Linux VM in the two-VM design: a Linux `AF_VSOCK` listener is the matching channel, with the host side still on the `AF_HYPERV` address family via the VSockTemplate GUID mapping shown in the docs.
- Passing `wsl.exe` through: a PowerShell Direct `Invoke-Command -VMName <name> -ScriptBlock { wsl.exe -d <distro> -- <cmd> }` is the documented-pattern route, or the in-guest agent can spawn it. In either case the command runs under the guest's logged-in profile, so a headless agent needs a session and profile that exist at boot. Not verified.
- Note the zero-host-access requirement from the Soul doc: `wsl.conf` `[interop] enabled = false` blocks Linux from launching Windows processes, which is the setting to use inside the agent's WSL distro.

### Gaps
- Whether a non-interactive PowerShell Direct session can start a WSL distro that persists after the session ends is unverified.
- SSH and WinRM were not fetched. Their availability on Server Core and their security posture were not verified in this session.
- Whether .NET 9+ exposes `AF_HYPERV` or `AF_VSOCK` natively is unverified.

## Key Question 5: Shared storage (SMB, 9p, virtiofs), security, keeping Soul Passport off VM disks

### Takeaway
Two mechanisms are documented for putting a scoped folder inside a WSL distro: `/etc/fstab` can mount an SMB share at WSL start (`mountFsTab`), and WSL's own virtiofs path is experimental. Automatic Windows drive mounting can be turned off. This supports the design's "one scoped mounted folder" rule, though the Hyper-V-VM-level share mechanism was not verified.

### Cited Findings
- `/etc/fstab` "is a file where you can declare other filesystems, like an SMB share," and `[automount] mountFsTab` defaults to true. — [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- `[automount] enabled = false` stops fixed Windows drives from being mounted automatically; they can still be mounted manually or via fstab. — [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- `.wslconfig` `virtiofs` is "An experimental setting to use VirtioFS for Windows filesystem shares. Requires `virtio` and `hostFileSystemAccess` to be enabled." — [Microsoft Learn: Advanced settings configuration in WSL](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- Hyper-V sockets carry "data stream only"; they are not a file-share mechanism. — [Microsoft Learn: Make your own integration services](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service)
- WSL's `/mnt/<drive>` DrvFs mounts follow Windows permissions and are controlled by Windows, unlike files under the Linux root. — [Microsoft Learn: WSL FAQ](https://learn.microsoft.com/en-us/windows/wsl/faq)

### Inferences
- The Soul design's scoped-mount rule maps onto WSL settings: `[automount] enabled = false`, `[interop] enabled = false`, and a single `/etc/fstab` entry for the one allowed folder. The mount source should be a host SMB share that is read-write only for that agent. Credentials for that share should not be stored on the agent's disk.
- SMB from the host to the guest is the most mature mechanism, but the SMB server runs on the host and must be locked to the Hyper-V VM network only, with per-agent accounts. Hyper-V sockets are not a substitute for file transfer.
- Soul Passport: keep it host-side (in the host database or a host-only encrypted store) and release only short-lived, scoped tokens to the agent through the vsock/HvSocket channel. This matches the Soul doc's direction and the Hyper-V socket "data stream" model. The mechanism is an inference, not a documented pattern.

### Gaps
- No Microsoft documentation was retrieved on the Hyper-V VM-to-host SMB or file-share options (for example, host-to-VM SMB, enhanced session, or VM integration service file copy). Whether Hyper-V offers a virtiofs-style share to Windows guests is unverified.
- The security implications of the host SMB share (signing, encryption, and auth model) were not researched.

## Key Question 6: The differencing-disk gotcha: updating the golden image and rebase practice

### Takeaway
The Microsoft differencing-disk page returned 404, so the child-invalidates-on-parent-change rule is not sourced. The Soul doc treats it as a known gotcha. The safe pattern is to treat the golden image as immutable, build new parents, and create fresh children, with durable Soul data kept off the child disk. WSL's own export/import gives a per-distro backup and move path.

### Cited Findings
- `wsl --export <Distro> <FileName> --format vhd` and `wsl --import-in-place` (or `--import ... --vhd`) move a distro's file system as a VHD. — [Microsoft Learn: WSL FAQ](https://learn.microsoft.com/en-us/windows/wsl/faq)
- Hyper-V "snapshot virtual machines before making changes provides a safety net for experimentation and rollback" — generic statement, not a parent/child warning. — [Microsoft Learn: Hyper-V overview](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/overview)
- The Soul doc states the rule directly: the parent cannot change without invalidating children, so Soul data must live outside the VM disk. — Bazaar.Soul.md, Section 2 (internal design doc, not an external source).

### Inferences
- Golden-image pipeline: build a versioned parent VHDX, mark it read-only, and never write to it after children exist. Toolchain updates create a new parent version; each agent gets a fresh child from the new parent. Agent-specific state is migrated through the host-side Soul store, not preserved on the child.
- For WSL inside the VM, the same principle applies one level down: a new WSL distro image per toolchain version, with `wsl --export`/`--import` for moves, rather than in-place upgrades of a shared distro.

### Gaps
- Child-disk invalidation behaviour (what Hyper-V does when a parent is modified, moved, or re-pathed, and the exact failure mode) is not verified from a primary source. Do not treat the specific mechanism as confirmed until the Hyper-V differencing-disk documentation is read directly.
- No sourced best practice for rebasing existing children onto a new parent (Hyper-V has no documented rebase command in the sources reached).

## Key Question 7: Alternatives (Windows containers with Hyper-V isolation, Windows Sandbox, WSL2 distros without VMs, Cloud Hypervisor, Firecracker)

### Takeaway
Hyper-V-isolated Windows containers are a documented, supported option on Server and give each container its own kernel, but are not verified for running WSL or Roblox Studio. Windows Sandbox is not available on Server. Cloud Hypervisor's documentation covers Linux/KVM hosts only and does not describe a Windows host. Firecracker is Linux-only on both host and guest-kernel requirements.

### Cited Findings
- Windows containers: "Windows containers running on Windows Server default to running with process isolation." Hyper-V isolation runs "each container inside of a highly optimized virtual machine and effectively gets its own kernel." Applies to Windows Server 2016 through 2025. — [Microsoft Learn: Isolation modes](https://learn.microsoft.com/en-us/virtualization/windowscontainers/manage-containers/hyperv-container)
- Hyper-V isolation nested on a Hyper-V VM is supported in production with one level of nesting, but "there's performance overhead" in start-up, storage, network, and CPU. — [Microsoft Learn: Nested virtualization](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/nested-virtualization)
- Windows Sandbox: supported on Windows Pro, Enterprise, Pro Education/SE, and Education; "currently not supported on Windows Home"; "doesn't allow multiple instances to run simultaneously"; disposable (state is discarded on close). It is not listed for Windows Server. — [Microsoft Learn: Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-overview)
- Cloud Hypervisor: runs "on top of the KVM hypervisor and the Microsoft Hypervisor (MSHV)"; minimum host kernel 6.8; the only host requirement listed is "Host with KVM enabled," and host-side commands are Linux-based. The page does not describe a Windows host. — [Cloud Hypervisor README](https://github.com/cloud-hypervisor/cloud-hypervisor/blob/main/README.md)
- Cloud Hypervisor Windows guests: support starts at release 0.10.0; "Any modern Windows Server version is compatible, as well as Windows 11." Tested: Windows Server 2019, Windows Server Core 2004, Windows 11 IoT Enterprise LTSC 2024. UEFI only (`CLOUDHV.fd`), BIOS boot unsupported. `kvm_hyperv=on` is required. No VGA; console via Windows SAC. Windows 11 with TPM 2.0 "was not proven successful." — [Cloud Hypervisor: Windows guest docs](https://github.com/cloud-hypervisor/cloud-hypervisor/blob/main/docs/windows.md)
- Firecracker: "Firecracker supports x86_64 and aarch64 Linux" and "requires read/write access to `/dev/kvm`." Guest requirement: an uncompressed Linux kernel and an ext4 image. The page does not mention Windows as host or guest. — [Firecracker: Getting started](https://github.com/firecracker-microvm/firecracker/blob/main/docs/getting-started.md)
- WSL2 is a "lightweight utility VM," not a general-purpose VM (see Question 1). — [Microsoft Learn: WSL FAQ](https://learn.microsoft.com/en-us/windows/wsl/faq)

### Inferences
- Confirmed: Firecracker cannot boot Windows and does not run on a Windows host. Its documented requirements exclude both.
- Cloud Hypervisor can boot Windows guests, but its documented host is Linux (KVM or MSHV). Running Cloud Hypervisor on a Windows host is not documented, so the Soul doc's implied path (a Windows host running Cloud Hypervisor) has no support basis. The Soul doc's action item to test is still correct, but the test would need a Linux host, which conflicts with the "Windows-only server host" constraint.
- Hyper-V-isolated Windows containers are a credible alternative to full VMs for headless agents: each gets its own kernel, the host stays Windows, and the image model fits the golden-image pipeline. Open question: whether WSL2 and the .NET agent run inside such a container.
- The Soul doc's claim that Cloud Hypervisor boots "sub-second" is not sourced here. Remove it or source it before relying on it.

### Gaps
- Windows containers with WSL2 inside, and whether the Windows container image can host a long-running .NET agent under Hyper-V isolation, are not verified.
- Cloud Hypervisor on Windows host: no documentation found. Treat as unsupported until shown otherwise.
- Cloud Hypervisor boot-time figures were not verified.

## Key Question 8: Roblox Studio: official Linux client, and running in a headless Windows VM

### Takeaway
Roblox does not provide an official Linux Studio client, based on a Roblox DevForum quote and a search-result snippet that could not be cross-checked on Roblox's own requirements page (403). Studio on Linux exists only through the community Vinegar project, which runs it under Wine. Whether Studio runs in a headless Windows Server Core VM was not verified; this is a likely blocker because Studio needs a graphical display, which Server Core lacks.

### Cited Findings
- Vinegar "Runs Roblox Studio using Wine," per a community guide. A Roblox developer's forum quote (Bitdancer) describes the Wine support as "unofficial, as it was an effort of our team in their free time." — [Roblox DevForum: The Ultimate Guide on How to Run Roblox on Linux](https://devforum.roblox.com/t/the-ultimate-guide-on-how-to-run-roblox-on-linux-studio-player/3171920)
- Roblox's official computer requirements (from a search snippet, not the page itself) list 64-bit Windows 10 or 11 and macOS 10.13 or later, and state that Linux is explicitly unsupported. The Roblox help page returned HTTP 403 when fetched. — search result snippet only; primary page not retrieved.
- Community reports of Studio issues under Wine (shadow artifacts, camera problems, crashes after updates) are from a DevForum thread and a forum guide. Not official. — [Roblox DevForum: Vinegar choppy camera movement and visual errors](https://devforum.roblox.com/t/vinegar-choppy-camera-movement-and-visual-errors/4045465)

### Inferences
- The "no official Linux client" premise holds under the sources reached. The Roblox official requirements page should be checked directly before the decision is recorded.
- Given that Studio is Windows-only and needs an interactive display, Bazaar.Windows (a human-facing desktop) remains the correct place for Studio work, which matches the Soul doc's division of labour. Running Studio inside a headless Server Core agent VM is not supported by any source reached.

### Gaps
- Whether Roblox Studio runs at all on Windows Server Core (no desktop shell) or in a Server with Desktop Experience VM was not verified. Not verified: GPU or OpenGL/DirectX requirements for Studio inside a VM.
- Official Roblox statement on supported Windows Server editions or VM use: not found.

## Cross-cutting: Recommendation for the topology decision (inference, not a source)

- Both topologies are technically sound on documented Microsoft support. The single-VM design (Server Core + WSL2, nested) is supported but has an unmeasured nested-stack cost and WSL idle-shutdown behaviour that must be configured away. The two-VM design uses the mainstream non-nested topology but doubles per-agent guest overhead and needs a vsock/SSH control channel for the Linux half.
- Because Roblox Studio is Windows-only and the Soul design already routes Studio to Bazaar.Windows, the agent VM's Windows requirement comes from tooling, not from Studio. That weakens the case for a second Windows VM per agent.
- Recommended next step: benchmark one agent on both topologies with the same toolchain, measuring RAM at idle and under build load, boot-to-ready time, checkpoint/save time, and WSL I/O latency. Use the Hyper-V socket control channel in both cases.

## Consolidated Gaps (for the report writer)
1. No sourced RAM, CPU, boot-time, checkpoint, or saved-state figures for either topology.
2. Hyper-V differencing-disk and checkpoint pages returned 404; child invalidation mechanics and rebase practice unverified.
3. Roblox official requirements page returned 403; official Linux-support statement rests on a snippet and a forum quote.
4. WMI/CIM `root\virtualization\v2` classes, HCS, and .NET NuGet options were not verified.
5. Whether `AF_HYPERV`/`AF_VSOCK` is usable directly from .NET is unverified.
6. Whether Hyper-V-isolated Windows containers can run WSL2 or Roblox tooling is unverified.
7. Cloud Hypervisor: no evidence of Windows host support found in the README or Windows guest docs.
8. Search quota ran out after five queries; this report is based on primary-page fetches plus those five searches. A re-run with more search budget would strengthen the community-report side (nested-WSL stability, Server Core Studio, Cloud Hypervisor Windows host).
