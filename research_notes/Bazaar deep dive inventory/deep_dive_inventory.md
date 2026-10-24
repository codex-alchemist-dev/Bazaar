# Bazaar (Codex Alchemist) Deep Dive Inventory

## Scope and method

The request asked for "deep dives" (named deep-dive topics or research threads). The sources contain no list of deep dives. The literal term "deep dive" appears only as a document type: every project Vault gets "Deep Dive docs per system/API" (S5 Constellation §3, DECIDED; also S2 turn 28 and S3). So this inventory covers named research and investigation threads, meaning technical or product questions that were researched, spiked, or explicitly left as open. Named system proposals are listed separately in Appendix A, because they are design ideas rather than research threads.

Status is the status at the latest mention, using five labels:
- **Completed**: answered and decided, or researched to a conclusion.
- **In progress**: actively being worked, or reopened and not closed.
- **Proposed**: suggested or recommended, not confirmed or logged.
- **Abandoned**: explicitly dropped, deprioritized, or superseded.
- **Unclear**: conflicting, unanswered, or never resolved in the sources.

Source tags:
- **S1**: `bdf692f3-Secret_project_team_assembly-2026-10-25.md`, a 2,786-line transcript. Its turns are dated 2026-08-06 to 2026-08-07 UTC, despite the 10-25 file name. Its latest turn is 2026-08-07 13:07 UTC, so it is the most recent source. Line numbers refer to this file.
- **S2**: `a4bc545f-deepseek-conversation.json`, 33 question/answer turns, undated. Turn numbers are 0-indexed.
- **S3**: `c9f71868-chatgpt-conversation.md`, undated. Headings are cited by name.
- **S4**: `7092de89-gemini-conversation-2026-08-01-01-30-44.md`, dated 2026-08-01. Line numbers refer to this file.
- **S5**: `bed41a95-files.zip`, extracted to `/tmp/claude-0/-home-user-Bazaar/c63e21e3-9139-50bc-9f48-e82954f68a61/scratchpad/files-zip/`. It holds seven status-tagged docs: Bazaar.md, Bazaar.Server.md, Bazaar.Windows.md, Bazaar.Android.md, Bazaar.Linux.md, Bazaar.Soul.md, Bazaar.Constellation.md. It is undated, and it predates the S1 Aug 7 turns. Where S5 and S1 disagree, S1 is the latest word, but S1's Aug 7 recommendations were not confirmed by the human or logged.

Coverage caveat: S1 was read through targeted keyword searches and line ranges, not line by line. S2 was read by parsing the JSON and searching its text. S3 and S4 were read through headings and keyword searches. The threads below are therefore a complete list only for the terms searched. Threads with only a passing mention may be missing.

---

## Key findings

1. **Many threads are settled or superseded.** Search, the primary database, cache and queue, and GitHub identity are decided. The client UI framework is decided but has an unrun validation spike. Server transport was reopened on Aug 7.
2. **The live research is the agent VM architecture and the backend language.** These are the threads from the newest S1 turns (Aug 6 to Aug 7). Several recommendations there are not yet logged or ratified. The latest Hyper-V and two-VM recommendations are the assistant's, and the human has not explicitly confirmed them.
3. **S5 is stale on the VM architecture and backend language.** S5 still lists the backend language as UNDECIDED and the per-agent Windows Server Core plus nested WSL2 design as DECIDED. S1 supersedes both.
4. **Several threads have unresolved validation spikes.** These include the Avalonia hands-on spike, the foldable-layout spike, the Cloud Hypervisor Windows test, the GitHub App issue-creation test, and the RTK and Caveman compression test.

---

## Part 1: Research threads (deduplicated, latest status)

### A. Server, VM and infrastructure

