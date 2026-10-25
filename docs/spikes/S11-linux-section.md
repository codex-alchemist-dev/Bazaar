# Spike S11: Linux half (PTY) — section for docs/spikes/S11.md

Branch: `spike/S11`. Design source: `docs/design/35-spikes-plan.md` (row S11). Harness: `spikes/S11/Spike.S11.Pty/`. Raw output: `spikes/S11/results/linux-run{1,2,3}.txt`. The Windows (ConPTY) half is in PR #47 (`docs/spikes/S11.md`); this section does not repeat it, except as a labelled reference row in the Runs table.

## Question

Can a .NET program control a local shell through a pseudo-terminal correctly (shell echo, resize, colour pass-through, Ctrl-C, clean shutdown), using a licence that is OSI-compatible for the OSI-only build? This section answers it for Linux only.

## Pass criteria (fixed before the first run)

From `docs/design/35-spikes-plan.md`, row S11:

> Shell, resize, colour and Ctrl-C correct on both; licence OSI-compatible for the OSI-only build (C23) (fail: own terminal widget)

Time-box: 2 d.

## Method

**Harness.** `spikes/S11/Spike.S11.Pty` (net11.0, Linux). It mirrors the Windows harness's check names and output format (`CHECK|<shell>|SHELL_ECHO|RESIZE|COLOUR|CTRL_C|SHUTDOWN`, `TIMING`, `SUMMARY`).

- The pty is created with `posix_openpt`, `grantpt`, `unlockpt` and `ptsname_r`. The window size is set with `ioctl(TIOCSWINSZ)` on the master.
- The shell is started with `posix_spawn` and `POSIX_SPAWN_SETSID`, with signal defaults and an empty signal mask. The child opens the slave (without `O_NOCTTY`) as its stdin, which makes the slave its controlling terminal, then `dup2`s it to stdout and stderr. This avoids a custom `fork` in managed code.
- Output is drained by a polling reader thread. The reader is stopped before the master is closed, which hangs up the slave and sends SIGHUP to the session.
- Process checks read `/proc/<pid>/stat` and count live processes whose session id equals the shell's pid.
- Shells: `/usr/bin/bash --noprofile --norc -i` (bash 5.2) and `/bin/sh -i` (dash). zsh is not installed in the container and was not tested.
- Checks: marker echo (`echo S11_RESULT_$((6*7))` must produce `S11_RESULT_42` in output, not in the typed echo); resize 80x25 to 100x30 and 132x40, read back with `stty size`; colour SGR 31, 38;5;196 and 38;2;255;0;0 through `printf`; Ctrl-C (0x03 written to the master) stopping a foreground `sleep 30`, with no sleep left and the shell still running a command; shutdown (close master) ending the shell within 5 s with no process left in the session.
- Each shell is spawned fresh for every run. Timings are one sample per shell per run.

**Environment, Linux half (this container).** Ubuntu 24.04.5 LTS, kernel 6.18.44-fc-v153, x86_64, 4 logical CPUs, 15 GiB RAM, glibc 2.39. .NET runtime 11.0.0-rc.2.26504.105 (SDK 11.0.100-rc.2). Shells: bash 5.2, dash (`/bin/sh`).

**Environment, Windows half (reference, from the PC result files).** Microsoft Windows NT 10.0.26300.0, x64, 24 logical CPUs, .NET runtime 10.0.13 (not 11, as the spike was built for net10.0). RAM is not recorded in the result files.

**Build note.** The `spike/S11` branch's `global.json` pins SDK 10.0.402, which is not installed in this container; `origin/main` pins 11.0.100-rc.2. The harness was built from a directory outside the repo so that SDK resolution used the installed 11.0 SDK. `global.json` was not changed.

## Runs

Three runs, one sample of each timing per shell per run. Timings are in milliseconds.

