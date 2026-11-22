# KVM density and Linux desktop stacks: research notes

Date: 2026-11-19. Scope: running many lightweight Linux/Windows guests with a working desktop, plus screenshot and input injection with no viewer.
Confidence tags: VERIFIED = primary source page fetched and read this session; SECONDARY = mailing list, bug tracker, vendor blog, search-result summary; UNVERIFIED = recalled or inferred, not read. "Not found" means I did not find it.
Caveat: the fetch tool summarises pages with a small model, so VERIFIED quotes are as relayed by it. Spot-check anything load-bearing.

## 1. QMP screendump, input-send-event, libvirt screenshot

- QMP `screendump` exists since 0.14. Args: `filename` (new file), `device` (display device ID, default primary; since 2.12), `head` (default 0; since 2.12), `format` (default PPM; since 7.1). VERIFIED. https://gitlab.com/qemu-project/qemu/-/raw/master/qapi/ui.json
- `ImageFormat` enum is `png` and `ppm`, since 7.1. VERIFIED (same URL). PNG needs a QEMU build with libpng. SECONDARY: https://src.opensuse.org/dfaggioli/qemu/commit/9a0a119a382867
- QMP `input-send-event` since 2.6. Args: `device`, `head`, and `events` (a list of InputEvent). Event kinds are `key`, `btn`, `rel`, `abs`, and `mtt` (multi-touch, since 8.1). Axis names are `x` and `y`. VERIFIED (ui.json URL above).
  - Absolute pointer (`abs`) needs the guest to have a tablet-type device (usb-tablet or virtio-tablet). UNVERIFIED, from memory. Check before relying on it.
- `screendump` with `-display none`: not found in any primary source. No doc says it works or fails. What I found:
  - A QEMU issue on a segfault with PNG screendump: https://gitlab.com/qemu-project/qemu/-/issues/1210 (SECONDARY, not read in detail).
  - Old Red Hat bugs where `-nographic` plus `screendump` crashed qemu (SECONDARY): https://bugzilla.redhat.com/show_bug.cgi?id=691830 and others in the search results.
  - Test it empirically on the target QEMU version. Plan to try std VGA / virtio-vga with `-display none` and check the output is non-black.
- Video devices: the `device`/`head` args refer to a QEMU display device (a QemuConsole). virtio-vga with multiple heads has had screendump crash bugs on non-primary heads (SECONDARY): https://bugzilla.redhat.com/show_bug.cgi?id=1684383
- Stale or lagging frames with QXL+SPICE: https://bugs.launchpad.net/qemu/+bug/1314293 (SECONDARY).
- libvirt `virDomainScreenshot(domain, stream, screen, flags)`: "Take a screenshot of current domain console as a stream. The image format is hypervisor specific." `screen` is a sequential monitor ID, flags must be 0, returns a MIME type string. Since 0.9.2. VERIFIED. https://gitlab.com/libvirt/libvirt/-/raw/master/src/libvirt-domain.c
- `virsh screenshot domain [imagefilepath] [--screen screenID]`: "Takes a screenshot of a current domain console and stores it into a file." VERIFIED. https://libvirt.org/manpages/virsh.html
- `virsh send-key domain [--codeset codeset] [--holdtime ms] keycode...`: default codeset is linux; multiple keycodes are sent simultaneously and may arrive in random order. VERIFIED (same page). Keys only; no pointer.
- Does libvirt need a `<graphics>` element for screenshots? No primary source found. A Red Hat bug shows a guest with a video device but no `<graphics>` gave a "no such screen" style error from `virsh screenshot`, and adding a VNC `<graphics>` made it work: https://bugzilla.redhat.com/show_bug.cgi?id=1366119 (SECONDARY). Same search: libvirt stores QEMU screenshots as PPM, per the old patch series https://lists.libvirt.org/archives/list/devel@lists.libvirt.org/thread/SSUWVRTQTKEEXW5OKDYXOSALLD7ZFNMD (SECONDARY).
  - Implication: for libvirt, a cheap `<graphics type='vnc'>` (even bound to a unix socket, no client) is the known-working way. Bypassing libvirt and talking QMP directly (`qemu-monitor-command`) is possible but not verified for `-display none`.
- Hyper-V (the first host): screenshots and input come from different mechanisms (Msvm_VideoHead / synthetic keyboard WMI). Not researched here.

## 2. Memory density on KVM