| # | Thread | One-line description | Sources | Status | Latest mention and outcome |
|---|---|---|---|---|---|
| T01 | Bazaar.Server backend language and platform | Compare Bun, Go, Elixir/Phoenix and .NET for the server. S2's Node.js plus Fastify recap is an earlier default that was never ratified. | S1 l.2362–2450; S5 Server §4; S2 (recap, per S5 note) | **In progress** | Aug 7 (S1 l.2362–2450): the assistant re-scored the options against the new .NET client stack and Windows VM needs, and called .NET the frontrunner. Elixir's supervision-tree advantage was acknowledged. Not ratified. The assistant proposed a Council vote. S5 still says UNDECIDED. |
| T02 | Agent VM platform and hypervisor | Choose what runs each agent's VM: Firecracker, Cloud Hypervisor, QEMU/KVM, or Hyper-V, and which guest OS. | S1 l.1089–1091, l.1333–1400, l.2449–2528; S5 Soul §2 | **In progress** | Aug 6 to 7: the Linux-by-default split was walked back in favour of Windows-default Hyper-V. Firecracker was ruled out for Windows guests (Linux-only by design). Cloud Hypervisor was named as the closest option for Windows guests but is untested. Aug 7 (l.2528): Francis set the scope to Windows-only for now (`Bazaar.Server`, Linux host descoped). Hyper-V isolation, Cloud Hypervisor, and the QEMU/KVM host path are not closed. |
| T03 | Agent environment topology | Choose between one Windows Server Core VM with nested WSL2 per agent, and two separate VMs per agent (one Windows, one Linux). | S5 Soul §2 (DECIDED nested design); S1 l.2568–2685 | **Proposed** | Aug 7 (S1 l.2616): Francis proposed two VMs per agent to avoid nesting. The assistant agreed it was the better call and asked to log it. Not logged. This supersedes the S5 design. |
| T04 | Hyper-V isolation mode for Windows containers | Test whether Hyper-V isolated containers save resources compared with full VMs, and how they interact with differencing disks. | S1 l.2568–2615 | **Abandoned** (deprioritized) | Aug 7: the assistant said isolation mode has a larger storage and memory footprint than process isolation, so the savings do not justify the complexity. It recommended logging it as investigated and deprioritized, with "Hyper-V-isolated containers" as a future investigation. The footprint claim cites "Microsoft's documentation" with no link. Not confirmed by Francis. |
| T05 | Shared files between one agent's Windows and Linux VMs | Share live files between the two VMs of one agent, avoiding duplicated work, for example a "shared D-drive". | S1 l.2616–2685 | **Proposed** | Aug 7: a shared read-write virtual disk was rejected because two OSes writing one disk risks corruption. A host-mediated SMB share (Windows SMB, Linux cifs or Samba) was recommended. The assistant asked to log it. Not logged. |
| T06 | Host-to-guest command passthrough | Route tool calls and terminal commands from the agent harness into both the Windows PowerShell and the WSL2 shell in each VM. | S1 l.2449–2528 | **Unclear** | Aug 7: HvSocket was called the architecturally sound guest-agent channel. PowerShell Direct and `wsl.exe` were also discussed. No decision. The human's question about the guest-controller process was left unanswered. |
| T07 | RAM and CPU capacity planning | Size the host for 6–8 agents with two VMs each, on a 32 GB NUC versus a 96 GB main PC. | S1 l.2686–2786 | **In progress** | Estimates: Windows agent VM 3–4 GB, Linux VM 2–3 GB, so one agent pair is about 6 GB. All 6–8 agents running at once is about 36–56 GB for VMs alone. Two active agents fit the NUC, at about 20–24 GB total. Francis asked for the numbers, and then the NUC versus main-PC question. The assistant asked whether the NUC RAM is soldered or SO-DIMM. Unanswered. Main PC proposed as dev and staging, not production. |
| T08 | Windows Server licensing and evaluation | Work out Standard versus Datacenter per-VM rights, the 180-day evaluation, and CAL requirements for agent-only hosts. | S5 Soul §2; S1 l.1497–1505, l.1531 | **In progress** | Standard grants 2 VMs per licensed host. Datacenter grants unlimited VMs, and is likely the right tier once the agent roster is real. The 180-day evaluation is legitimate for early development only. CAL requirements for agent-only use are unresolved and need a licensing consult. S5 does not carry forward the two-VM change, which means 12–16 Windows VMs and probably Datacenter. |
| T09 | Server-to-client transport | Choose between raw WebSocket with Protobuf, Socket.io, WebTransport, SignalR, and a shared C# contracts project. | S5 Server §3; S1 l.1155–1160, l.2398–2440; S1 l.745 | **In progress** (reopened) | S5 says WebSocket plus Protobuf is DECIDED, with WebTransport to revisit for v1.0. On Aug 7 the assistant suggested that a C# server plus C# clients might drop Protobuf for a shared `Bazaar.Contracts` project, or use SignalR. Not decided. |
| T10 | Primary database and vector store | Decide between PostgreSQL with pgvector and alternatives such as SQLite, Chroma, LanceDB and Qdrant. | S5 Server §3; S1 l.1093; S2 turns 7–8; S1 l.745 | **Completed** | PostgreSQL plus pgvector is DECIDED. Scale for Discord-style GC chat is confirmed as RESEARCHED: standard indexing on `gc_id` plus timestamp is enough. Chroma, LanceDB and Qdrant are superseded. |
| T11 | Cache and queue or pub-sub | Choose the cache, event fan-out and scheduler layer, with Valkey, Redis, NATS and MQTT as candidates. | S5 Server §3; S1 l.1105–1125; S2 turns 7–8 (NATS/MQTT); S1 l.745 (Redis Pub/Sub, NATS) | **Completed** | Valkey (BSD-3) is DECIDED over Redis, because Redis's 2024 relicense to SSPL and RSAL, followed by AGPLv3 in 2025, conflicts with open-sourcing. NATS and MQTT are dropped. |
| T12 | Vault and document search engine | Full-text and hybrid search over thousands of cross-linked docs: Postgres FTS, Meilisearch, Typesense, Quickwit, or ParadeDB `pg_search`. | S1 l.1093, l.1137, l.1166–1240, l.1305+; S5 Constellation §3; S5 Server §3 | **Completed** | ParadeDB `pg_search` (BM25) plus pgvector is DECIDED. It replaced the earlier Meilisearch and Typesense proposal. Open item: `pg_search` is AGPL, which needs a real legal check before open-sourcing. |
| T13 | OmniRoute gateway evaluation | Confirm that OmniRoute is legitimate, check bring-your-own-key support, provider separation and per-agent model routing. | S2 turns 0–4; S4 l.39–86; S1 l.368–378; S5 Bazaar §3, Server §3 | **Completed** | OmniRoute is DECIDED as infrastructure. Its token-preservation claims are self-reported, not independently benchmarked (S1 l.378). Per-agent model whitelists are DECIDED. |
| T14 | Free-tier supply limits (NVIDIA NIM for DeepSeek) | Establish the real rate limits behind "practically infinite" DeepSeek supply. | S4 l.307; S5 Bazaar §3, Server §3 | **Completed** | DECIDED with a caveat: roughly 40 RPM and 1,000 requests per day per free endpoint, before fallback. Not literally unlimited. |
| T15 | Token compression verification (RTK and Caveman) | Verify on real code and diffs that the compression leaves source files intact. | S1 l.368–378; S5 Server §3 | **Proposed** | S5 action item: test empirically before trusting it on source files. No test result recorded. |
| T16 | GitHub identity and presence | Decide between machine accounts and GitHub Apps for per-agent identities, and how they fork and open PRs. | S1 l.624–660, l.857–935; S5 Server §2 | **Completed** (one sub-claim untested) | Machine accounts are a dead end under GitHub's ToS, so the design uses GitHub Apps (`claude-bazaar[bot]` and so on) installed on the `codex-alchemist-dev` org. Git operations use LibGit2Sharp. Open action item: test whether an App token can open issues on a public repo it is not installed on. The only source is a community report. |
| T17 | CI and CD engine | Choose between a self-built minimal task runner and an existing tool such as Woodpecker. | S5 Server Open Q3; S1 l.1238–1300 (explainer only) | **Proposed** | Concept agreed (build and test on every push or PR, auto-ship on pass). No engine chosen. |
| T18 | Build versus adopt for the v0.001 coder | Decide whether to build a coding CLI or use an existing one (Kilo Code, Claude Code, Antigravity) as the v0.001 coder. | S2 turn 5; S1 l.158–172 | **In progress** | Francis said Claude Code can act as the coder for v0.001, either building a boilerplate tool or adopting an existing one. No formal decision logged. |
| T19 | v0.001 scope and sequencing | Decide whether v0.001 ends at a working persistence and message-passing core that a human relays, with agents plugged in at v0.5. | S1 l.194 | **Unclear** | The question was posed to Francis. No answer recorded. |

