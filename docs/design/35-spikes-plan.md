# Spikes and success criteria

Status: DRAFT for owner review (2026-10-25) · Owner questions: 12
Depends on: 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 05-model-gateway.md, 06-agent-runtime.md, 13-skimmer-bouncer.md, 14-identity-service-and-passport.md, 16-agent-vms-and-environments.md, 17-coding-agent-harness.md, 18-vault.md, 19-constellation-and-git-solar-system.md, 20-engine-integration-api.md · Used by: 09-work-life-simulation.md, 21-github-integration.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 31-local-coworker-npu.md, 33-backup-dr-and-export.md

## Purpose
A spike is a time-boxed experiment that settles one unverified claim before code depends on it. This plan merges the spike lists from the implementation plan, the two reports and the notes into 25 spikes, with duplicates such as Monaco and the GitHub test appearing once. Spikes produce findings, not product code.

## Decided inputs
- One OS-agnostic server; Windows (the NUC) is the first host; no Windows-only API outside a provider (CLAUDE.md; `Bazaar.Server.md` s3).
- Wire format OPEN until the first client-server spike; no Protobuf tooling yet (CLAUDE.md).
- Claude lane uses Anthropic API keys only (CLAUDE.md).
- Roblox Studio runs only on the human's Windows desktop (CLAUDE.md).
- xUnit v3; `Avalonia.Headless` for UI (CLAUDE.md).
- Private chat material and owner data stay out of git and cloud sessions (CLAUDE.md).

## Design

### Spike catalogue
All numeric thresholds are [PROPOSED] starting points unless marked otherwise.

