# Spikes and success criteria

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 10
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 05-model-gateway.md, 06-agent-runtime.md, 13-skimmer-bouncer.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 17-coding-agent-harness.md, 18-vault.md, 19-constellation-and-git-solar-system.md, 20-engine-integration-api.md, 23-observatory-and-usage.md · Used by: 09-work-life-simulation.md, 21-github-integration.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 31-local-coworker-npu.md, 33-backup-dr-and-export.md

## Purpose
A spike is a time-boxed experiment that settles one unverified claim before code depends on it. This plan merges the spike lists from the implementation plan, the two reports and the notes into 25 spikes, with duplicates such as Monaco and the GitHub test appearing once. S26 (browser profiles) was added by DECISIONS-LOG round 7 (C22). Spikes produce findings, not product code.

## Decided inputs
- One OS-agnostic server; Windows (the NUC) is the first host; no Windows-only API outside a provider (CLAUDE.md; `Bazaar.Server.md` s3).
- Every agent gets a Windows VM and a Linux VM; S8 sizes the pair and does not compare topologies (Soul 2026-10-24; DECISIONS-LOG round 1, C03).
- Wire format OPEN until S6; no Protobuf tooling yet (CLAUDE.md).
- Claude lane: the owner's Anthropic API key and FreeLLMAPI free tokens, by availability; usage records label the source; subscription (Pro/Max) logins stay off every route (CLAUDE.md, 2026-10-25; DECISIONS-LOG round 4, C14).
- Pub/sub: Postgres NOTIFY for GC fan-out; Valkey (Linux) or Garnet (Windows) for cache and presence (DECISIONS-LOG round 1, C07).
- Git: LibGit2Sharp stays decided unless S23 fails, behind `IGitProvider` (DECISIONS-LOG round 3, C12).
- Fork-and-PR is built as decided; S22 tests it (DECISIONS-LOG round 3, C13).
- Clients: Avalonia is conditional on the Monaco gate S12; the public build is OSI-only (DECISIONS-LOG rounds 4 and 5, C23 and C26).
- Terminals: agent terminals run in VMs and are streamed by the server; Bazaar.Windows has a built-in local terminal (DECISIONS-LOG round 4, C31).
- Client-side exceptions: on-device inference, the local terminal, the Roblox Studio relay, and syncing the viewed branch; layout stays on the server (DECISIONS-LOG round 4, C17).
- Roblox Studio runs only on the human's Windows desktop (CLAUDE.md).
- Fold: the server sends all posture variants; the client switches locally (DECISIONS-LOG round 5, C27). Push: bare wake only (round 5, C41).
- Browser: full Chromium with an anonymous profile and Passport-backed account profiles (DECISIONS-LOG round 7, C22).
- xUnit v3; `Avalonia.Headless` for UI; private chat material and owner data stay out of git and cloud sessions (CLAUDE.md).

## Design
### Spike catalogue
All numeric thresholds are [PROPOSED] starting points unless marked otherwise.