### B. Client and UI

| # | Thread | One-line description | Sources | Status | Latest mention and outcome |
|---|---|---|---|---|---|
| T20 | Client UI framework: WinUI 3 versus Avalonia | Choose one framework for the desktop and Android clients, weighing headless testing, native feel and code sharing. | S2 turns 23–24; S1 l.1583–1790, l.1795–1975; S5 Windows §2, Android §4 | **Completed** (decision); hands-on spike not run | Avalonia is DECIDED (marked CHANGED from WinUI 3). The deciding factor was headless testing via `Avalonia.Headless`. WinUI 3 remains the documented fallback. The assistant recommended a throwaway spike in both frameworks (S1 l.1775). S5 lists it as an open validation step. |
| T21 | Headless GUI-testing survey | Find out which GUI stacks (C, WinUI 3, Qt, Textual, Slint, Rust, Python) can render headlessly for agent verification. | S1 l.1597–1657 | **Completed** (superseded by T20) | Qt and WinUI 3 were covered in depth. Textual and Slint were marked "less exhaustively verified". The survey fed directly into T20. |
| T22 | Editor and terminal embedding under Avalonia | Choose the Monaco-in-WebView2 setup, the ConPTY terminal library, and the paid versus free WebView2 package. | S5 Windows §2, §4; S5 Server §2 (diff reuse); S1 l.1188 | **Unclear** | Monaco is DECIDED as the editor and diff viewer. Terminal libraries (`Iciclecreek.Avalonia.Terminal` and others) are RESEARCHED, but the library choice is OPEN. The most polished WebView2 integration is in Avalonia XPF, a paid tier. Not decided. |
| T23 | Android feasibility: Avalonia 12 and foldable detection | Check whether Avalonia on Android is mature enough, and whether fold and posture detection works through .NET for Android. | S5 Android §4, Windows §2; S1 l.2013–2099 | **Completed** (research); foldable spike not run | Avalonia 12's rebuilt Android backend is RESEARCHED as resolving the maturity concern. Foldable detection uses Jetpack WindowManager through .NET for Android, a documented pattern. The foldable-layout validation spike is still open. |
| T24 | Local NPU target by platform | Decide which NPU each client uses for local summarization and voice cleanup, and whether the server needs one. | S2 turn 13, turn 31; S5 Android §4; S5 Windows §3 | **Unclear** | Android is DECIDED: ONNX Runtime Mobile with QNN, Snapdragon only. The server has no NPU (S2 turn 31). Windows is conflicting: S2 says the Windows app targets the Intel NPU, but S5 names DirectML and QNN as execution-provider options without naming a vendor. |
| T25 | Linux client feature scope | Define what the Linux client includes beyond "same as Windows". | S5 Linux | **Proposed** | Low priority. Scope undefined. The PTY backend is noted as a native equivalent, not ConPTY. |
| T26 | Agent GUI control and GUI session pool | Decide how agents control graphical apps and websites (Playwright, Chromium, VNC), and whether a shared pool of GUI sessions is needed. | S2 turns 30–31; S5 Soul §2 (pool PROPOSED, OPEN) | **Unclear** | S2 turn 30 proposed a layered approach (Playwright headless, then VNC). In turn 31 the user challenged it: the server has no NPU, and agents must handle CAPTCHAs and visual checks. No replacement was settled. The Soul doc says agents cannot watch live viewports, so GUI work is confined to Bazaar.Windows. The GUI session pool is PROPOSED with sizing and checkout OPEN. |
| T27 | Constellation rendering technology | Choose how the knowledge-graph view is rendered: native Skia, D3-Force, WebGPU, or Win2D. | S5 Constellation §1 | **Completed** | Native Skia through Avalonia is DECIDED. D3-Force and WebGPU are web-only and ruled out. Win2D is no longer needed under Avalonia. A 3D view can come later on the same data model. |