- KSM: scans registered anonymous memory, merges identical pages into one write-protected page. "KSM only merges anonymous (private) pages, never pagecache (file) pages." Opt-in via `madvise(MADV_MERGEABLE)`. Originally developed for KVM to fit more VMs in RAM. VERIFIED. https://docs.kernel.org/admin-guide/mm/ksm.html
- KSM CPU cost: ksmd defaults to `sleep_millisecs=20`, `pages_to_scan=100` ("chosen for demonstration purposes"). "Scans may use a lot of processing power." A scan-time advisor caps CPU via `advisor_max_cpu` (default 70). It adds latency on copy-on-write and swap-in, and it needs bookkeeping memory. VERIFIED (same page).
- KSM security: the kernel page gives no side-channel guidance. Known attacks on cross-VM dedup:
  - Flip Feng Shui (USENIX Security 2016) abuses KSM to place a victim page and Rowhammer a bit, with a demo against OpenSSH in a co-hosted VM. SECONDARY. https://www.usenix.org/conference/usenixsecurity16/technical-sessions/presentation/razavi
  - Dedup side/covert channels are described in Suzaki 2013 and the 33C3 talk (single-bit "does this page exist" channel). SECONDARY. https://infocondb.org/con/ccc/33c3/memory-deduplication-the-curse-that-keeps-on-giving-a-tale-of-3-different-memory-deduplication-based-exploitation-techniques
  - Implication: KSM across VMs of different trust domains leaks; agents of one company are mutually distrusting at some level, so treat KSM as an opt-in tradeoff.
- Per-guest savings numbers for identical guests: not found in a primary source (I did not locate a measured figure). Do not quote one.
- virtio-balloon free page reporting: QEMU property `free-page-reporting` (`VIRTIO_BALLOON_F_REPORTING`) returns unused guest pages to the host automatically. `free-page-hint` (`VIRTIO_BALLOON_F_FREE_PAGE_HINT`) is a separate feature mainly for migration. Both are off by default. Libvirt docs describe reporting as returning unused guest pages to the hypervisor for other guests; arrived in QEMU 5.1. SECONDARY (patch lists and search summaries). https://lists.libvirt.org/archives/list/devel@lists.libvirt.org/thread/AO3GBVXU4Q3XXU2XDIZVSBJQWOIPAJFU/
  - The kernel doc page I tried for free page reporting returned 404: not found.
- virtio-mem: memory hot(un)plug, 2 MiB granularity in most configs, x86-64/aarch64/RISC-V/s390x. Min versions: Linux 5.8, QEMU 5.1, libvirt 7.9. Windows support is in virtio-win but "technology-preview", "unstable", with no public virtio-win release at the time of that page. VERIFIED (page read, date unknown). https://virtio-mem.gitlab.io/
- zram: compressed RAM block device usable as swap. Doc assumes about a 2:1 compression ratio and says an unused device costs about 0.1% of its size. Supports several algorithms and a `mem_limit`. VERIFIED. https://docs.kernel.org/admin-guide/blockdev/zram.html. No measured guest figures found.

## 3. Sharing a read-only base image's page cache

- Kata Containers (DAX): the architecture doc says Kata uses Linux DAX to "efficiently map the guest image in the host environment into the guest VM", sharing the guest image as the VM rootfs. DAX bypasses the guest page cache, "zero copy provides both time and space optimizations", and "allows the host to efficiently share pages". Hypervisor table lists QEMU with an NVDIMM memory-file backend; Cloud Hypervisor via a PMEM device and `dax` FsConfig. VERIFIED. https://raw.githubusercontent.com/kata-containers/kata-containers/main/docs/design/architecture/README.md
  - That doc gives no MiB-saved figure ("not found"), does not name virtio-pmem, and does not mention erofs.
- virtio-fs DAX window: a kata-dev reply says the window maps host pages and so costs about 0 bytes of extra physical RAM; real cost is the host buffer cache; there is no sharing between DAX windows unless they map the same host file. SECONDARY. https://lists.katacontainers.io/pipermail/kata-dev/2021-May/001930.html
- Hidden cost: struct page (ZONE_DEVICE) metadata is still allocated in the guest for DAX-mapped ranges and can offset savings for small files; per-inode DAX avoids DAX for small files. SECONDARY. https://developer.aliyun.com/article/863752
- virtio-pmem with FS-DAX pays the guest-side ZONE_DEVICE metadata cost up front, even for holes in a sparse image. SECONDARY (LWN patch note). https://lwn.net/Articles/1092485/
- QEMU docs for virtio-pmem and virtio-mem: the paths I tried returned 404. QEMU-side virtio-pmem details are not found here.
- erofs as a shared read-only rootfs: no source read. Not found.
- Windows guests: no DAX/pmem equivalent was researched. Not found.
- Takeaway: for Linux guests, a read-only erofs/ext4 base image on virtio-pmem or virtio-fs DAX gives one host page-cache copy, but only if all guests map the same host file. UNVERIFIED as a recommendation; benchmark.