| Run | Date (UTC) | Measure | Linux bash | Linux sh | Windows cmd (ref, PC) | Windows pwsh (ref, PC) |
|---|---|---|---|---|---|---|
| 1 | 2026-10-25 06:35:43 | first output after spawn | 3.2 | 1.3 | 13.8 | 12.8 |
| 1 | 2026-10-25 06:35:43 | prompt seen after spawn returned | 54.5 | 50.3 | 58.4 | 301.7 |
| 2 | 2026-10-25 06:35:47 | first output after spawn | 2.5 | 1.4 | 13.5 | 13.8 |
| 2 | 2026-10-25 06:35:47 | prompt seen after spawn returned | 53.9 | 50.2 | 54.4 | 305.3 |
| 3 | 2026-10-25 06:35:52 | first output after spawn | 3.3 | 1.8 | 13.8 | 13.1 |
| 3 | 2026-10-25 06:35:52 | prompt seen after spawn returned | 53.9 | 50.2 | 51.6 | 304.4 |

Linux checks: 10 of 10 checks passed in each of the three runs (5 checks × 2 shells). Exit code 0 in all three runs.

Median (Linux, n = 3): first output bash 3.2 ms, sh 1.4 ms; prompt bash 53.9 ms, sh 50.2 ms.
Spread (Linux, min to max): first output bash 2.5 to 3.3 ms, sh 1.3 to 1.8 ms; prompt bash 53.9 to 54.5 ms, sh 50.2 to 50.3 ms.

Windows reference (n = 3, from PC result files, not measured here): first output cmd median 13.8 ms (13.5 to 13.8), pwsh median 13.1 ms (12.8 to 13.8); prompt cmd median 54.4 ms (51.6 to 58.4), pwsh median 304.4 ms (301.7 to 305.3). The Windows and Linux shells are different programs, so the two sets of timings are not a like-for-like comparison.

Trial run before the three official runs (not counted): all 10 checks passed.

## Result

**Linux half: pass (for the PTY-control part of the criterion).** On bash 5.2 and dash, all checks passed in all three runs:

- **Shell:** marker echo appeared in output on both shells (`S11_RESULT_42`).
- **Resize:** `stty size` reported the requested size after each change (`want 100x30 got 100x30; want 132x40 got 132x40`, cols x rows).
- **Colour:** SGR 31, 38;5;196 and 38;2;255;0;0 arrived unmangled on both shells.
- **Ctrl-C:** a foreground `sleep 30` was running, 0x03 stopped it, no sleep was left in the session, and the shell ran a follow-up command.
- **Shutdown:** closing the master ended the shell within 5 s with no process left in its session. The raw wait status was 1, meaning the shell was terminated by SIGHUP. The harness did not have to kill it.

Ctrl-C working implies the slave was the controlling terminal of the new session. This is the evidence that the `POSIX_SPAWN_SETSID` plus open-slave approach works.

Not done: zsh (not installed). No check of the Linux desktop terminal widget. This is a headless pty test, not a rendering test.

**Licence (Linux half): OSI-compatible for this half.** The harness uses only its own P/Invoke declarations into the system C library (glibc `libc`). It contains no third-party code and adds no library dependency. glibc is supplied by the operating system and is not redistributed with Bazaar.

**The overall criterion's fail clause ("own terminal widget") is not answered by this spike.** This spike did not check the Avalonia terminal widget, its licence, or whether Bazaar needs its own widget. Anyone relying on this result for the widget decision needs a separate check.

## Consequence

For the DECISIONS-LOG (the main session writes it; this section does not edit that file):

- Local PTY control on Linux is viable with the platform's own libc P/Invoke (`posix_openpt`, `posix_spawn` with `POSIX_SPAWN_SETSID`, `ioctl(TIOCSWINSZ)`), with no third-party terminal library needed for the PTY layer. This is evidence for the Linux client's local terminal and for the "no own terminal widget" branch of the criterion, but it does not decide the widget.
- The widget/licence question stays open until someone checks the Avalonia terminal widget's licence against the OSI-only build (C23) and decides whether an own widget is needed.
- Windows (ConPTY) is recorded in PR #47; this section only adds the Linux half.
- The `spike/S11` branch's `global.json` (SDK 10.0.402) does not match `origin/main` (11.0.100-rc.2). This was not changed here and should be resolved on the branch separately.