### C. Engine tooling

| # | Thread | One-line description | Sources | Status | Latest mention and outcome |
|---|---|---|---|---|---|
| T28 | Engine headless verification (Godot and Roblox) | Check what agents can verify without a GUI: Godot `--headless` and Roblox `luau-analyze` plus Selene. | S5 Soul §2; S1 l.1442–1500 | **Completed** | Godot headless covers compile errors, unit tests and exports. Luau static analysis works with no Studio. The known gap is any code that calls real Roblox APIs, which needs a client. The same gap applies to Godot. |

---

## Part 2: Named systems and proposals

These are named design ideas, not research threads. Status is from the latest source that mentions each one. Where no later source mentions it, status is "Unclear".

| Name | Status | Sources | Notes |
|---|---|---|---|
| The Constellation (formerly "Mood Board") | Completed (renamed) | S5 Constellation §1; S1, S2 | DECIDED. The rename replaces the informal "Mood Board". |
| Reverse Gravity Layout ("anti-gravity") | Completed | S5 Bazaar §4, Constellation §1; S2 turn 27 | DECIDED. |
| Git Solar System | Completed (naming) | S5 Constellation §2; S2 turn 29 | DECIDED for naming and the visual language. |
| Document Vault (Index, Plan, Deep Dive, Research docs) | Completed | S5 Constellation §3; S2 turn 28; S3 | DECIDED. "Deep Dive" is a per-system doc type. |
| Sidewalk (casual, non-code GC) | Completed | S5 Bazaar §2; S3 | DECIDED. The rename to "Cafeteria" or "Break Room" was rejected. |
| Work-Life Simulation (shifts, breaks, vacations) | Completed | S5 Bazaar §4; S4 l.259–287 | DECIDED. |
| Council (voting and debate) | Completed | S5 Bazaar §5; S3 l.315 | DECIDED. Not yet built. |
| Soul System and Soul Passport | Completed | S5 Soul §1; S2 turn 30 | DECIDED. |
| Email (external only) | Completed | S5 Bazaar §2; S2 turns 16–18 | DECIDED. Internal coordination stays in GCs. |
| Engine Integration API | Completed (direction) | S5 Bazaar §4; S3 l.531 | DECIDED in direction. Roblox Studio is first-class. |
| Reputation and voting | Completed | S5 Bazaar §5; S3 l.287–315; S4 l.197 | DECIDED. |
| Skimmer Bouncer (also "Passive Reading" or "Bouncer pattern") | Proposed | S5 Bazaar §2, §4; S2 turn 10; S3 | Cheap model reads every GC message and wakes the full agent only when relevant. Same concept under three names. |
| Event bus ("Nervous System") | Proposed | S5 Bazaar §2; S3 l.635 | Must not replace GCs. |
| Mirror Agent | Proposed | S5 Bazaar §4 | Not revisited. |
| What If Engine | Proposed | S5 Bazaar §4 | Not revisited. |
| Historical Replay / Time Machine | Proposed | S5 Bazaar §4 | Not revisited. |
| Psych Ward / The Asylum | Proposed (semi-serious) | S5 Bazaar §4; S3 l.2882 | Not revisited. |
| Mandatory vent post | Proposed (semi-serious) | S5 Bazaar §4; S3 l.2960 | Not revisited. |
| Bazaar Archive | Proposed | S5 Bazaar §4 | Not revisited. |
| Talent Pool | Proposed | S5 Bazaar §4 | Not revisited. |
| Genetic Memory Transfer | Proposed | S5 Bazaar §4 | Not revisited. |
| Cross-Project Leakage / Hive Mind | Proposed | S5 Bazaar §4 | Not revisited. |
| Playtest Mode | Proposed | S5 Bazaar §4 | Constrained by GUI limits. |
| I'm Feeling Lucky | Proposed | S5 Bazaar §4 | Not revisited. |
| Company Constitution | Proposed (5 stars). OPEN: v1 not drafted. | S5 Bazaar §4, Open Q1; S3 l.1851 | Agreed to be essential. |
| Organizational Chart | Proposed (5 stars) | S5 Bazaar §4; S3 l.1910 | Not revisited. |
| Resource Economy | Proposed (4 stars). OPEN: not spec'd. | S5 Bazaar §4, Open Q2; S3 l.1950 | Not revisited. |
| Interrupt System | Proposed (5 stars) | S5 Bazaar §4; S3 l.1994 | Not revisited. |
| Company Calendar | Proposed (4 stars) | S5 Bazaar §4; S3 l.2028 | Not revisited. |
| Office Spaces | Abandoned (REJECTED) | S5 Bazaar §4; S3 l.2060 | GCs self-organize. |
| Company Memory | Proposed (5 stars) | S5 Bazaar §4; S3 l.2096 | Overlaps with Vault and decision logs. |
| Health System | Proposed (5 stars) | S5 Bazaar §4; S3 l.2140 | Not revisited. |
| Company Analytics | Proposed (4 stars) | S5 Bazaar §4; S3 l.2168 | Not revisited. |
| Bazaar SDK | Proposed. Shape OPEN. | S5 Bazaar §4, Open Q3; S3 l.2204 | Agreed in principle, no API surface. |
| Observatory | Proposed | S5 Bazaar §4; S3 l.2292 | Not revisited. |
| Digital Twin | Proposed | S5 Bazaar §4; S3 l.975 | Same name in both sources. |
| Soul Marketplace | Proposed | S5 Soul §1; S2 turn 29 | Not revisited. |
| Identity Service | Proposed. OPEN: not adopted. | S5 Soul §1, Open Q1 | Chokepoint for credentials. |
| Shared pool of GUI sessions | Proposed. OPEN. | S5 Soul §2 | See T26. |
| Context, Attention, Intent, Workspace Profiles, Plugin Capability, Presence, Experience Systems | Unclear (proposed only in S3) | S3 l.719–975 | Proposed by ChatGPT. Not carried into S5 or S1. Never triaged. |
| Knowledge Graph (S3 proposal) | Abandoned (merged) | S3 l.761; S5 Constellation §1 | S5 rejects a separate Knowledge Graph as redundant with Vault plus Constellation. |
| Modular UI panes | Unclear | S3 l.575 | Not carried into S5. |
| Anti-loop sanity checks (Watercooler) | Unclear | S4 l.287 | Not carried into S5. |
| Energy levels and Pomodoro breaks | Unclear (folded into Work-Life) | S4 l.259 | Likely absorbed by Work-Life Simulation (DECIDED). |
| Brand combos per agent | Completed (as "no global auto/*") | S4 l.100; S5 Bazaar §3 | Per-agent routing whitelists are DECIDED. |
| Senior and junior dev hierarchy | Completed (partially) | S4 l.319; S5 Bazaar §3 | Junior swarm exists. Specialist emergence is PROPOSED. |
| Nigerian Prince Protocol (scam-baiting) | Proposed (not a real spec) | S5 Bazaar §2; S2 turn 17 | Humour. |