| ID | Question and procedure | Pass (fail = fallback) | Time-box | Prerequisites | Runs on | Unblocks |
|---|---|---|---|---|---|---|
| S1 | PostgreSQL 18 EDB install as service on the NUC image; pgvector MSVC build; restart after reboot | Service survives reboot; `vector` loads; FTS and vector queries return expected rows (fail: Linux VM for search) | 1 d | NUC, admin shell | Owner NUC | 03, 04, 18 |
| S2 | Garnet on Windows: StackExchange.Redis for commands used; data after kill | All used commands pass; data survives restart (fail: NOTIFY only) | 1 d | Only if S3 fails (Q3) | Owner NUC | 03, 04 |
| S3 | Chat load: append plus NOTIFY and replay at burst rates, 200-message page, 10k replay; forced listener reconnect | p95 append under 50 ms; no lost events after reconnect (fail: batch or partition redesign) | 2 d | S1 | Cloud (Linux baseline); NUC (pass) | 01, 02, 04 |
| S4 | Service recovery: `sc failure`, ShutdownTimeout, kill mid-turn | Host restarts within 60 s; turn resumes from last persisted step with no duplicate message | 1 d | S1 | Owner NUC | 03 WP8, 06 WP3 |
| S5 | Orleans one-agent vs plain hosted service: 20 idle agents RSS, wake latency, Windows and Linux parity | Orleans wins criteria 1 and 4 without losing 2 or 3 (`06` criteria); else hosted service | 2 d | Orleans version and licence on NuGet | Cloud | 06 |
| S6 | Wire codec: JSON source-gen vs Protobuf behind `IWireCodec`; bytes and time; unknown and added fields; AOT warnings | JSON unless a non-.NET consumer is v1 or Android shows a JSON-specific failure (`02` rule) | 2 d | Android device for part 2 | Cloud (part 1); Android (part 2) | 02, Server doc s3, Contracts WP1 |
| S7 | Android push: primary-page read of the foreground-service timeout; `deviceidle force-idle`; FCM vs UnifiedPush/ntfy wake; Wi-Fi to mobile reconnect | Wake within 2 min in 9 of 10 Doze trials; fetch after wake restores GC view | 3 d | Android device; Firebase project (owner); ntfy host | Owner device; doc read in cloud | 02, 29, 30 |
| S8 | VM pair sizing: RAM, CPU, boot, save and restore per Windows and Linux pair; pairs in 32 GB; HvSocket, Linux vsock and SMB throughput; single nested WSL2 VM vs two-VM pair (Q4) | At least 4 pairs running with 2 GB host headroom; boot-to-ready within 120 s (fail: fewer pairs, or nested topology) | 3 d | Hyper-V on NUC; golden images (owner) | Owner NUC | 16, 17 |
| S9 | Host-side egress and mount test: default-deny at the Hyper-V switch; in-guest WSL2 cannot bypass; Passport and audit paths absent from guest | Zero bypass connections in the test matrix; canary in a mount is unreadable from guest | 1 d | S8 | Owner NUC | 15, 16 WP9 |
| S10 | Default-app handoff: file-type and terminal registration; `ms-settings:defaultapps` link; Windows Terminal console handoff | Bazaar is selectable in Settings; handoff opens the terminal; no UserChoice write (fail: deep link only) | 2 d | Windows 11 desktop | Owner Windows | 28, Windows doc s1, s4 |
| S11 | Terminal control on ConPTY (Windows) and PTY (Linux): candidate licence, maintenance, Headless behaviour | Shell, resize, colour and Ctrl-C correct on both; licence is OSI-compatible for signing (fail: own terminal widget) | 2 d | None | Cloud (PTY); Windows (ConPTY) | 28 |
| S12 | Monaco in a WebView: WebView2 (Windows); community WebView and Accelerate `NativeWebView` (Linux, Android) | Monaco loads offline from bundled assets; typing, IME, paste work; theme applied; Accelerate price quoted | 3 d | Avalonia quote (Q9); Android device | Owner Windows and Android | 27, 28, 29 |
| S13 | Android IME and fold on a real foldable at 120 Hz: caret, soft keyboard, activity recreation vs in-place resize, frame pacing | No lost keystrokes in 200-character input; GC draft survives fold; at most 2% dropped frames while scrolling | 3 d | Foldable device | Owner device | 29 |
| S14 | Graph: 5k and 10k nodes, render (Windows and Android) and server layout tick | 5k render at 60 fps, at most 10 ms draw; layout tick at most 8 ms at 10k on NUC; Android not gated until measured | 3 d | Constellation WP3 | Cloud (layout, relative); owner (render) | 19, 27 |
| S15 | Detachable windows and docking on Avalonia 12 (Dock.Avalonia) vs WinUI 3 reference | Detach and re-dock without crash; 60 fps drag on NUC-class laptop; owner signs off feel | 2 d | Owner review | Owner Windows | 27, 28 |
| S16 | SOCKS5 through `SocketsHttpHandler` on .NET 10; `https://` proxy URIs on Linux (dotnet/runtime#102441) | Request succeeds via local SOCKS5 proxy (fail: outbound proxy stays schema-only) | 0.5 d | None | Cloud | 05 (low) |
| S17 | FreeLLMAPI surface: read `server/src` for key scoping, `sk-cp-` keys, chat family lock, usage and health, prompt logging; run pinned version with non-Claude free keys | Forced fallback never reaches an excluded model (pass: policy upstream); else unpolicied upstream. Anthropic key never loaded | 2 d | Pinned version; free keys (owner) | Cloud | 05 WP13 |
| S18 | Edit-format benchmark per model (Haiku 5.5, DeepSeek V4 Flash and Pro, one free-tier model): about 40 tasks x 3 formats x 3 seeds | Per model, one format within 5 points of best with fewer tokens; no format flagged for fake success | 4 d | API keys and spend cap (Q6) | Cloud | 17 WP13, whitelist |
| S19 | Cost and cache: cache hit rate, escalation share, per-agent tokens from a two-week pilot | Measured typical Claude-lane spend within 25% of the model, or the model is rebased; hit rate recorded | 14 d elapsed | Pilot running on NUC | Owner NUC | 05, 09, 23 |
| S20 | Skimmer shadow: score every (message, agent) pair without acting, on real transcripts | At least 98% of messages later acted on would have woken the agent; at most one call per batch (fail: stays PROPOSED) | 1 wk | Transcripts stay local (Q7); scorer via gateway | Owner NUC | 13 |
| S21 | Intel NPU via OpenVINO EP (Windows ML catalogue): enumerate providers; run one small model; record tok/s and CPU fallback | Model runs on NPU with no CPU fallback at driver 32.0.100.5540 or later (fail: CPU speech path) | 2 d | Intel NPU machine | Owner hardware | 31 |
| S22 | GitHub App: (a) issue on uninstalled public repo; (b) fork then PR with installation token; (c) bot commit email; (d) token lifetime | (a) recorded, never relied on; (b) PR opened from fork (fail: org-owned direct branches, `14` C3) | 2 d | Owner creates App and repos (Q2); at most 10 writes | Cloud, after owner setup | 21, 14 C3, 18 WP5 |
| S23 | git: LibGit2Sharp vs git CLI for worktrees, merge with conflicts, two concurrent writers | LibGit2Sharp does all three without corruption; else git CLI on server (bundling git needs counsel) | 2 d | None | Cloud | 17 WP10, 18 WP3 |
| S24 | Roblox Studio MCP bridge: Studio on desktop; tool list; approved edit in a test place | Transcript recorded; edits only in test place; no headless Studio claim | 3 d | Studio on owner desktop; Creator Terms read (counsel) | Owner Windows | 20 |
| S25 | Restore drill: base backup plus WAL archive; restore to a chosen timestamp; timing | No GC log gap; measured restore against proposed RPO 1 h and RTO 1 d | 2 d | S1 | Cloud (Linux); NUC | 33, 04 |

Cloud spikes (S3 baseline, S5, S6 part 1, S11 PTY, S14 layout, S16, S17, S18, S22, S23, S25 Linux) run as Haiku agents in the cloud container on synthetic or public data. The rest need owner hardware, accounts or private data.

## Data and interfaces
Spike record, one per spike in `docs/spikes/<ID>.md`: `id`, `branch`, `runner`, `criteria` (written before the run), `environment` (OS build, driver, PostgreSQL, .NET), `runs`, `result` (pass | fail | inconclusive), `findings` (the doc section, conflict or question it feeds).

## Behaviour and edge cases
- Time-box is a hard stop. Expiry records `inconclusive`; extension needs owner approval.
- GitHub spikes: throwaway public repos, at most 10 writes, no posts to third-party projects, within the secondary limits in the stack report.
- Destructive tests (S4, S25) run on a scratch VM or copy, never on live data. Spend is capped per spike; keys are session secrets, never in git.
- A failed spike blocks only the docs it unblocks; each row names its fallback. A result that contradicts a DECIDED item goes to Conflicts.

## Testing
- Throwaway scripts under `spikes/<ID>/`, not shipped. Criteria are fixed before each run; measured results repeat three times (median and spread).
- Real models only in S17 to S19; other cloud spikes use fakes or fixtures. UI spikes use `Avalonia.Headless` where it applies; feel needs owner sign-off.
- Key cases: S6 round trip with an unknown field; S4 kill between two persisted steps; S22 both API outcomes; S9 bypass matrix; S25 gap-free restore.

## Work packages
1. Wave A, cloud, start now, in parallel: S6 part 1 (first, since it closes the codec for Contracts), S17, S23, S5, S3 baseline, S25 Linux, S16, S11 PTY, S14 layout. Then S18 (after keys and cap) and S22 (after owner setup).
2. Wave B, owner NUC, with a cloud-written runbook: S1, then S3 pass, S4 and S25 NUC; S8, then S9; S2 only if S3 fails.
3. Wave C, Windows desktop and Android device: S10, S11 ConPTY, S12, S13, S14 render, S15, S7 device, S24.
4. Wave D, owner data and hardware, after the pilot: S19, S20, S21.

S6 and S22 gate the Contracts and GitHub designs. S8 gates the Hyper-V provider. S12 gates the Monaco choice.

## Conflicts
- **VM topology.** `Bazaar.Soul.md` says two VMs "decided"; CLAUDE.md says OPEN. `16` Q13 drops the single-VM benchmark; the stack report's decision 6 keeps it (Q4).
- **Wire format.** `Bazaar.Server.md` s3 says Protobuf is DECIDED and reopened; CLAUDE.md says OPEN. S6 decides.
- **Garnet.** CLAUDE.md names Garnet for Windows pub/sub; the stack report and `03` default to NOTIFY. S2 is conditional.
- **FreeLLMAPI and the Claude lane.** Priority support for FreeLLMAPI sits beside Anthropic-keys-only. The research says Claude traffic should bypass it; S17 can only record a no-go.
- **Android dataSync cap.** The Android doc and the distribution note disagree; S7 starts from the primary page.
- **Notepad.** `Bazaar.Windows.md` s1 says Bazaar replaces Notepad. Windows 11 does not allow that silently; S10 tests the deep-link route.
- **Monaco and "no web client".** S12 proves the editor works, not that it is allowed (Q10).
- **Scale.** `Bazaar.Server.md` calls gc_id indexing sufficient with no Postgres benchmark behind it; S3 tests it.

## Questions for the owner
1. Spikes on `spike/<ID>` branches, results in `docs/spikes/<ID>.md`, nothing merged to main? Default: yes.
2. You create the GitHub App on `codex-alchemist-dev` and throwaway public repos outside that installation? Default: yes, before S22.
3. Run S2 (Garnet) only if S3 fails? Default: yes.
4. Benchmark both the nested single-VM and the two-VM topology, despite the Soul doc's "decided" text? Default: both.
5. Never load your Anthropic key into FreeLLMAPI? Default: yes.
6. Hard spend cap for S18 and the S19 pilot? Default: USD 30 for S18 (rough, unmeasured).
7. Transcripts for S20 stay on your machines, cloud gets fixtures? Default: yes.
8. Which foldable, and is its panel 120 Hz? Test both FCM and UnifiedPush? Default: both.
9. Request an Accelerate quote now, before S12? Default: yes.
10. Does an embedded Monaco in a native client count as a web client? Default: no, only if you say so in writing.
11. Accept the [PROPOSED] thresholds as starting points? Default: yes.
12. S10: accept deep-link registration instead of silent Notepad replacement? Default: yes.
