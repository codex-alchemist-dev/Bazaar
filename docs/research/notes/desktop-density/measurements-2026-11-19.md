# Desktop density: first measurements (2026-11-19)

Measured in a cloud container (Ubuntu 24.04, 4 vCPU, 16 GB, no GPU, no `/dev/kvm`). These numbers describe the software stack inside a guest, not a hypervisor. VM overhead, boot time and host-side sharing still need the VmBench run on the owner's PC (doc 59 section 10).

## Setup
- Chromium 153 (Chrome for Testing build 1243), `cage` 0.1.5 (wlroots kiosk compositor), `grim`, `wtype`, QEMU 8.2.2 (TCG, no KVM).
- Headed browser: `WLR_BACKENDS=headless WLR_RENDERER=pixman cage -- chrome --ozone-platform=wayland --disable-gpu`. No display device, no GPU, no X server.
- Headless browser: `chromium_headless_shell` driven over the DevTools protocol.
- Memory is read from `/proc/<pid>/smaps_rollup`, summed over the process tree. "Private" is `Pss_Anon`. "PSS" also counts each process's share of file-backed pages (the browser binary and libraries).

## Results

| What | Result |
|---|---|
| Headed Chromium in `cage` with no display device | Works. Real window, tabs and toolbar, 1280 x 720. |
| Compositor (`cage`) memory | 11 MB PSS |
| Headed Chromium, one tab, alone | 416 MB PSS, of which 195 MB private (13 processes) |
| Two headed Chromium instances on one kernel | about 300 MB PSS each, 195 MB private each. About 220 MB of binary and library pages are shared between them. |
| Headless Chromium, one tab | 168 MB PSS, of which 61 MB private (6 processes) |
| Screenshot through the compositor (`grim`, PNG, 1280 x 720) | 27 to 36 ms |
| Screenshot through DevTools (`Page.captureScreenshot`, PNG) | 24 to 38 ms |
| Typing through the compositor (`wtype`) | Works with a guard. Chromium drops the text of the first key of a `wtype` call when that key is "-" or a space (the keydown arrives, the character does not); strings starting with a letter or digit arrived whole. Of five test strings, a plain call and a `-M shift -m shift` prefix each lost 2; a leading `-k Shift_L` or `-P Shift_L -p Shift_L` lost none. Key chords such as Ctrl+A and Shift+Tab work as is. |
| Pointer through the compositor (`wlrctl pointer move`, `click`) | Not usable. `wlrctl` only moves relatively; a corner push followed by a move did not land on the requested point (one click hit a link about 1100 px away, another registered nothing). Needs a helper that speaks absolute motion on the virtual-pointer protocol. Until then the guest source refuses pointer input, and the browser (DevTools) and hypervisor sources carry it. |
| Pointer through the compositor (`bazaar-pointer`, absolute motion on `zwlr_virtual_pointer_manager_v1`) | Works. 15 of 15 clicks at 5 points landed in the intended 80 px cell; left, middle and right buttons and wheel direction are told apart. A click takes about 80 ms. It needs a 20 ms pause between the move and the press: without it 2 of 4 early runs lost one click. The "Chrome for Testing" infobar shifts the page by about 57 px, so tests run Chromium in kiosk mode. |
| Typing and clicking through DevTools (`Input.insertText`, `Input.dispatchMouseEvent`) | Works in headless. |
| QEMU with `-display none`: QMP `screendump` to PNG | Works, 7 ms for a 720 x 400 text screen |
| QEMU with `-display none`: QMP `input-send-event` (absolute pointer, key) | Accepted |

## What this means
1. A guest needs no display device, no GPU and no viewer window to have a working desktop that can be screenshotted and driven. A software-rendered kiosk compositor adds about 11 MB.
2. The browser, not the desktop, is the memory cost. About 195 MB per headed browser is private and cannot be shared. About 220 MB is binary and library pages, which one kernel shares between instances but separate VMs do not, unless the hypervisor deduplicates or maps a shared image.
3. A hypervisor can screenshot and send input to a VM that shows no window anywhere, with nothing running in the guest.
4. Headless Chromium with DevTools screenshots costs about a third of a headed browser's private memory.