---

## Part 3: Duplicates and renames

Each row groups one thread or system that appears under different names.

| Canonical name | Also called | Notes |
|---|---|---|
| The Constellation | "Mood Board", "Mood board" (S2, S4) | Renamed in S5. |
| Reverse Gravity Layout | "anti-gravity" layout | Same layout. |
| Skimmer Bouncer | "Passive Reading" (S2, S5), "Bouncer pattern" | Same concept. |
| Knowledge Graph (S3) | Constellation plus Vault | S5 says it is redundant. |
| Event bus | "Nervous System" | Same concept, S3 and S5. |
| Machine accounts | GitHub Apps (superseded) | Same thread (T16). |
| Server Core plus nested WSL2 per agent | "Soul VM", "golden image plus differencing disks", "Server Core plus WSL2" | Same thread (T03). Superseded by the two-VM proposal. |
| Two-VM per agent | "one Windows and one Linux VM per agent" | Same as T03. |
| Shared D-drive | "shared virtual disk", "host-mediated SMB share", "host-folder mount" | Same thread (T05). Shared block disk rejected. |
| Hyper-V isolation mode | "Hyper-V-isolated containers" | Same thread (T04). |
| Firecracker microVMs | "Firecracker for Windows" | Same thread (T02). |
| Search stack | "Vault search", "document search", "BM25", "ParadeDB", "Meilisearch", "Typesense", "Quickwit" | Same thread (T12). |
| Real-time transport | "WebSocket", "Socket.io", "WebTransport", "SignalR" | Same thread (T09). |
| Diff viewer | "GitHub diff rendering", "Monaco diff editor" | Same decision (T22). |
| Shared UI project | "Bazaar.UI.Core", "/client/shared-ui", "shared UI class library" | Same proposal. |
| Server product name | "Bazaar.Server", "Bazaar.Server.Windows" | Naming collision with "Bazaar.Windows" (client). S1 recommends keeping "Bazaar.Server" with Windows-only scope documented. Not logged. |
| Headless GUI testing | "Avalonia.Headless", "headless render to bitmap" | Same concern (T20 and T21). |
| Backend language candidates | "Node.js plus Fastify" (S2 recap), "Bun" (S5) | Same thread (T01). The Node option is not the same as Bun. |
| Cache and queue | "Redis", "Valkey", "Redis Pub/Sub", "NATS", "MQTT" | Same thread (T11). |
| Vector store | "pgvector", "Chroma", "LanceDB", "Qdrant" | Same thread (T10). |

