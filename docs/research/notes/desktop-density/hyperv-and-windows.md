# Hyper-V and Windows: dense GUI guests, screenshots and input without a viewer

Research notes, 2026-11-19. Read-only web research. Tags: VERIFIED = primary source page read in this session; SECONDARY = third-party or search-result summary only; UNVERIFIED = inferred or not found. Not legal advice.

Primary-source note: all Microsoft Learn pages cited were fetched directly unless tagged otherwise. Several Win32 Hyper-V WMI pages are dated 2018-2022 (page metadata), so they say nothing about behaviour on Windows 11 25H2/26H1 builds.

## 1. GetVirtualSystemThumbnailImage (screenshot without a viewer)

- Signature: `uint32 GetVirtualSystemThumbnailImage([in] CIM_VirtualSystemSettingData REF TargetSystem, [in] uint16 WidthPixels, [in] uint16 HeightPixels, [out] uint8 ImageData[])`. Namespace `root\virtualization\v2`, method on `Msvm_VirtualSystemManagementService`, implemented in vmms.exe. Source: https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/getvirtualsystemthumbnailimage-msvm-virtualsystemmanagementservice . VERIFIED.
- Pixel format: "raw RGB 565 format" (16 bits per pixel). Same page. VERIFIED. Buffer is little-endian 16-bit words per the page's VBScript sample (low byte first: `thumbnailBytes(i+1)*&HFF + thumbnailBytes(i)`). VERIFIED (sample code), though the sample's multiplier is sloppy; decode as standard LE RGB565.
- Target must be the VM's `Msvm_VirtualSystemSettingData` (reached via `Msvm_SettingsDefineState`) or a snapshot's setting data. Page says it "may represent either the current instantiation of the virtual machine, or an instance of a virtual machine snapshot". VERIFIED.
- Return codes: 0 done, 4096 job started (async job; poll), 32768-32778 errors (Access Denied 32769, Invalid state 32775, etc.). VERIFIED.
- Size limits: parameters are uint16, so 65535 max each on paper. No documented maximum on the page. A 2024 blog sizes the request to the VM's current `Msvm_VideoHead` `CurrentHorizontalResolution`/`CurrentVerticalResolution` so output follows the guest resolution: https://david-homer.blogspot.com/2024/12/take-screenshot-of-hyper-v-virtual.html . SECONDARY (blog read in full). Whether the host scales (bilinear or nearest) when the request differs from the guest resolution is not documented: UNVERIFIED. Practical rule: request the exact video-head resolution and downscale yourself.
- vmconnect not required? The docs never mention vmconnect. The Microsoft sample starts the VM and calls the method with 16x16, no viewer opened. The method is a vmms call into the VM worker, not a client of vmconnect. Conclusion: works with no viewer window (docs imply it; the sample's flow shows no viewer). Confidence: VERIFIED that docs/sample do not require a viewer; UNVERIFIED by live test. Needs a spike on the actual host.
- Linux guests: not documented anywhere I read. The thumbnail comes from the synthetic video device frame buffer on the host side (hyperv_fb/hyperv_drm write to it through VMBus synthvid). libvirt's Hyper-V driver uses this call and converts RGB565 to RGB888 (SECONDARY, search-result summary of a libvirt commit). Linux support: UNVERIFIED by test; expected to work whenever the guest uses the Hyper-V synthetic video device (Gen2 default).
- Privileges: the docs say run "on the virtual machine host server ... with Administrator privileges" and that access to the class "might be restricted by UAC Filtering". VERIFIED. Membership in the "Hyper-V Administrators" local group is the usual way to avoid full admin for WMI; this page does not state it: UNVERIFIED for this method. A Windows Service running as SYSTEM or as a Hyper-V Administrators member needs no UAC elevation.
- Related: `Msvm_VideoHead` exposes CurrentHorizontalResolution/CurrentVerticalResolution (used by the MS ClickButton sample; VERIFIED in that sample).
- Alternative for full-rate capture: not found in primary docs. RDP/Enhanced Session or an in-guest agent (Bazaar.Guest.Agent) are the other routes; out of scope for this note.

## 2. Synthetic input from the host (Msvm_Keyboard, Msvm_SyntheticMouse)

Keyboard (https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-keyboard, VERIFIED):
- One instance always present per VM, found via `Msvm_SystemDevice` association. Layout default "00000409" (US). `UnicodeSupported` property exists.
- Methods: `TypeText`, `TypeKey`, `PressKey`, `ReleaseKey`, `TypeScancodes`, `TypeCtrlAltDel`, `IsKeyPressed`, `Reset`, `RequestStateChange`. Power/enable methods unsupported.
- `TypeText(string asciiText)`: equivalent to PressKey+ReleaseKey per character. Max 512 ASCII chars; Unicode 256-512 depending on surrogate pairs. Return 1 = untranslatable characters. https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/typetext-msvm-keyboard . VERIFIED.
- `PressKey(uint32 keyCode)`: keyCode is a Windows virtual-key code; VK_MENU/CONTROL/SHIFT are remapped to left variants (164/162/160). Key stays down until `ReleaseKey`. https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/presskey-msvm-keyboard . VERIFIED.
- `TypeScancodes(uint8[] Scancodes)`: raw scan codes; no format detail (set 1 vs 2, extended prefixes) in the page. https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-keyboard-typescancodes . VERIFIED (page is sparse); scan code set details UNVERIFIED.
- Keyboard layout: TypeText maps characters using the VM's keyboard layout property; a non-US guest layout may mistype. Not stated in docs: UNVERIFIED.

Mouse (https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-syntheticmouse, VERIFIED):
- Methods: `SetAbsolutePosition(sint32 h, sint32 v)`, `ClickButton(uint32 buttonIndex)` (1-based), `SetButtonState`, `GetButtonState`, `SetScrollPosition`, `Reset`. `NumberOfButtons = 5`, `AbsoluteCoordinates = True`, `PointingType = 3` (mouse).
- Coordinates: documented as "in pixels" (https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/setabsoluteposition-msvm-syntheticmouse). The MS ClickButton sample sets position using `Msvm_VideoHead.CurrentVerticalResolution - 1`, i.e. guest pixel coordinates (https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/clickbutton-msvm-syntheticmouse). VERIFIED. Whether internal scaling is to a 0..0xFFFF absolute range is not in docs: UNVERIFIED.
- `EnabledState` 2 = "guest supports a synthetic mouse"; 3 = guest does not. So the guest needs the Hyper-V synthetic mouse driver. VERIFIED.
- Linux guests: hid-hyperv binds the VMBus `HV_MOUSE_GUID` device, negotiates synthetic HID 2.0, and takes the report descriptor from the host (so absolute vs relative is host-defined). Source: https://raw.githubusercontent.com/torvalds/linux/master/drivers/hid/hid-hyperv.c . VERIFIED (source read). That the host injects absolute pointer reports into Linux through the same path is consistent with `AbsoluteCoordinates=True` but not tested here: UNVERIFIED by test.
- Same UAC-filtering caveat as above; same Admin requirement for WMI.
- Known limits / not found: no documented rate limit or queue depth for these calls; each call is a WMI round trip. Action latency not found.
- Windows Sandbox is different: `wsb` CLI has `exec`, `share`, `connect`, `ip` but no screenshot or input API (see item 4).

## 3. Dynamic Memory, hot-add, page sharing

- Settings: Startup RAM, Minimum RAM (as low as 32 MB, up to Startup), Maximum RAM (from Startup up to 1 TB), memory buffer %, memory weight. Source: https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/dynamic-memory . VERIFIED.
- Startup RAM is the OS-install/upgrade allotment; Dynamic Memory only reclaims after startup. Smart Paging used only on restart under pressure; temporary (expected under 10 min). Run-time changes allowed: raise Maximum, lower Minimum. Same page. VERIFIED.
- Linux: "All supported Windows Server and Windows operating systems support dynamic memory, as do many Linux distributions." https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/features-terminology . VERIFIED.
- Ubuntu 16.04-24.04 LTS list "Dynamic Memory - Hot-Add" and "Ballooning" as supported (built-in LIS) with notes: 64-bit only; startup and minimum memory should be at least the distro vendor's recommendation; apps limited to about 80 percent of RAM; on Windows hosts use multiples of 128 MB for startup/min/max or hot-add can fail. https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/supported-ubuntu-virtual-machines-on-hyper-v . VERIFIED (Windows Server column, 2016-2025; Windows 11 client host not separately listed: UNVERIFIED).
- Linux driver hv_balloon: GPL, Microsoft; hot-add code is wrapped in `#ifdef CONFIG_MEMORY_HOTPLUG`; without it the driver fails host hot-add requests. https://raw.githubusercontent.com/torvalds/linux/master/drivers/hv/hv_balloon.c . VERIFIED (partial read; summarised by fetch tool). Guest kernel must enable memory hotplug and (distro-dependent) auto-online of hot-added memory: UNVERIFIED which distros default it.
- Page sharing between VMs: Microsoft's Dynamic Memory blog says page sharing is "not the best option": 2 MB large pages make 4 KB matches unlikely, caches (SuperFetch) reduce zero pages, hashing takes hours, and it cannot free memory quickly. https://techcommunity.microsoft.com/blog/virtualization/dynamic-memory-coming-to-hyper-v-part-38230/381714 . VERIFIED for the 2010 design rationale. No current Microsoft statement found that Hyper-V now does inter-VM dedup: not found. Your belief (no page sharing) is consistent with the evidence, but the blog is a 2010-era design post, not a current feature statement. Confidence: SECONDARY-to-VERIFIED for "not implemented as of the DM design"; UNVERIFIED for 2026.
- Consequence: density on Hyper-V comes from Dynamic Memory (min RAM and ballooning), not dedup.

## 4. Windows Sandbox and the HCS family

Architecture (https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-architecture, VERIFIED):
- Dynamic Base Image: boots from the host's installed Windows; immutable files shared with the host; only a small mutable subset has pristine copies. Compressed package about 30 MB; about 500 MB on disk once installed.
- Memory: container-style cooperation with the host; host can reclaim memory from the sandbox like from a process.
- Direct map: OS binaries (example ntdll.dll) use the same physical pages as the host.
- WDDM GPU virtualization if GPU supports WDDM 2.5+; otherwise WARP CPU rendering. Battery pass-through.
- "Integrated scheduler" is a term from the 2019 blog post; the current Learn page does not use it. The search-result summary attributes it to the Windows OS Platform blog (https://techcommunity.microsoft.com/blog/windowsosplatform/windows-sandbox/301849): SECONDARY (blog not fetched by me).
- Critical constraint: direct map means the sandbox runs the exact host build; "Windows Sandbox only allows you to use the same build as your host OS." https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-faq . VERIFIED. So it cannot give a different or Linux guest, and it only shares the host's own Windows image.

Instance limit:
- "Windows Sandbox currently doesn't allow multiple instances to run simultaneously." https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-overview (page date 2026-03-29, updated 2026-10-08). VERIFIED.
- FAQ (updated 2025-09-24): "Today, Windows Sandbox only allows users to launch one Sandbox instance at a time." VERIFIED.
- `wsb` CLI (Windows 11 24H2+): `start` (returns an ID), `list`, `exec`, `stop`, `share`, `connect`, `ip`. `wsb list` lists "running Windows Sandbox sessions for the current user" and each command takes `--id`, which hints at multi-session in the API, but the docs still state one at a time. https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli . VERIFIED. No multi-instance change found for 25H2 or later in primary sources: not found.
- `wsb exec` has no process I/O (cannot read command output); `exec -r ExistingLogin` needs an active user session so you must `connect` (RDP window) first. Same CLI page. VERIFIED. This makes unattended sandbox control hard; no screenshot/input API in the CLI.
- Windows Sandbox has no persistence (state discarded; restarts initiated inside the sandbox do persist from 22H2). Overview page. VERIFIED.
- Edition: Pro/Enterprise/Education; not Home. Overview. VERIFIED.
- Community reports of 25H2 failures (error 0x800705B4 etc.) exist; SECONDARY only (search results, not read).

HCS / hcsshim / utility VM:
- Windows containers have two isolation modes: process and Hyper-V; Hyper-V isolated containers each run in "a highly optimized virtual machine" with their own kernel; `vmwp` is visible on the host. https://learn.microsoft.com/en-us/virtualization/windowscontainers/manage-containers/hyperv-container . VERIFIED. Page also says Windows containers do not support shared memory between container and host.
- Windows 10/11 Pro and Enterprise hosts: unlimited Hyper-V-isolated containers "for test or development purposes only"; Windows Server Standard: 2; Datacenter: unlimited. https://learn.microsoft.com/en-us/virtualization/windowscontainers/about/faq . VERIFIED.
- GUI in Windows containers: Microsoft docs fetched do not say a desktop is supported; search results (SECONDARY) say Windows containers do not have a GUI even from Desktop Experience images, Nano Server has no GUI stack, and RDP into containers is not supported. Official Learn statement of "no GUI" was not located. Windows containers are therefore not a route to a GUI desktop guest. Confidence: SECONDARY.
- Is Windows Sandbox's mechanism (dynamic base image + direct map) exposed for arbitrary multiple GUI guests through HCS? Not found in any primary source. The hcsshim repo and HCS schema docs were not read: UNVERIFIED. Do not assume it is.

## 5. vPMEM (.vhdpmem) and shared read-only images

- Create: `New-VHD D:\x.vhdpmem -Fixed -SizeBytes 4GB`; must be created on an existing NTFS DAX volume; only fixed format. `Add-VMPmemController`, then `Add-VMHardDiskDrive -ControllerType PMEM -ControllerLocation 1 -Path ...`. Guests can use it as block or DAX volume. Gen2 only; no live/storage migration; production checkpoints exclude pmem state. https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/manage/persistent-memory-cmdlets . VERIFIED. Page says "VM persistent memory devices are supported in Windows Server 2019" (page updated 2026-01-21); client Windows 11 support not stated: UNVERIFIED.
- Requires a host DAX volume, i.e. real NVDIMM persistent-memory hardware on the host. Not available on a typical Windows 11 desktop. (Inference from "NTFS DAX volume" requirement; hardware requirement itself is SECONDARY from search results.)
- Sharing one read-only image across many VMs through DAX so the page cache is shared (as LCOW does through HCS): NOT FOUND in the Hyper-V PowerShell/WMI docs. The cmdlet docs describe one `.vhdpmem` per device and say nothing of read-only sharing or multi-VM attach. UNVERIFIED. LCOW's use of vPMEM is an HCS feature (hcsshim); I did not read hcsshim source or HCS docs: UNVERIFIED. Treat this as a spike, not a plan; likely blocked on the host-hardware requirement anyway.
- Hyper-V "shared drives" (shared VHDX) exist for Gen2 but target clustering, not read-only page-cache sharing. https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/features-terminology . VERIFIED (existence only).

## 6. Smallest practical Windows guest with a GUI able to run headed Chromium

Requirements facts (primary):
- Windows Server 2025: 2 GB minimum RAM for Server Core and Desktop Experience (4 GB recommended for Desktop Experience); a VM at the minimum (1 core, 1,024 MB) fails install, allocate 1,280 MB or more for setup, then can lower to 1,024 MB "depending on actual server configuration". 32 GB disk minimum; Server Core about 4 GB smaller than Desktop Experience. https://learn.microsoft.com/en-us/windows-server/get-started/hardware-requirements . VERIFIED. Windows Server 2016-2022: 1 GB Core / 2 GB Desktop Experience. VERIFIED (same page).
- Windows 11 IoT Enterprise LTSC (2024): preferred 4 GB RAM, 64 GB SSD, 2 cores; optional minimum 2 GB RAM, 16 GB storage, TPM/Secure Boot optional, DirectX 10/none. https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/hardware/system_requirements . VERIFIED (these are OEM minimums, not idle usage).
- Windows 11 Enterprise/Pro minimums: not fetched this session (commonly 4 GB, 64 GB): UNVERIFIED here.
- Idle RAM figures with primary sources: NOT FOUND. Microsoft does not publish idle RAM per SKU in what I read. Any figure from memory (Server Core roughly 300-500 MB idle; Win11 roughly 1.5-2.5 GB; Win11 LTSC lower) is UNVERIFIED and must be measured on the target host (Dynamic Memory counters). A Windows Sandbox test cited in a search summary: a 4 GB sandbox used about 237 MB host memory idle (SECONDARY, from a third-party test, not read).
- Server Core + GUI: Server Core has no desktop shell; the "App Compatibility Feature on Demand" adds some GUI tools (Task Manager, MMC, Explorer etc. per recollection). I could not fetch the Microsoft page (404 on the URLs I tried): UNVERIFIED. Whether Chromium headed runs on Server Core + FOD: UNVERIFIED. Headless Chromium on Server Core is well documented only informally; not researched here. Risk: Chromium needs a full user session with a desktop window station; Server Core has no shell, so headed use is doubtful.
- Server with Desktop Experience is the only variant where the headed-browser path is non-speculative; it is a full desktop shell with the same UI stack as Windows 11 Server-flavoured.

Licensing notes (research only; all SECONDARY except where noted):
- Hyper-V on Windows 11 Pro needs no extra license for the role itself; each Windows guest needs its own license; Windows Server guests are covered only by Server license rights; Windows 11 Pro covers one instance per device. Source: Microsoft Q&A thread summaries (search results, not read): https://learn.microsoft.com/en-us/answers/questions/1346084/licencing-windows-11-vms-correctly-running-on-hype and https://o365hq.com/blog/every-way-to-run-windows-as-a-virtual-machine-licensing-channels-pros-cons-and-cost . SECONDARY.
- Windows containers EULA: unlimited Hyper-V-isolated containers on Pro/Enterprise "for test or development purposes only"; IoT editions "unlimited*" with commercial-agreement caveats. https://learn.microsoft.com/en-us/virtualization/windowscontainers/about/faq . VERIFIED (that is about containers, not VMs).
- IoT Enterprise LTSC is OEM/embedded-channel licensed and ties to devices; whether it can be bought per VM for a server-hosted fleet: UNVERIFIED.
- Windows Server Standard (2 VMs per license) / Datacenter (unlimited) licensing for Server guests requires the host to be licensed with Server; a Windows 11 Pro host is not a Server-licensed host: UNVERIFIED primary; flagged for counsel before first public release.
- Evaluation images exist (Windows Enterprise 90-day, Server 180-day) but are unsuitable for a persistent company: not researched.

## 7. GPU-PV / RemoteFX

- RemoteFX vGPU was removed (disabled by the July 2020 updates, removed with April 2021 updates); no security updates for it; DDA is the Microsoft-named alternative. KB4570006 (search-result summary, SECONDARY): https://support.microsoft.com/servicing/os/windows/2020/07/kb4570006-update-to-disable-and-remove-the-remotefx-vgpu-component-in-windows
- Windows Server 2025 officially supports GPU Partitioning (GPU-P) via SR-IOV, with per-VM fraction of GPU; one partition per VM; requires IOMMU-capable hosts, vendor drivers, Server 2025 host (Datacenter if clustering). https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/plan/plan-for-gpu-acceleration-in-windows-server and https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/partition-assign-vm-gpu . VERIFIED.
- Needed for a software-rendered desktop? Not needed for a basic desktop; Microsoft frames GPU acceleration as needed for CAD, 3D, rendering, ML ("most customers consider GPUs a necessity" for those). Plan page. VERIFIED. For Chromium with software rendering (SwiftShader/WARP) it is not required; CPU cost per VM not measured: UNVERIFIED. Expect not needed, confirmed as far as docs go.
- Client Windows 11 `Add-VMGpuPartitionAdapter` (unofficial, long-used): not found in primary docs I fetched; the page above is Server 2025 scoped. UNVERIFIED for Win11 Pro host.
- Windows Sandbox falls back to WARP when no WDDM 2.5 GPU is available (architecture page). VERIFIED. Shows WARP is a Microsoft-supported desktop renderer.

## 8. Differencing VHDX

- `New-VHD -ParentPath base.vhdx -Path child.vhdx -Differencing`. https://learn.microsoft.com/en-us/powershell/module/hyper-v/new-vhd . VERIFIED.
- Concept: changes go to the child; parent kept clean ("differencing disks ... useful for testing, development"). https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/features-terminology . VERIFIED.
- Boot time and disk behaviour with many children: no Microsoft numbers found. Known constraints from general knowledge (UNVERIFIED here): parent must stay read-only and unmodified or children corrupt; chain depth costs I/O; each child is independent so the host page cache for the parent is shared across children naturally (host-level, not guest-level); guests still each hold their own guest page cache copy of identical files, so there is no RAM dedup (consistent with item 3). Needs a benchmark.
- Linux-specific VHDX tuning from Microsoft: when creating dynamic VHDX use 1 MB block size (default 32 MB), prefer ext4 and `mkfs.ext4 -G 4096`. https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/best-practices-for-running-linux-on-hyper-v . VERIFIED. Also set I/O scheduler to none/noop; Gen2 grub timeout quirk; copying a Gen2 Ubuntu disk needs the `EFI/boot/bootx64.efi` copy (Ubuntu note 11). VERIFIED. These matter when cloning children from a Linux parent.
- Generic-disk cloning gotchas for Windows children (sysprep, SID, machine identity): not researched.

## Linux guest video facts (relevant to items 1 and 2)

- hyperv_fb is deprecated in favour of the DRM driver (`CONFIG_DRM_HYPERV`, hyperv_drm, in kernel since 5.14), and a patch series removing hyperv_fb from the kernel was posted 2025-12-26; mirrors show a removal commit, final release not confirmed. Source: lkml threads (SECONDARY): https://lkml.iu.edu/2512.3/01685.html . Design for hyperv_drm only. Both bind the Hyper-V synthetic video device; hyperv_drm also has a PCI stub for Gen1. https://raw.githubusercontent.com/torvalds/linux/master/drivers/gpu/drm/hyperv/hyperv_drm_drv.c . VERIFIED (source read).
- Resolution caps: none in drv.c; caps depend on host MMIO sizing and the mode-setting file, not read: UNVERIFIED. The thumbnail size follows the guest video head (item 1).

## Decision-relevant takeaways (also in my reply)

1. Thumbnail WMI call is the supported headless screenshot path for Hyper-V: RGB565, Admin-level, no viewer documented as needed. Still needs a live spike (Linux guest, throughput, max size).
2. Keyboard and mouse injection is documented, absolute mouse in guest pixels, TypeText capped at 512 chars. Works only with synthetic HID/mouse drivers in the guest.
3. Hyper-V density = Dynamic Memory (min 32 MB) plus ballooning; no inter-VM page sharing.
4. Windows Sandbox is single-instance by Microsoft's current docs, same-build-as-host only, no screenshot/input API, no persistence: not a fleet primitive.
5. Windows containers/HCS are not a GUI-desktop route (SECONDARY); vPMEM sharing is unverified and needs NVDIMM host hardware.
6. Idle RAM per SKU: no primary source; must be benchmarked.