| ID | Question and procedure | Pass (fail = fallback) | Time-box | Prerequisites | Runs on | Unblocks |
|---|---|---|---|---|---|---|
| S1 | PostgreSQL 18 EDB install as service on the NUC image; pgvector MSVC build (one `halfvec` profile, C39); restart after reboot | Service survives reboot; `vector` loads; FTS and vector queries return expected rows (fail: Linux VM for search) | 1 d | NUC, admin shell | Owner NUC | 03, 04, 18 |
| S2 | Garnet on Windows: StackExchange.Redis for commands used; data after kill. Runs regardless of S3 (C07) | All used commands pass; data survives restart (fail: presence and cache need another Windows provider, Q9; GC fan-out is unaffected) | 1 d | NUC | Owner NUC | 03, 11, 23 |
| S3 | Chat load on the `gc_event`/`gc_message`/`gc_message_version` model (C09) with hash partitions by `gc_id` (C10): append plus NOTIFY and replay at burst rates, 200-message page, 10k replay; forced listener reconnect | p95 append under 50 ms; no lost events after reconnect; `gc_id` plus timestamp indexing measured, not assumed (fail: batch or partition-count redesign) | 2 d | S1 | Cloud (Linux baseline); NUC (pass) | 01, 02, 04 |
| S4 | Service recovery: `sc failure`, ShutdownTimeout, kill mid-turn | Host restarts within 60 s; turn resumes from last persisted step (06 `turn_step`) with no duplicate message | 1 d | S1 | Owner NUC | 03 WP8, 06 WP3 |
| S5 | Orleans one-agent vs plain hosted service: 20 idle agents RSS, wake latency, Windows and Linux parity | Orleans wins criteria 1 and 4 without losing 2 or 3 (`06` criteria); else hosted service | 2 d | Orleans version and licence on NuGet | Cloud | 06 |
| S6 | Wire codec: JSON source-gen vs Protobuf behind `IWireCodec`; bytes and time; unknown and added fields; AOT warnings | JSON unless a non-.NET consumer is v1 or Android shows a JSON-specific failure (`02` rule) | 2 d | Android device for part 2 | Cloud (part 1); Android (part 2) | 02, Server doc s3, Contracts WP1 |
| S7 | Android push: `deviceidle force-idle` and Doze; FCM vs UnifiedPush/ntfy wake (bare wake, C41); Wi-Fi to mobile reconnect; GC view fetched over the authenticated connection after wake | Wake within 2 min in 9 of 10 Doze trials; fetch after wake restores GC view | 3 d | Android device; Firebase project (owner); ntfy host | Owner device | 02, 29, 30 |
| S8 | VM pair sizing only (C03): RAM, CPU, boot, save and restore per Windows and Linux VM; pairs in 32 GB; HvSocket, Linux vsock and SMB throughput | At least 4 pairs running with 2 GB host headroom; boot-to-ready within 120 s (fail: fewer pairs per host; topology unchanged) | 3 d | Hyper-V on NUC; golden images (owner) | Owner NUC | 16, 17 |
| S9 | Host-side egress and mount test: default-deny at the Hyper-V switch for both VMs; guest processes cannot bypass; audit and Passport-store paths absent from guest; per-agent folder (metadata plus session-cookie profile, C02) detached on hard taint | Zero bypass connections in the test matrix; mount unreadable after detach; no audit or store path visible from guest | 1 d | S8 | Owner NUC | 15, 16 WP9 |
| S10 | Default-app handoff: file-type and terminal registration; `ms-settings:defaultapps` link; handoff to the built-in Bazaar.Windows terminal (C31) | Bazaar is selectable in Settings; handoff opens the built-in terminal; no UserChoice write (fail: deep link only) | 2 d | Windows 11 desktop | Owner Windows | 28, Windows doc s1, s4 |
| S11 | Local terminal control on ConPTY (Windows) and PTY (Linux client). Agent VM terminals are server-streamed and not tested here (C31) | Shell, resize, colour and Ctrl-C correct on both; licence OSI-compatible for the OSI-only build (C23) (fail: own terminal widget) | 2 d | None | Cloud (PTY); Windows (ConPTY) | 28, 30 |
| S12 | Monaco in a WebView, free routes only (C23, C26): WebView2 on Windows; WebKitGTK on Linux. No Android Monaco (unsupported on mobile) | Monaco loads offline from bundled assets; typing, IME, paste work; theme applied; HWND fallback recorded if composition fails (fail: AvaloniaEdit native editor, and the Avalonia decision is reopened) | 3 d | Windows desktop; Linux desktop | Owner Windows; Linux | 27, 28, 30 |
| S13 | Android IME and fold on a real foldable at 120 Hz: caret, soft keyboard, activity recreation vs in-place resize, frame pacing; fold switch is local (C27) | No lost keystrokes in 200-character input; GC draft survives fold; fold switch needs no network; at most 2% dropped frames while scrolling | 3 d | Foldable device | Owner device | 29 |
| S14 | Graph: 5k and 10k nodes, render (Windows and Android) and server layout tick (layout stays server-side, C17) | 5k render at 60 fps, at most 10 ms draw; layout tick at most 8 ms at 10k on NUC; Android not gated until measured | 3 d | Constellation WP3 | Cloud (layout, relative); owner (render) | 19, 27 |
| S15 | Detachable windows and docking on Avalonia 12 (Dock.Avalonia) vs WinUI 3 reference | Detach and re-dock without crash; 60 fps drag on NUC-class laptop; owner signs off feel | 2 d | Owner review | Owner Windows | 27, 28 |
| S16 | SOCKS5 through `SocketsHttpHandler` on .NET 10; `https://` proxy URIs on Linux (dotnet/runtime#102441) | Request succeeds via local SOCKS5 proxy (fail: outbound proxy stays schema-only) | 0.5 d | None | Cloud | 05 (low) |
| S17 | FreeLLMAPI surface: read `server/src` for key scoping, `sk-cp-` keys, chat family lock, usage and health, prompt logging; run pinned version with non-Claude free keys; check that no subscription login is reachable through the Claude route | Forced fallback never reaches an excluded model (pass: policy upstream); else unpolicied upstream. Anthropic key not loaded into FreeLLMAPI (default, Q3) | 2 d | Pinned version; free keys (owner) | Cloud | 05 WP13 |
| S18 | Edit-format benchmark per model (Haiku 5.5, DeepSeek V4 Flash and Pro, one free-tier model): about 40 tasks x 3 formats x 3 seeds | Per model, one format within 5 points of best with fewer tokens; no format flagged for fake success | 4 d | API keys and spend cap (Q4) | Cloud | 17 WP13, whitelist |
| S19 | Cost and cache: hit rate, escalation share, per-agent tokens from a two-week pilot; spend split by source (Anthropic key vs FreeLLMAPI) and cache writes at 5 min and 1 h from `gw_usage` (C14, C33) | Measured typical Claude-lane spend within 25% of the model, or the model is rebased; hit rate and per-source split recorded | 14 d elapsed | Pilot running on NUC | Owner NUC | 05, 09, 23 |
| S20 | Skimmer shadow: score every (message, agent) pair without acting, on real transcripts, labelled per 13's gates | At least 98% of messages later acted on would have woken the agent; at most one call per batch (fail: stays PROPOSED; the scored wake, C08, stays off) | 1 wk | Transcripts stay local (Q5); scorer via gateway | Owner NUC | 13 |
| S21 | Intel NPU via OpenVINO EP (Windows ML catalogue): enumerate providers; run one small model; record tok/s and CPU fallback | Model runs on NPU with no CPU fallback at driver 32.0.100.5540 or later (fail: CPU speech path) | 2 d | Intel NPU machine | Owner hardware | 31 |
| S22 | GitHub App: (a) issue on uninstalled public repo; (b) fork then PR with installation token; (c) bot commit email; (d) token lifetime | (a) recorded, never relied on; (b) PR opened from fork (fail: PR flow degrades to installed repos only, C13) | 2 d | Owner creates App and repos (Q2); at most 10 writes | Cloud, after owner setup | 21, 14 C3, 18 WP5 |
| S23 | git: LibGit2Sharp vs git CLI for worktrees, merge with conflicts, two concurrent writers, behind `IGitProvider` (C12) | LibGit2Sharp does all three without corruption; else the git CLI becomes an alternative `IGitProvider` (bundling git needs counsel) | 2 d | None | Cloud | 17 WP10, 18 WP3, 21 |
| S24 | Roblox Studio MCP bridge: Studio on desktop; tool list; approved edit in a test place; relay runs client-side as ratified (C17) | Transcript recorded; edits only in test place; no headless Studio claim | 3 d | Studio on owner desktop; Creator Terms read (counsel) | Owner Windows | 20 |
| S25 | Restore drill: base backup plus WAL archive; restore to a chosen timestamp; sequence floor and recovery entry per 33 (PROPOSED, C10); timing | No GC log gap after the recovery entry; measured restore against proposed RPO 1 h and RTO 1 d | 2 d | S1 | Cloud (Linux); NUC | 33, 04 |
| S26 | Browser profiles in the VM pair (C22): full Chromium headed and headless; anonymous profile holds no credentials; account profile uses the per-agent mount and detaches on hard taint | Both modes run; no cookie leak across profiles; account profile unusable after detach (fail: a third browser VM, only if this shows it is needed) | 2 d | S8, S9 | Owner NUC | 14, 15, 16, 17 |

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
- Key cases: S6 round trip with an unknown field; S4 kill between two persisted steps; S22 both API outcomes; S9 bypass matrix and cookie-mount detach; S25 gap-free restore; S26 profile isolation.

## Work packages
1. Wave A, cloud, start now, in parallel: S6 part 1 (first, since it closes the codec for Contracts), S17, S23, S5, S3 baseline, S25 Linux, S16, S11 PTY, S14 layout. Then S18 (after keys and cap) and S22 (after owner setup).
2. Wave B, owner NUC, with a cloud-written runbook: S1, then S2, S3 pass, S4 and S25 NUC; S8, then S9 and S26.
3. Wave C, Windows desktop, Linux desktop and Android device: S10, S11 ConPTY, S12 (WebView2 and WebKitGTK), S13, S14 render, S15, S7 device, S24.
4. Wave D, owner data and hardware, after the pilot: S19, S20, S21.

Gates: S6 gates the Contracts design; S8 gates the Hyper-V provider; S12 is a gate for the Avalonia decision as a whole (C26). S22 informs the GitHub PR flow, which is built as decided (C13), so it is run before that flow ships but does not block its design.

## Conflicts
- **Wire format.** `Bazaar.Server.md` s3 still says Protobuf is DECIDED in the table header; CLAUDE.md and the reopened text say OPEN. S6 decides; the Server.md row must be fixed.
- **VM topology text.** CLAUDE.md still says "OPEN: benchmark nested WSL2 vs two VMs", and `Bazaar.Soul.md` line 54 says "decided". C03 resolved it as the two-VM pair; CLAUDE.md needs the update.
- **Pub/sub text.** CLAUDE.md and `Bazaar.Server.md` s3 still name Garnet as the Windows cache and pub/sub provider without the "candidate" label. C07 resolved NOTIFY for GC fan-out; S2 gates presence and cache.
- **Git text.** `Bazaar.Server.md` s2 says "not shelling out to the `git` CLI" as DECIDED. C12 keeps LibGit2Sharp behind `IGitProvider`; the wording needs the same change.
- **Notepad.** `Bazaar.Windows.md` line 12 still says Bazaar replaces Notepad. S10 tests the deep-link route; Windows 11 does not allow a silent default.
- **Android keep-alive.** `30` still plans a `dataSync` foreground service. C41 says no keep-alive service, so S7 no longer tests the foreground-service timeout; doc 30 must drop the keep-alive.
- **Scale claim.** `Bazaar.Server.md` s3 calls `gc_id` indexing sufficient; this stays unverified until S3 runs on the hash-partitioned layout (C10).
- **Passport mount.** C02 made session cookies mountable. `Bazaar.Soul.md` lines 12 and 28 still describe metadata-only "one scoped folder"; S9 and S26 test the decided version.
- **Risks accepted, not conflicts.** Fork-and-PR may be refused by GitHub (C13). FreeLLMAPI's "Claude Code tiers" may involve subscription logins; S17 checks, and subscription logins stay off every route (CLAUDE.md).

## Questions for the owner
1. Spikes on `spike/<ID>` branches, results in `docs/spikes/<ID>.md`, nothing merged to main? Default: yes.
2. You create the GitHub App on `codex-alchemist-dev` and throwaway public repos outside that installation, before S22? Default: yes.
3. Never load your Anthropic key into FreeLLMAPI? C14 decided both sources but does not say whether the key enters FreeLLMAPI. Default: no; the key goes direct to Anthropic and FreeLLMAPI uses its own pool.
4. Hard spend cap for S18 and the S19 pilot? Default: USD 30 for S18 (rough, unmeasured); S19 cap set before the pilot.
5. Transcripts for S20 stay on your machines, cloud gets fixtures? Default: yes.
6. Which foldable, and is its panel 120 Hz? Test both FCM and UnifiedPush? Default: both.
7. Accept the [PROPOSED] thresholds as starting points? Default: yes.
8. S10: accept deep-link registration instead of silent Notepad replacement? Default: yes.
9. If S2 (Garnet) fails, which Windows provider serves presence and cache? Default: presence and cache wait for a chosen provider; GC fan-out is unaffected (NOTIFY).
10. Accept S26 (browser profiles, from C22) as part of this plan? Default: yes.

ANSWERED: Run S2 only if S3 fails? S2 runs regardless and gates presence and cache work (DECISIONS-LOG round 1, C07).
ANSWERED: Benchmark nested and two-VM topologies? The two-VM pair is decided; S8 is sizing only (DECISIONS-LOG round 1, C03).
ANSWERED: Request an Accelerate quote before S12? No paid Avalonia tier in the public build, so no quote (DECISIONS-LOG round 4, C23).
ANSWERED: Does embedded Monaco in a native client count as a web client? No; native clients may embed Monaco (CLAUDE.md; Server.md §1; DECISIONS-LOG round 5, C26).