## 4. microVMs

- Firecracker: target "<= 125 ms" from InstanceStart API call to guest `/sbin/init`, with serial console disabled and minimal kernel and rootfs. VMM thread overhead "<= 5 MiB" for a 1 vCPU, 128 MiB guest. VERIFIED. https://github.com/firecracker-microvm/firecracker/blob/main/SPECIFICATION.md
- Firecracker devices: "only 6 emulated devices are available: virtio-net, virtio-balloon, virtio-block, virtio-vsock, serial console" plus a minimal keyboard controller. No GPU or display. "Firecracker supports Linux host and guest operating systems" (and OSv). So no Windows guests and no graphical desktop path (a VNC-less framebuffer is not offered). VERIFIED. https://raw.githubusercontent.com/firecracker-microvm/firecracker/main/FAQ.md
- Cloud Hypervisor: runs on KVM or MSHV; supports "64-bit Linux and Windows guests" (Windows Server 2025 and Windows 11 mentioned). Hotplug of virtio-net/block/pmem/fs/vsock. README did not mention virtio-gpu or a display device, so graphics support is not found there. VERIFIED. https://raw.githubusercontent.com/cloud-hypervisor/cloud-hypervisor/main/README.md
- Cloud Hypervisor Windows guests: need a host with KVM and a UEFI Windows image with VirtIO drivers. VERIFIED. https://raw.githubusercontent.com/cloud-hypervisor/cloud-hypervisor/main/docs/windows.md
  - A commit "tests: Disable test_windows_* for mshv" says Windows on MSHV was not passing (old). SECONDARY. https://git.phyllo.me/External/cloud-hypervisor/commit/87a4f4ae324b22c99ee74930dc012c718cc445f0
- Cloud Hypervisor on a Windows host: no. MSHV means a Linux root partition with `/dev/mshv`; the doc lists no Windows host, and on MSHV it "can boot Linux guests using an IGVM file". VERIFIED. https://raw.githubusercontent.com/cloud-hypervisor/cloud-hypervisor/main/docs/mshv.md
  - I did not find any WHP backend. "Not found" is not proof of absence.
- crosvm virtio-gpu: crosvm book page `book/devices/gpu.html` returned 404. A search shows a Rutabaga/gfxstream appendix with host-build steps assuming Unix, and nothing on Windows hosts. SECONDARY. https://crosvm.dev:443/book/appendix/rutabaga_gfx.html. Not found on display/headless options or on Windows host support.
- QEMU virtio-gpu docs mention `wsi` = `surfaceless` or `headless` for rendering without a window system. SECONDARY (search snippet). https://qemu.readthedocs.io/en/master/system/devices/virtio/virtio-gpu.html

## 5. Minimal Linux GUI stacks for one headed Chromium

- cage: "A kiosk runs a single, maximized application." Run as `cage APPLICATION`. Nested in a Wayland/X11 session it opens as a window; on a TTY it uses KMS+DRM. Optional XWayland. README says nothing about headless. VERIFIED. https://raw.githubusercontent.com/cage-kiosk/cage/master/README.md
  - Cage is built on wlroots, so the wlroots headless backend can likely drive it via `WLR_BACKENDS=headless`. UNVERIFIED.
- wlroots env vars (`WLR_BACKENDS=headless`, `WLR_RENDERER=pixman`, `WLR_HEADLESS_OUTPUTS`): the docs were not retrievable (404, Anubis block). Not found as a primary read; names come from memory. UNVERIFIED. Check `docs/env_vars.md` in wlroots.
- weston: man page says the headless backend runs in memory, useful for capturing outputs or testing clients, with `--width`/`--height`. `backend=headless` in weston.ini. SECONDARY. https://man.archlinux.org/man/weston.1.en
  - kiosk-shell under headless: not found.