---

## Part 4: Conflicts and stale sources

1. **Backend language.** S5 Server says UNDECIDED. S1 Aug 7 is leaning .NET. S2 proposed Node.js plus Fastify, which S5 says was never ratified. The S1 position is the latest, but it is not decided.
2. **VM architecture.** S5 Soul says the per-agent Windows Server Core plus nested WSL2 design is DECIDED. S1 Aug 7 proposes two separate VMs per agent instead. Francis has not confirmed the change. The S5 Soul doc is stale on this point.
3. **Linux host.** S5 Soul says QEMU/KVM covers Linux hosts. S1 Aug 7 descopes the Linux host to a stretch goal (Bazaar.Server.Windows for now).
4. **Windows NPU vendor.** S2 turn 13 says Intel NPU. S5 Windows names DirectML and QNN as options without a vendor. This is unresolved.
5. **Windows Server licensing.** S5 concludes Datacenter is likely. The two-VM proposal (12–16 Windows VMs) makes that conclusion stronger, but S5 does not reflect it.
6. **Hyper-V isolation footprint.** S1 says Microsoft's documentation shows a larger footprint than process isolation. No link is given, so treat it as unverified.
7. **File date.** The S1 file name is dated 2026-10-25, but its content runs from 2026-08-06 to 2026-08-07. The inventory uses the content dates.

---

## Appendix: Open validation items

These items were recommended or flagged as actions but not performed.

- Avalonia hands-on spike (T20). Recommended in S1 l.1775; S5 Windows Open Q1.
- Foldable-layout validation spike (T23). S5 Android Open Q1.
- Cloud Hypervisor Windows-guest test (T02). S5 Soul §2.
- GitHub App issue-creation test on a throwaway public repo (T16). S5 Server §2.
- RTK and Caveman compression test on real code (T15). S5 Server §3.
- Licensing consult for CAL and agent-only use (T08). S5 Soul §2.
- ParadeDB `pg_search` AGPL legal check before open-sourcing (T12). S5 Constellation §3.
- NUC RAM upgradeability check (T07). S1 l.2786.