- grim: "Grab images from a Wayland compositor"; PNG, JPEG (optional libjpeg), PPM; `-` writes to stdout. README does not name the protocol. VERIFIED. https://raw.githubusercontent.com/emersion/grim/master/README.md. wlr-screencopy / ext-image-copy-capture dependency: UNVERIFIED.
- wtype: "xdotool type for wayland"; types text and sends key events; the compositor destroys the virtual keyboard object when wtype exits. Protocol (virtual-keyboard) not named in README. VERIFIED. https://raw.githubusercontent.com/atx/wtype/master/README.md
- ydotool: uses the kernel `uinput` framework through a daemon `ydotoold`, which needs `/dev/uinput` (usually root). Not Wayland-specific; works on X11, console, framebuffer. Since v1.0.0 the daemon is mandatory. VERIFIED. https://raw.githubusercontent.com/ReimuNotMoe/ydotool/master/README.md
- wlr-virtual-pointer for pointer input: not read. UNVERIFIED.
- Xvfb + tiny WM: not researched with a source. Anthropic's computer-use demo uses "X11 + VNC" in Docker (VERIFIED, see item 7), which is the known-working reference pattern.
- Published idle RAM numbers for cage, labwc, sway, weston, Xvfb: not found. Measure.

## 6. Chromium

- `--ozone-platform=wayland` headed: not verified from a primary source this session. UNVERIFIED.
- Headless: Chrome removed old headless (`--headless=old`) in Chrome 132; plain `--headless` means the new mode since v128; the old behaviour lives in standalone `chrome-headless-shell`. SECONDARY (search summary of developer.chrome.com). https://developer.chrome.com/blog/removing-headless-old-from-chrome
- Playwright 1.49 switched the `chrome`/`msedge` channels to new headless and ships a separate `chromium-headless-shell` build. GPU/WebGL availability and performance vary between modes. SECONDARY. https://github.com/microsoft/playwright/issues/33566
- CDP `Page.captureScreenshot`, `Input.dispatchMouseEvent`, `Input.dispatchKeyEvent`, `Page.startScreencast`: protocol JSON fetch was truncated; I did not read these definitions. UNVERIFIED. Reference: https://chromedevtools.github.io/devtools-protocol/ (screencast is marked experimental in my memory; confirm).
- `--remote-debugging-pipe`: communicates on fd 3 and 4 with NUL-delimited JSON, and only the launching process can access it (unlike a WebSocket port). SECONDARY. https://github.com/cyrus-and/chrome-remote-interface/pull/440 (via search). Playwright's pipe mode had failures in some Docker and Windows+Bun setups. SECONDARY. https://github.com/microsoft/playwright-java/issues/1871
- Idle RAM per instance: no official figure. A Lightpanda benchmark reports 382-388 MB idle PSS for headless Chrome with CDP up (vendor, SECONDARY). Blogs say 100-500 MB. Measure PSS across the whole process tree. https://computingforgeeks.com/install-lightpanda-headless-browser-linux/

## 7. Browser on the HOST instead of a VM

- Chromium Windows sandbox design: for threat-modelling "we consider the sandbox compromised (that is, running malicious code)". Mechanisms: restricted token, job object (limits process count and UI actions; CPU, memory and I/O limits are not enforced), alternate desktop (about 4 MB), lowest integrity level, Win32k lockdown (`DisallowWin32kSystemCalls`), AppContainer/LPAC. Gaps: network access from the target is possible without AppContainer; "Emulation and virtual machine solutions do not by themselves provide security." VERIFIED. https://chromium.googlesource.com/chromium/src/+/main/docs/design/sandbox.md
- Chromium security FAQ: attacks by code already running as your user, or by a compromised machine, are outside the threat model ("can do anything you can do"). A compromised GPU process on X11 is not treated as a bug because X11 does not isolate clients. VERIFIED. https://raw.githubusercontent.com/chromium/chromium/main/docs/security/faq.md
  - So a host-run browser protects the host only against a renderer compromise. It does not protect against a browser-process compromise, a kernel bug, or what the agent itself does. Extra containment must come from outside Chromium.
- Windows Sandbox: a disposable VM using the Microsoft hypervisor, with its own kernel, "supports virtual GPU", fast launch, smart memory management. Requires Pro/Enterprise/Education; not Windows Home. "Windows Sandbox currently doesn't allow multiple instances to run simultaneously." Networking is on by default. VERIFIED. https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-overview
  - Key blocker for density: single instance at a time per host.
- Linux options:
  - bubblewrap: unprivileged user namespaces, constructs sandboxes. Protection depends entirely on your arguments, so it is not a ready sandbox. Use `--new-session` or seccomp against TIOCSTI; anything mounted in can enable escalation. VERIFIED. https://raw.githubusercontent.com/containers/bubblewrap/main/README.md
  - Landlock: unprivileged self-restriction, stackable LSM; filesystem rules, and TCP bind/connect port rules from ABI 4. VERIFIED. https://docs.kernel.org/userspace-api/landlock.html
  - gVisor: "an application kernel that implements a Linux-like interface", runsc OCI runtime. The page does not mention Chromium, GUI or Windows hosts. VERIFIED (page read). https://gvisor.dev/docs/
  - seccomp and user namespaces: no dedicated source read. Note Chromium's own Linux sandbox uses user namespaces and seccomp-bpf; UNVERIFIED here.
- Vendors:
  - Browserbase: "Each browser runs in a dedicated VM", in an isolated subnet with strict firewalls; VM killed and recreated after each session; "No GPU access". VERIFIED. https://docs.browserbase.com/guides/security. Hypervisor not named.
  - Browser Use (a different company) says it runs each browser as its own Firecracker microVM. SECONDARY. https://browser-use.com/posts/firecracker-browser-infra
  - Anthropic computer-use demo: Linux desktop in Docker with X11 + VNC; "weakly separated" components; tells users to "use a dedicated virtual machine or container with minimal privileges", avoid sensitive data/logins, allowlist domains. Latest tool set mentioned is `computer_toolset_20260801`. VERIFIED. https://github.com/anthropics/claude-quickstarts/blob/main/computer-use-demo/README.md
  - Playwright Docker docs: `--ipc=host` and `--init` are recommended; for untrusted sites use a separate user and the provided seccomp profile (older README wording; current page not read). SECONDARY. https://playwright.dev/docs/docker
  - OpenAI Operator public notes: fetch returned 403. Not found.

## 8. Windows guests on KVM

- QEMU: "No Hyper-V enlightenments are enabled by default by either KVM or QEMU." Useful flags: hv-relaxed, hv-vapic (exit-less EOI), hv-time (Reference TSC clocksource, exit-less timestamps), hv-synic, hv-stimer (avoids heavy HPET/RTC fallback that can waste idle CPU), hv-tlbflush, hv-ipi; each has dependencies (for example hv-stimer needs hv-vpindex, hv-synic, hv-time). `hv-passthrough` enables all supported ones but blocks migration and is for development only. VERIFIED. https://qemu.readthedocs.io/en/master/system/i386/hyperv.html
- libvirt maps these to `<hyperv>` features (relaxed, vapic, spinlocks) and a `hypervclock` timer; libvirt made relaxed/vapic/spinlocks default for Windows os_type in 2014. SECONDARY. https://bugs.launchpad.net/nova/+bug/1400315
- KSM effectiveness for Windows guests: not found. (Windows zeroes freed pages, which helps with zero-page merging, but that is UNVERIFIED reasoning.)
- virtio drivers: the virtio-win project ships the drivers; no primary read this session beyond the viosock item below.
- virtio-mem on Windows: technology preview, see item 2.
- viosock (AF_VSOCK) on Windows: added to virtio-win in 0.1.285-1 per an Incus forum post; installed manually with `pnputil` using viosock.inf/.sys/.cat plus viosocklib and viosockwspsvc; a `vstbridge.exe -i` service is a separate step. SECONDARY. https://discuss.linuxcontainers.org/t/getting-incus-exec-working-on-windows-11-vms-built-with-incus-windows/26404 and https://virtio-win.github.io/Knowledge-Base/SSH-over-VSock
  - Red Hat tracker has an open RFE for vsock driver productization and a bug that viosock cannot be updated via Windows Update (SECONDARY). https://issues.redhat.com/browse/RHEL-109592
  - A June 2026 QEMU patch series adds `vsock-listen` on Windows (qemu-ga) and says a very recent kvm-guest-drivers-windows driver is needed. SECONDARY. https://patchew.org/QEMU/20260619180121.2982356-1-polina.vishneva@virtuozzo.com/
  - Whether the stable virtio-win ISO currently bundles viosock by default: not found.
- virtio-serial for Windows: standard in virtio-win (vioserial); no source read this session. UNVERIFIED.
- Hyper-V host side (first supported host): guest transport there is Hyper-V sockets (AF_HYPERV), not read here.

## Open items to settle with a test

1. QMP `screendump` with `-display none` and std VGA vs virtio-vga: does it return non-black PNG.
2. libvirt `virsh screenshot` with only a VNC unix-socket `<graphics>`.
3. Idle PSS of cage+Chromium (headless wlroots backend, pixman) vs Xvfb+Chromium, per guest.
4. Whether the stable virtio-win ISO includes viosock and how it installs unattended.
5. KSM and free-page-reporting yield with N identical guests, and CPU cost (kernel docs give only the knobs).
