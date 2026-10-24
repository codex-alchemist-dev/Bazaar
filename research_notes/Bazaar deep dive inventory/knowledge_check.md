# Bazaar knowledge check: deep dive set vs. prior-chat uploads

## Scope, method, and handling notes

- Compared the seven deep dives (extracted from `bed41a95-files.zip`) against the ChatGPT export, the Gemini export, the DeepSeek JSON (33 Q/A entries), and the "Secret project team assembly" file.
- No web lookups were done. Third-party product facts are flagged for verification, not confirmed or refuted.
- Citation keys (absolute paths):
  - `DD-<Name>` = deep dive, e.g. `DD-Soul` = `/tmp/claude-0/-home-user-Bazaar/c63e21e3-9139-50bc-9f48-e82954f68a61/scratchpad/files-zip-2/Bazaar.Soul.md`. `DD-Core` = `.../files-zip-2/Bazaar.md`.
  - `GPT` = `/root/.claude/uploads/c63e21e3-9139-50bc-9f48-e82954f68a61/c9f71868-chatgpt-conversation.md`, cited by line.
  - `GEM` = `/root/.claude/uploads/c63e21e3-9139-50bc-9f48-e82954f68a61/7092de89-gemini-conversation-2026-08-01-01-30-44.md`, cited by line.
  - `DS[n]` = DeepSeek JSON entry index n (0-based), `/root/.claude/uploads/c63e21e3-9139-50bc-9f48-e82954f68a61/a4bc545f-deepseek-conversation.json`. A text dump is at `/tmp/claude-0/-home-user-Bazaar/c63e21e3-9139-50bc-9f48-e82954f68a61/scratchpad/deepseek_dump.txt`.
  - `SEC` = `/root/.claude/uploads/c63e21e3-9139-50bc-9f48-e82954f68a61/bdf692f3-Secret_project_team_assembly-2026-10-25.md`, cited by line. `H#` marks the nth human turn; `A` marks an assistant turn.
- Sensitivity: `SEC` line 6 says "Keep this on the DL." This file quotes it. `research_notes/` sits inside the Bazaar git repo, which the deep dives say will be open-sourced. Do not commit this file to a public branch.
- Snapshot timing: the deep dive set appears to be a snapshot from the doc-split step (about 12:07–12:13 UTC, 2026-08-07; `SEC` L2103–L2344). It predates the Aug 7 exchanges at 12:16–13:07 UTC (`SEC` L2362–L2786: .NET frontrunner, Linux host descoped, two-VM-per-agent, RAM sizing). Those decisions are absent from the deep dives.
- Date anomaly: the `SEC` filename says 2026-10-25 (after today's date, 2026-10-24). Its header says created 2026-08-06 and updated 2026-08-07 13:07 UTC. Ordering here uses the header timestamps.

---

## Key Question 1: Which design decisions were revised over time?

### Takeaway

The biggest revisions are in the Soul/VM and Server-backend areas, which the deep dives have not caught up with. The user has also explicitly overturned several earlier assistant proposals (event bus as coordination, knowledge graph as a separate feature, workspace profiles, office-space rooms, a fun-vs-substrate split, and a money "wallet" for agents). Some of these rejections are recorded in the deep dives, others are not.

### Cited Findings

**Stale in the deep dives (contradicted by later discussion)**

- **Soul VM topology.** `DD-Soul` L24 says "Windows Server Core + WSL2, not two separate VMs" (DECIDED), and L25 says Hyper-V on a Windows host and QEMU/KVM on a Linux host are "the same architecture either way." Later, the user proposed one Windows VM plus one Linux VM per agent, with SMB and no nested WSL2 (`SEC` H38, L2616). The assistant endorsed it (`SEC` L2630–L2666). The user then asked follow-ups about per-launch cost and RAM (`SEC` H39–H40). This is user-proposed and assistant-endorsed, but no explicit adoption is recorded.
- **Linux host.** `DD-Soul` L25 keeps a Linux-host path (qcow2/QEMU). The user later said "For now we'll have Bazaar.Server.Windows and maybe think of linux somewhere far down the line" (`SEC` H36, L2528). This is a direct contradiction.
- **Server host.** `DD-Server` L11 says "Runs on your NUC." `SEC` H18 (L1145) says the server is more likely Windows. `SEC` H40 gives the NUC as 32 GB RAM with a main PC at 96 GB. `DS[6]` says the NUC is an 1135G7 shared with a receipt scanner.
- **GUI session pool.** `DD-Soul` L32 keeps a "shared pool of GUI-capable sessions" as PROPOSED/OPEN. The user said "nah, no GUI session... we won't be giving them ANY desktop environment at all" (`SEC` H24, L1597).
- **Protobuf.** `DD-Server` L37 marks Protobuf messages as DECIDED ("refined"). Protobuf first appears as an assistant suggestion (`SEC` L1123). The user never endorsed it in the uploads. `SEC` L2437 proposes dropping it in favour of a shared `Bazaar.Contracts` C# project if the server goes .NET. Downgrade it.
- **Backend language.** `DD-Server` L40 and L46–L54 list only Bun, Go, and Elixir/Phoenix as options. The assistant recommended .NET as frontrunner (`SEC` L2421–L2441, after H34 at L2362). `DD-Server` also says DeepSeek's "recap" proposed Node + Fastify (L40). That recap appears in the DeepSeek answers for entries 19 and 20 (dump lines 231 and 265), and the assistant echoed it at `SEC` L1004–L1045. It was never ratified.
- **Resource economy.** `DD-Core` L96 and L123 keep "per-agent budgets" as PROPOSED/OPEN. The user rejected agent money: "NO WERE NOT GIVING THEM A WALLET... I will never put money into" (`DS[18]`). The ChatGPT budget idea (`GPT` L1950–L1990) and the DeepSeek "Budget Meeting GC" (`DS[20]`) are both affected.
- **Psych Ward and vent post.** `DD-Core` L84–L85 labels them "PROPOSED (semi-serious)." The user rejected a fun-vs-substrate split: "There is genuinely nothing there purely for 'fun' anymore" (`SEC` H5, L277–L282). The user also tied the Psych Ward to a rant (`DS[18]`). Relabel as operational.
- **Observability.** `DD-Core` L104 labels Observatory "PROPOSED (expected as given)." The user said "I thought observability was a given" (`GPT` L2385). The ChatGPT "three questions" principle (`GPT` L2568–L2574) is not in the deep dives. Promote to DECIDED core principle.
- **Windows NPU providers.** `DD-Win` L39 says "NPU execution-provider selection (DirectML vs QNN)." The user said Windows uses the Intel NPU and "we don't gotta support snapdragon on windows" (`DS[13]`). `SEC` L755 confirms "Windows gets Intel NPU with OpenVINO, Android uses QNN and SNPE." QNN on Windows is a leftover.
- **Android gestures.** `DD-Android` L30 says "hold-anywhere brings up a radial menu; holding without swiping drops into voice dictation." These two outcomes conflict. The user's version is `DS[25]` (hold brings radial menu) and `DS[26]` (hold without swipe opens voice). Clarify the gesture spec.
- **Vim leader key.** `DD-Win` L47 says "leader-key driven (`<Space>`)." `DS[26]` says the user wants typed two-chord commands, "nothing is a Ctrl+ something," and not chorded binds. Clarify whether `<Space>` is a leader.
- **Mood-board rendering.** `DD-Const` L14 correctly moved to Skia. `SEC` L1083 and L1236 still mention Win2D. Already fixed in `DD-Const`; keep as-is.

**Consistent (no action needed)**

- **Event system.** Proposed by the assistant as "everything becomes an event" (`GPT` L641–L675). The user accepted it only if it never replaces GCs (`GPT` L1022). ChatGPT accepted this (`GPT` L1033–L1063: "GCs are where agents think together; events are how the software reacts"). `DD-Core` L33 matches. Keep, and add the constraint text verbatim.
- **Knowledge graph.** Proposed as a separate feature (`GPT` L761–L803). The user said "isn't that the mood board and vault combi" (`GPT` L1023). Accepted at `GPT` L1071. `DD-Const` L11 matches.
- **Office spaces.** Rejected. The user said GCs nest, group, and recurse (`GPT` L2379). `DD-Core` L99 matches.
- **Email.** External-only, notifications via GC (`DS[16]`–`DS[18]`). `DD-Core` L37–L38 matches.
- **Sidewalk.** The user rejected "Cafeteria" (`DS[18]` last line). `DD-Core` L32 matches.
- **Build order.** Server, Windows, Android, Linux (`SEC` H28, L1955). `DD-Linux` L15 matches.
- **Client framework.** WinUI 3 was replaced by Avalonia (`SEC` H25–H28, L1703–L1955). `DD-Win` L18 matches. The user's complaint about WinUI's "stiff" feel is in `DS[24]`.
- **Android framework.** Kotlin plus Compose was locked in earlier (`SEC` L765, L1034). Replaced by Avalonia. `DD-Android` L39 matches. `SEC` L1034 is stale.

**Assistant proposals later reversed (should appear as REJECTED in deep dives)**

- Workspace profiles (`GPT` L835–L865): the user said "meh" (`GPT` L1025). Accepted in reverse at `GPT` L1141–L1165. Not recorded as rejected in `DD-Core`.
- Kanban as a to-do list: the user recast it as a dependency/scheduling board (`GPT` L1023). ChatGPT accepted the "Blocked by Task B" model (`GPT` L1107–L1137). No Kanban section exists in the deep dives.
- Fun-vs-substrate split (`SEC` L240–L258, assistant): rejected at `SEC` H5 (L277).
- Firecracker per-agent microVMs as default (`SEC` L1246): reversed at `SEC` L1388 after H19.
- Tiny11 as golden base (`SEC` L1398): reversed at `SEC` L1482 (Server Core has no equivalent need). `DD-Soul` L41 matches the final state.
- Machine accounts for agents (`SEC` L439, L612–L636, L879–L895): reversed to GitHub Apps at `SEC` L921–L923 after H14 (L857). `DD-Server` L20–L21 matches. ChatGPT email framing as "inter-company communication" with an Inbox GC (`GPT` L1661–L1690) was never endorsed.
- Docker or Firecracker per agent (`DS[20]`): superseded by Hyper-V VMs (`SEC` H19–H38). Not in deep dives, correctly.

**Stage plan (not in deep dives)**

- v0.001: Claude Code as the sole coder, with Claude orchestrating. v0.5: the founding four (Claude, Gemi, DeepSeek, ChatGPT) collaborate without Claude Code, with a "small council" and tool calling, but no tightly integrated Kanban or mood-vault. v1.0: full Server, then Windows, then Android (`SEC` H2–H3, L113–L200). This is user-stated and absent from the deep dives.

### Inferences

- The deep dive set needs a snapshot date and a rejected-decisions list. Several "DECIDED" items are contested by later user statements.
- Items the user already rejected should be moved out of the working set, so later agents do not re-derive them. Examples: the wallet, workspace profiles, office spaces, GUI pool, and the fun-vs-substrate split.

### Gaps

- Did the user ever explicitly adopt two-VM-per-agent? The sources show proposal plus endorsement, with no explicit "yes."
- Mirror Agent, What-If Engine, Time Machine, Talent Pool, Genetic Memory, Hive Mind, Psych Ward, and "I'm Feeling Lucky" come from DeepSeek or ChatGPT. Several cite earlier chats not uploaded (see KQ4). The uploaded excerpts show no user endorsement of most of them.

---

## Key Question 2: Where do sources disagree on facts?

### Takeaway

The main factual disagreements are the agent roster (Charles vs. ChatGPT), routing-tag syntax, the Windows/Android NPU vendors, the NUC's hardware, the server's host OS, the email domain, and the OmniRoute/NIM figures. Several inconsistencies sit inside a single source.

### Cited Findings

**Agent roster and roles**

- The founding team in `SEC` H1 and H7 (L6, L386) is Francis, Claude, Gemi, DeepSeek, and ChatGPT ("I got you chatGPT"). Charles is not in it. `SEC` L67 (assistant thinking block) first misidentified the team as "Claude, Gemini, DeepSeek, Charles, and the user." Later turns corrected it to ChatGPT.
- Charles is the user's boilerplate agent (`DS[6]`, a Brooklyn 99 pun). `DD-Core` L55 and `GPT` L176 treat it as a junior specialist. `DD-Core` L56 lists ChatGPT as "Founding member, live-system role TBD." Charles's status (junior vs. founding) is unresolved.
- Gemini's coding role: `DS[8]` says "Claude and deepseek and Gemini code depending on task." `GPT` L143 says Claude does "Less coding." `DD-Core` L53 gives Gemini research/PM only. Three different role boundaries.
- DeepSeek introduced "Jerry" (Scrum Master) and "Terry/The Scambaiter" in its answers to `DS[17]`–`DS[19]` (dump lines 195, 219, 231). The user never named either in the uploaded questions. These are assistant-coined. Keep out of canonical docs until adopted.

**Model whitelists and routing tags**

- Claude whitelist: "Haiku, Sonnet, Opus, Fable" (`GPT` L226–L237; `DD-Core` L52; `GEM` L87 user text "fable of all versions"). "Fable" appears in user text (`GPT` L1222 "claude fable 5"). Verify the official model list before encoding.
- DeepSeek whitelist: "V4 Flash, V4 Pro, 1M-context" (`GPT` L239–L247; `DD-Core` L54; `GEM` L87). But the DeepSeek answer at `DS[10]` says "DeepSeek V3 cheapest." Version inconsistency.
- Routing tag for Charles: `DD-Core` L55 says `auto/cheap`. `GPT` L185 says `auto/coding:cheap` ("Probably"). The user typed `auto/code:cheap` (`GEM` L87). The DeepSeek entry 8 answer (dump line 69) says Charles `auto/cheap`, DeepSeek `auto/coding:cheap`, Claude `auto/reasoning`. Its entry 6 answer (dump line 29) adds `auto/reasoning:pro`. Its entry 11 answer (dump line 142) adds `auto/research`. Gemini's list (`GEM` L69–L78) is `auto/coding`, `auto/fast`, `auto/cheap`, `auto/smart`. Eight-plus tag variants across sources; the real list is unknown.

**Product names and identifiers**

- Working name: `Bazaar.CDX.ALCMST.firev.xx.kg` (`GPT` L19–L20; `DS[9]`).
- `CodexAlchemist.Bazaar` (`SEC` H6, L338).
- GitHub org `codex-alchemist-dev` (`SEC` L505, H13 L642). Alternate `ca-firev` (`SEC` L508).
- Email domains: `claude@bazaar.firev.xx.kg` (`DD-Core` L37); `claude@firev.xx.kg` (`GPT` L1673); "infinite @firev.xx.kg" (`DS[16]`); plan for `@codexalchemist.firev.xx.kg` (`SEC` L427). Unresolved.
- Server naming: `Bazaar.Server` (`DD-Server`); `Bazaar.Server.Windows` (`SEC` H36, L2532). The assistant flagged a collision with `Bazaar.Windows` (client) at `SEC` L2548–L2562.
- Codex Alchemist scope: "first company built on Bazaar, dev team behind Fireball Everything" (`DD-Core` L21; `GEM` L364–L372; `GPT` L3882–L3892). The user asked whether Codex Alchemist is game-dev-only (`DS[19]`). Open.
- Sidewalk vs. Cafeteria: `DS[18]` rejected Cafeteria. `DD-Core` L32 agrees.
- The user's own word for the Psych Ward was "asylum" (`GPT` L2866). `DS[18]` says "psyche pard." Both map to `DD-Core` L84 "Psych Ward / The Asylum."
- "Soul" as the name for the agent identity package is not confirmed by the user. `DS[29]` says "we really need a name for the personality, memory vault, email, and VM per account thing." `DD-Soul` L11 uses "Soul System." Confirm.
- "The Constellation" is user-confirmed (`SEC` H27, L1875).

**Hardware and NPU**

- NUC: `DS[6]` says 1135G7 shared with a receipt scanner. `SEC` H40 (L2748) says NUC has 32 GB RAM. Both may be true; neither file describes the other.
- Arc B580: `DS[21]` says a Core Ultra 9 285K with a 13 TOPS NPU and "an arc B580" for the local machine. `DS[31]` says the NUC has an Arc B580 ("which the user mentioned has an Arc B580"). The user did not say that. Likely misattributed.
- Windows NPU: user says Intel NPU via OpenVINO (`DS[13]`; `SEC` L755). `DD-Win` L39 says DirectML vs QNN. `SEC` L767 says ONNX Runtime with DirectML and OpenVINO on Windows.

**OmniRoute and NVIDIA NIM (all third-party, flagged)**

- OmniRoute size: `DS[0]` says "over 230 AI models"; `DS[3]` says "250+ AI providers." Internal disagreement.
- Anthropic BYO key: `DS[1]` says the docs "don't explicitly mention" it. `DS[2]` then answers "Yes, absolutely" and describes a paid-tier-first, free-fallback chain. The second is not supported by the first's research.
- NIM free-tier limits: `GEM` L314 says "around 40 RPM and ~1,000 requests per day," cited to aggregator blogs. `DD-Server` L39 and `DD-Core` L55 repeat it as DECIDED and call the supply "practically infinite." Same paragraph says "not truly infinite." `GEM` L309 calls it "almost, but not quite."
- Antigravity / Antigravity CLI / Gemini CLI: `GEM` L13–L29 says they share one account-level quota pool. `GEM` L37–L47 says OmniRoute lists them as separate providers because each has its own OAuth handshake and cooldown. Both are presented as fact with Reddit, SourceForge, and CoddyKit citations.
- Compression: `SEC` L356–L376 says the assistant checked a claim that OmniRoute's RTK and Caveman engines preserve code. `DD-Server` L38 says "should be verified empirically." Keep it that way.
- Gateway port: `GEM` L141 uses `localhost:20128/v1`. Unverified.

### Inferences

- Routing-tag syntax and provider counts are the most likely to break a config. Verify against the real OmniRoute docs before writing any whitelist.
- The NIM "practically infinite" wording is a self-contradiction in `DD-Server` L39 and should be removed.

### Gaps

- Which of the Charles/ChatGPT roster versions is current? Ask the user.
- The uploaded DeepSeek file cannot confirm the "Node + Fastify recap" source in `DD-Server` L40, because the recap appears only in the DeepSeek dump lines `DS[120]`/`DS[231]`/`DS[265]`, which are assistant summaries.

---

## Key Question 3: Earlier ChatGPT/Gemini claims corrected by the user, and corrected versions

| # | Earlier claim (source) | User correction (source) | Corrected version (where it now stands) |
|---|---|---|---|
| 1 | Everything becomes an event; event bus coordinates agents (`GPT` L635–L675) | Only if it never undermines GCs (`GPT` L1022) | GCs are the primary interface and the canonical history; events are internal reactions (`GPT` L1058–L1063, L1302–L1306). `DD-Core` L33 partly reflects this. |
| 2 | Knowledge graph as a separate feature (`GPT` L761) | Vault + mood board already are the graph (`GPT` L1023) | The Constellation = Vault + Mood Board, two views of one graph (`DD-Const` L11). |
| 3 | Kanban as a task list (implied; `GPT` L1101) | Scheduling board, dependency manager (`GPT` L1023) | "Blocked by Task B" dependency model (`GPT` L1107–L1137). Not yet in deep dives. |
| 4 | Workspace profiles (Developer/Artist/QA etc.) (`GPT` L835–L865) | "Meh"; UI should emerge from learning (`GPT` L1025) | No profiles; behaviour-driven adaptation (`GPT` L1141–L1165). Not recorded as rejected. |
| 5 | Office Spaces as fixed rooms (`GPT` L2060–L2092) | GCs nest/group/recurse on their own (`GPT` L2379) | GC tree grows with the company (`GPT` L2401–L2405). `DD-Core` L99 REJECTED: matches. |
| 6 | Email as an internal "inbox GC" channel (`GPT` L1661–L1690) | Email for external and fun only; notifications via GC (`DS[18]`) | External-only email (`DD-Core` L38). Matches. |
| 7 | Agents on a per-agent dollar budget (`GPT` L1950–L1990) | "NO WERE NOT GIVING THEM A WALLET" (`DS[18]`) | No currency. Replace with non-monetary quotas tied to Work-Life Simulation (open). `DD-Core` L96 still OPEN. |
| 8 | "Boring" substrate under fun UI (`SEC` L240–L258, assistant) | "Nothing purely for fun" (`SEC` H5, L277–L282) | Fun features are formal (vent post and Asylum are operational). |
| 9 | Psych Ward / vent post as semi-serious (`DD-Core` L84–L85 based on `DS[18]`) | Psych Ward ties to rants and reflection (`DS[18]`) | Operational recovery and reflection; keep. |
| 10 | Mobile as "Mobile IDE / companion app" (`GPT` L2712–L2722) | "Development Console" (`GPT` L2622) | Use the user's phrase. `DD-Android` L11 has it. |
| 11 | Server "Run the company" / Windows "Build products" / Android "Stay connected" (`GPT` L2796–L2806, assistant) | User phrasing: server = best software company to work at; Windows = best AI IDE; Android = handheld development console (`GPT` L2620–L2622) | `DD-Server` L11, `DD-Win` L11, `DD-Android` L11 use the assistant's slogans. Replace with the user's wording. |
| 12 | Agent email and GitHub org membership via machine accounts (`SEC` L439) | Machine accounts are a dead end (`SEC` H14, L857); GitHub Apps instead (`SEC` L921) | GitHub Apps (`DD-Server` L20–L21). Matches. |
| 13 | Claude is the one "architect" with no coding (`GPT` L134–L143) | "Claude and deepseek and Gemini code depending on task" (`DS[8]`) | Unresolved (see KQ2). |
| 14 | "Infinite @mentions" as the goal (`GEM` L162–L231) | Goal is a team that develops without the user, with curfew/breaks (`GEM` L231) | Work-Life Simulation (`DD-Core` L93) matches. |
| 15 | DeepSeek "basically infinite" via NIM (`GEM` L301–L317) | User uses NIM free for roleplay (`GEM` L345) | Unresolved. Flagged in KQ5. |
| 16 | Event bus via Redis/NATS (`GEM` L173) | Valkey chosen (`SEC` H18) | `DD-Server` L36 matches. |
| 17 | DeepSeek "Terry" scam-bait and "Jerry" scrum (answers to `DS[17]`–`DS[19]`) | User never confirmed these names. | Not in deep dives. Keep out. |
| 18 | Client "Mobile IDE" wording (`GPT` L2712) | "Development Console" (`GPT` L2622) | See #10. |
| 19 | Tech stack with React/Vite web UI (`SEC` L1004, from DeepSeek recap) | "No web interface, ever" follows from the server/client split (`GPT` L2623) | `DD-Core` / `DD-Server` L12: no web interface. |
| 20 | NUC is the server (`DS[6]`) | Server "more likely Windows" (`SEC` H18); NUC 32 GB (`SEC` H40) | Hardware = NUC for now; host OS = Windows (`SEC` H36). |

Also corrected by the user inside the DeepSeek chat: DeepSeek's event-bus/MQTT design (`DS[7]`; user says "No orchestrator"), the server doing summarization (`DS[11]`–`DS[12]`; user says NPU on the client), and emails as an internal channel (`DS[16]`–`DS[18]`).

### Inferences

- The ChatGPT and Gemini exports contain several assistant proposals the user rejected in-turn. The deep dives keep some of them (office spaces, workspace profiles) and not others (Kanban dependencies, wallet rejection).

### Gaps

- Earlier chats referenced by ChatGPT (`GPT` L1, the "(1).json" and "(1)(1–3).json" files, and a "gemini-conversation-2026-08-01-00-20-02(1).md") are not in the upload. Corrections made there cannot be checked.

---

## Key Question 4: What is in the Secret file that is newer or missing from the exports?

### Takeaway

The Secret file (2026-08-06 to 08-07) contains the newest decisions: the v0.001/v0.5/v1.0 bootstrap plan, the Avalonia move, the seven-document split, the .NET frontrunner, the Windows-only server host, the two-VM-per-agent design, isolation-mode rejection, and RAM sizing. None of this is in the exports, and most of it is not yet in the deep dives.

### Cited Findings

**Newer than the exports (not in ChatGPT, Gemini, or DeepSeek)**

- Team and bootstrap plan: `SEC` H2–H4 (L113–L200). v0.001 uses Claude Code; v0.5 is the founding four without Claude Code; v1.0 is Server, then Windows, then Android. The user says "our 5" (Francis plus four AIs).
- Claude Code's built-in Task-tool subagents as a boilerplate delegator, with Aider as a fallback (`SEC` L188, assistant claim, unverified).
- Stated exit criteria for v0.5 and the warning against scope creep (`SEC` L146–L152).
- Decision logs: logged both in vault docs and in GCs; GCs never purged, only archived (`SEC` H5, L277–L282). Matches `DD-Core` L30 and L115.
- Seven-document split and Google Docs read-only limit (`SEC` H29–H33, L2103–L2344). Local `.md` is the source of truth.
- GitHub Projects v2 as an org-level Kanban for the dev team (`SEC` L447–L473). Distinct from Bazaar's internal Kanban.
- Monorepo vs. multi-repo question (`SEC` H8, L423–L441). `DD-Server` L41 records the two-repo answer, but the assistant's reasoning for this is not in the Secret file.
- Avalonia XPF commercial tier for the WebView (`SEC` L1749–L1770, assistant claim, unverified). Also `DD-Win` L24.
- .NET for Android with Jetpack WindowManager (`SEC` L1979–L2093). Also `DD-Android` L39.
- Shared UI class library (`SEC` L2025; `DD-Win` L39).
- .NET frontrunner for the server, with Orleans and a shared `Bazaar.Contracts` project (`SEC` L2421–L2441, assistant).
- Hyper-V vs. Cloud Hypervisor, host-OS asymmetry, Linux host descoped (`SEC` L2449–L2562).
- Terminal pass-through via `wsl.exe` on the Windows side and HvSocket to a guest-agent service (`SEC` L2518–L2528).
- Two-VM-per-agent with host-side SMB shared folder (`SEC` L2616–L2682). The shared-block-device option is rejected for corruption risk (`SEC` L2676).
- Hyper-V isolation mode investigated and deprioritized (`SEC` L2568–L2666).
- Resource model: VMs suspend and resume via saved state, mapped to Work-Life shifts (`SEC` L2720–L2748). RAM estimates for 6–8 agents (`SEC` L2748–L2786): about 6 GB per agent pair; 2–3 concurrent agents on a 32 GB NUC; about 18–24 GB planning peak. These are assistant estimates, not measurements.
- GitHub Packages 500 MB and CODEOWNERS on Team plans only (`SEC` L505–L542, assistant claims, unverified).
- Tiny11 and Windows IoT Enterprise reversal (`SEC` L1398–L1482).

**Missing from the Secret file and the deep dives, but present in the exports**

- Local Co-Worker (Claude Cowork analogue): offline, NPU-first, inline autocomplete, escalates to server (`DS[21]`–`DS[22]`; `GPT` L611–L617). No deep-dive section.
- Thinking GCs, nested under each project GC, visible to all (`DS[18]`). No section.
- Deadlines, KPIs, QPIs, task queue (`DS[18]`). No section.
- Built-in browser and agent browser control, captchas, VNC, Playwright vs. Chromium (`DS[30]`–`DS[31]`). No section.
- SMTP/IMAP server design and inbox UI (`DS[31]`). The user asked "how do we do our SMTP server." ChatGPT claimed a design was already "discussed" (`GPT` L1694) but no such design is in any source.
- Per-agent git clones or worktrees (`DS[6]`; open).
- Human user-GCs for non-dev chat (`DS[9]`–`DS[10]`).
- Trust ladder for external accounts: no internet, no posting, no account creation, then gradually trusted (`GPT` L3342–L3390). `DD-Core` L43 covers governance only in general terms.
- Skimmer relevance threshold of 75 (`DS[20]`; assistant).
- Observatory's "why / what / waiting" triad (`GPT` L2568–L2574).

**Confirmed in both Secret and deep dives**

- Avalonia, Constellation name, shared UI, and foldable-via-.NET-Android decisions.
- Valkey, WebSocket, Postgres, Monaco with WebView2 (user accepted, `SEC` H18).
- Build order and Linux client as low-priority (`SEC` H28).

**Ownership caveat**

- Several "DECIDED" items in the deep dives were introduced by the assistant and not confirmed by the user in the uploads: ParadeDB `pg_search` (`SEC` L1305–L1307), libgit2/LibGit2Sharp (`SEC` L1113, L1238), Protobuf (see KQ1), and ".NET" (assistant recommendation only).

### Inferences

- The deep dives are a snapshot. The next revision should rebuild `DD-Soul` and `DD-Server` from the Aug 7 12:16–13:07 exchanges.

### Gaps

- The user has not explicitly endorsed two-VM, .NET, or Linux-host descoping. These are user-stated preferences or proposals, and the assistant endorsed them. Ask for confirmation before writing them as DECIDED.

---

## Key Question 5: Doubtful third-party facts to verify (none verified here; no web lookups performed)

Flagged for verification only. Each has a source line and the risk if wrong.

### OmniRoute

1. Provider and model counts: "230+ models" (`DS[0]`), "250+ providers" (`DS[3]`). Inconsistent.
2. Free Claude via Kiro/Qoder tiers and a "Tier 4 free fallback" (`DS[2]`). Unverified.
3. Anthropic BYO key support (`DS[1]`–`DS[2]`). The first answer found no explicit documentation.
4. Routing tags: `auto/cheap`, `auto/coding`, `auto/coding:cheap`, `auto/code:cheap`, `auto/reasoning`, `auto/reasoning:pro`, `auto/research`, `auto/fast`, `auto/smart`. Gathered from `DS`, `GPT`, `GEM`, `SEC`. Confirm the real list.
5. "18 routing strategies," "12-factor scoring," and "known auto-combo bug" (`DS[4]`). Unverified.
6. Compression: RTK plus Caveman, "byte-for-byte code preservation" (`SEC` L356–L376; `DD-Server` L38 flags it).
7. Gateway default port `20128` (`GEM` L141).
8. Antigravity / Antigravity CLI / Gemini CLI: one quota pool vs. separate provider rows (`GEM` L13–L47). Aggregator citations only.
9. ToS and policy risk of routing multiple agents through free or OAuth-based consumer tiers (`SEC` L150, assistant; `DS[1]` OAuth note). Not in `DD-Server`. Recommend adding.

### NVIDIA NIM

10. Free-tier limits of about 40 RPM and about 1,000 requests per day (`GEM` L314; `DD-Server` L39; `DD-Core` L55). Source is aggregator blogs. Verify against NVIDIA's own documentation. Also verify per-model vs. per-account scope.
11. "Practically infinite" wording (`DD-Server` L39), contradicted by the same paragraph.

### Claude / Anthropic

12. Model names "Haiku, Sonnet, Opus, Fable" (`GPT` L226–L237; `DD-Core` L52). "Fable" in particular. Verify the current list before encoding whitelists.
13. "Claude Code subagents / Task tool" as a delegation mechanism (`SEC` L188). Verify current behaviour.
14. "Claude Code dynamic workflows uses Claude tokens" (`SEC` H4). User's own claim; verify if it matters for budgeting.

### DeepSeek

15. "V3 cheapest" (`DS[10]`) vs. V4 Flash/Pro whitelist (`GPT` L239–L247). Verify current names.

### GitHub

16. Machine accounts: "one free per person," "no paid path," "confirmed" (`DD-Server` L20; `SEC` L624–L636). ToS wording should be checked against the current GitHub Terms.
17. Apps can't open issues on public repos without installation (`DD-Server` L23 flags this itself). Keep the flag.
18. GitHub Packages 500 MB free (`SEC` L528–L542).
19. CODEOWNERS / required reviewers Team-plan only (`SEC` L538).
20. GitHub Projects v2 org-level, cross-repo (`SEC` L473).

### Windows / Hyper-V / virtualization

21. Windows Server Standard = 2 VMs per host, stackable; Datacenter = unlimited; price ratio 5.5–6x; break-even 5–12 VMs (`DD-Soul` L38). Verify with Microsoft licensing.
22. 180-day evaluation with rearm (`DD-Soul` L39).
23. CAL requirements for agent-only hosts (`DD-Soul` L40). Open.
24. Firecracker boot about 100–125 ms and tens of MB overhead (`SEC` L1396). Verify.
25. Cloud Hypervisor supports Windows 10/Server 2019 guests (`DD-Soul` L42; `SEC` L1374–L1380). Verify and check maturity.
26. Hyper-V isolated containers have larger footprint than process isolation, citing Microsoft docs (`SEC` L2662–L2668). Unverified.
27. Unreal Engine Windows container I/O and bind-mount problems (`SEC` L2668). Unverified.
28. HvSocket / AF_VSOCK on Linux, `Ookii.VmSockets`, `HyperV.NET`, and `eryph` guest services (`SEC` L2518–L2528, L2668, and related). Check existence and maintenance.
29. Microsoft's own C# sample for managing Gen2 VMs through WMI (`SEC` L2448 area). Verify.
30. Hyper-V dynamic memory range and Server Core minimum RAM of 512 MB (`SEC` L2720–L2748). Estimates, verify on hardware.
31. Nested WSL2 in a Hyper-V VM needs per-VM virtualization enablement (`SEC` L2546). Verify.

### Other platform and library facts

32. Roblox Studio has no official Linux client (`DD-Soul` L24; `SEC` L1460). Verify.
33. `luau-analyze` official; Selene community linter (`DD-Soul` L34; `SEC` L1492). Verify.
34. Avalonia XPF is the paid tier with the polished WebView (`DD-Win` L24; `SEC` L1749–L1770). Verify.
35. "Avalonia 12" and its rebuilt Android backend (`DD-Win` L28; `DD-Android` L40). Verify version and claims.
36. `Iciclecreek.Avalonia.Terminal`; EasyWindowsTerminalControl wraps Windows Terminal's rendering code (`DD-Win` L24–L25). Verify both.
37. Microsoft official .NET-for-Android plus Jetpack WindowManager guide (`DD-Android` L39). Verify.
38. WebTransport "Baseline" support March 2026 (`DD-Server` L37; `SEC` L1214). Verify.
39. Redis licensing history (SSPL/RSAL 2024, AGPLv3 2025) (`DD-Server` L36; `SEC` L1105). Verify.
40. Valkey "currently benchmarks ahead on throughput" (`DD-Server` L36; `SEC` L1121). Verify. Weakest claim in that row.
41. ParadeDB `pg_search` is AGPL and the AGPL does not reach SQL-only users (`DD-Const` L29; `SEC` L1240). Legal reasoning is the assistant's, not legal advice. Verify.
42. Postgres handles "Discord-style GC chat at your scale" (`DD-Server` L34, tagged RESEARCHED; `SEC` L1256). Source is an assistant claim.
43. Elixir Phoenix Channels as the tech "Discord is built on" (`DD-Server` L52; `SEC` L1071). Verify.
44. .NET Server GC predictable pause times in .NET 8+ (`SEC` L2440 area). Verify if relied on.
45. Intel NPU "13 TOPS" on the user's Core Ultra 9 285K (`DS[21]`, user claim). Verify if it goes into docs.
46. Kilo Code orchestrator mode, ensemble mode, and "built-in Claude/Gemini credits" (`DS[5]`). Verify if relevant to build-vs-buy.
47. Snapdragon QNN/SNPE, Gemini Nano on-device usage (`DS[12]`; `SEC` L1081). Verify.

---

## Consolidated checklist

### (a) Consistent across sources, well supported

- No master orchestrator; GCs are the coordination system; events are reactions only (`DD-Core` L19, L33; `GPT` L1058–L1063; `DS[7]`).
- Knowledge graph = Vault + Mood Board = The Constellation (`DD-Const` L11; `GPT` L1071).
- Email is external-only; agent-to-agent notifications go through GCs (`DD-Core` L37–L38; `DS[18]`).
- GCs are append-only, archivable, never purged (`DD-Core` L30; `SEC` H5).
- Fold/unfold context-switch philosophy for Android (`DD-Android` L15–L23; `DS[10]`–`DS[11]`).
- Avalonia for all clients (`DD-Win` L18; `SEC` H25–H28).
- ONNX Runtime as local inference base; Intel NPU/OpenVINO on Windows; Snapdragon/QNN on Android (`DS[13]`; `SEC` L755, L1125; `DD-Android` L38, L41). Except the QNN-on-Windows leftover in `DD-Win` L39.
- Valkey, WebSocket, Postgres, Monaco (`SEC` H18).
- GitHub Apps, not machine accounts, for agent identity (`DD-Server` L20–L21; `SEC` L921).
- Server = autonomous company; clients are thin-but-native doors; no web client (`DD-Core` L12; `GPT` L1237–L1263; `DS[11]`).
- Linux client = low priority, last in build order (`DD-Linux` L13–L15; `SEC` H28).

### (b) Contradicted, stale, or superseded

- Soul topology: Server Core + WSL2 single VM vs. two-VM-per-agent (`DD-Soul` L24–L26 vs. `SEC` H38, L2616–L2682).
- Linux host path in Soul (`DD-Soul` L25) vs. descoped (`SEC` H36, L2532).
- Server runs on NUC (`DD-Server` L11) vs. Windows host and 32 GB NUC (`SEC` H36, H40).
- GUI session pool kept (`DD-Soul` L32) vs. rejected (`SEC` H24).
- Protobuf DECIDED (`DD-Server` L37) vs. assistant-only proposal (`SEC` L1123, L2437).
- Backend options omit .NET (`DD-Server` L40–L54) vs. .NET frontrunner (`SEC` L2421–L2441).
- Resource Economy as currency (`DD-Core` L96, L123) vs. wallet rejected (`DS[18]`).
- Psych Ward "semi-serious" (`DD-Core` L84–L85) vs. user says not fun-only (`SEC` H5).
- Observatory PROPOSED (`DD-Core` L104) vs. "observability was a given" (`GPT` L2385).
- Windows NPU "DirectML vs QNN" (`DD-Win` L39) vs. Intel/OpenVINO (`DS[13]`; `SEC` L755).
- Android gestures contradictory (`DD-Android` L30).
- Vim `<Space>` leader vs. typed two-chord commands (`DD-Win` L47; `DS[26]`).
- Gemini "practically infinite" NIM (`DD-Server` L39) vs. "not truly infinite" in same line.
- Charles: junior (`DD-Core` L55) vs. missing from the founding five (`SEC` H1, H7).
- Email domain: `bazaar.firev.xx.kg` (`DD-Core` L37) vs. `firev.xx.kg` (`GPT` L1673; `DS[16]`) vs. `codexalchemist.firev.xx.kg` (`SEC` L427).
- Org creation: `DD-Server` L42 says "DECIDED (created)." Sources show only the planned name and a link (`SEC` L505, H13). Creation is not evidenced.
- Repo layout: `DD-Server` L41 two-repo vs. monorepo question still open in `SEC` H8.

### (c) Gaps and open questions

- Bootstrap plan (v0.001/0.5/1.0) and exit criteria are absent from the deep dives.
- Kanban semantics (dependency manager) absent.
- Local Co-Worker absent.
- Thinking GCs, visibility of agent reasoning absent.
- Deadlines, KPIs, QPIs, task queue absent.
- Built-in browser, captcha strategy, Playwright vs. Chromium, VNC — open.
- SMTP/IMAP design — open. No design exists in any source.
- Social accounts beyond Stack Overflow (separate website, Facebook) — `DS[18]`, absent.
- Trust ladder for external accounts — absent.
- Per-agent clones or worktrees — open (`DS[6]`).
- Human user-GCs — absent (`DS[9]`).
- Where the terminal runs (client-local PowerShell vs. agent VM) — open.
- Agent-to-VM control protocol (HvSocket, guest agent, SSH) — absent from deep dives.
- RAM/CPU and suspend/resume budget — absent from deep dives.
- Isolation mode — rejected in conversation, not recorded.
- Names: "Soul" confirmation; Bazaar domain and email; `Bazaar.Server.Windows` vs. `Bazaar.Server` naming; Codex Alchemist scope (game-dev only or general?).
- Constitution: who writes v1 (`DD-Core` L122) — open.
- Council size (6–12, undecided per `DS[8]`) — open.
- Gemini and Claude code vs. architect roles — open.
- Missing uploads: `AI Software Studio Concept.md` (77 KB, `DS[32]`) and earlier ChatGPT/Gemini exports referenced in `GPT` and `SEC`. Many named systems (Mirror, What-If, Time Machine, Talent Pool, Genetic Memory, Hive Mind, Psych Ward) trace there, so their user acceptance cannot be checked here.

### (d) Suggested revisions, by file

**Bazaar.md (`DD-Core`)**

1. Add a snapshot-date header; the family drifted after 2026-08-07 12:10 UTC.
2. Add a REJECTED list: workspace profiles (`GPT` L1025, L1141–L1165); knowledge graph as a feature (`GPT` L1022–L1023); fun-vs-substrate split (`SEC` L277–L282); agent wallet (`DS[18]`); Office Spaces (already REJECTED, `DD-Core` L99).
3. L33 Nervous System: keep PROPOSED; quote the constraint from `GPT` L1058–L1063; add "prefer talking over silent state changes" (`GPT` L1347–L1363).
4. L84–L85: change "semi-serious" to operational. Cite `SEC` H5 and `DS[18]`.
5. L96 and L123: rewrite Resource Economy as non-monetary quotas tied to Work-Life. Reference `DS[18]` for rejection of money.
6. L104: promote Observatory and observability to DECIDED core principle (`GPT` L2385, L2568–L2574).
7. L52–L56: verify model names (Fable); resolve Charles status and the founding-five roster (`SEC` H1, H7); fix `auto/*` tags after verification.
8. L37: pick one email domain; list the alternatives until the user chooses.
9. L11–L12 / `DD-Server` L11 / `DD-Win` L11 / `DD-Android` L11: replace ChatGPT slogans with the user's wording (`GPT` L2620–L2622).
10. Add: bootstrap plan (`SEC` H2–H4); Kanban dependency semantics (`GPT` L1107–L1137); Local Co-Worker (`DS[21]`–`DS[22]`); Thinking GCs (`DS[18]`); deadlines/KPIs/QPIs and task queue (`DS[18]`); user-GCs (`DS[9]`); trust ladder (`GPT` L3342–L3390).

**Bazaar.Server.md (`DD-Server`)**

1. L11: replace "Runs on your NUC" with "Windows host, Bazaar.Server; current hardware NUC with 32 GB RAM (`SEC` H36, H40)."
2. L37 Protobuf: downgrade to PROPOSED; note that .NET may replace it with a shared `Bazaar.Contracts` project (`SEC` L2437).
3. L40–L54: add .NET (C#) as a candidate with the Aug 7 recommendation and Orleans (`SEC` L2421–L2441). Note the Council-vote framing. Keep Node+Fastify as "DeepSeek default, never ratified" (DeepSeek dump line 231; `SEC` L1045).
4. L39 NIM: remove DECIDED and "practically infinite." Mark limits as unverified (`GEM` L314).
5. L38 RTK/Caveman: keep "verify empirically." Add the OmniRoute ToS risk line (see KQ5 #9).
6. L42 org: change "DECIDED (created)" to "DECIDED (name); creation not evidenced."
7. L41: note GitHub Projects v2 as the dev-team tracker, distinct from Bazaar Kanban (`SEC` L473).
8. Add: host-to-VM protocol (HvSocket guest agent, `SEC` L2518–L2528); RAM planning estimate with the "estimate, not measurement" caveat (`SEC` L2748–L2786).
9. L20 and L23 are sound; keep the verification flags.

**Bazaar.Soul.md (`DD-Soul`)**

1. L24: replace DECIDED "Server Core + WSL2, not two VMs" with the two-VM-per-agent design, marked "user-proposed, assistant-endorsed; confirm" (`SEC` H38, L2616–L2666).
2. L25: keep golden+child disks; add a second golden image (Linux). Remove the QEMU/KVM Linux-host path or mark it descoped (`SEC` H36, L2532).
3. L26: keep (Soul data outside VM).
4. L27: keep. Add host-mediated SMB as the shared workspace; reject shared block devices (`SEC` L2676).
5. L32: GUI pool → REJECTED per user (`SEC` H24). Keep Avalonia.Headless as the verification route.
6. L38–L40: keep licensing as RESEARCHED-verify. Note that the two-VM design still counts Windows VMs per agent; Linux VMs carry no Windows license.
7. L41: keep; add the reversal history (`SEC` L1398 vs. L1482).
8. L42: keep OPEN; add the Hyper-V-for-Windows-host decision (`SEC` L2501–L2510).
9. Add: Hyper-V isolation mode, investigated and deprioritized (`SEC` L2568–L2666).
10. Add: saved-state suspend/resume mapped to Work-Life shifts (`SEC` L2720–L2748).
11. Add: VM terminal pass-through (`wsl.exe` plus HvSocket) (`SEC` L2518–L2528).
12. Add: browser and captcha strategy as OPEN (`DS[30]`–`DS[31]`).

**Bazaar.Windows.md (`DD-Win`)**

1. L39: change "DirectML vs QNN" to "OpenVINO (Intel NPU) for local inference; DirectML as GPU fallback" (`DS[13]`; `SEC` L755).
2. L47: clarify whether `<Space>` is a leader and whether it conflicts with typed two-chord commands (`DS[26]`).
3. L12 and L46: add terminal executable types (pwsh, CMD, Visual Studio terminals) and OS default-handler mapping for .txt/.json (`DS[21]`–`DS[23]`).
4. Add: Local Co-Worker surface and inline autocomplete (`DS[22]`).
5. Add: terminal location distinction (client-local PowerShell vs. agent VM terminal). Open.
6. L24: verify XPF and WebView claims before DECIDED.

**Bazaar.Android.md (`DD-Android`)**

1. L30: rewrite the gesture spec as a single trigger model (no-move hold = voice; hold plus drag = radial) and mark it "user to confirm" (`DS[25]`–`DS[26]`).
2. L11: replace "Stay connected" with the user's "handheld development console" (`GPT` L2622).
3. L40 and L39: verify Avalonia 12 and .NET-Android claims.
4. Add: unfolded layout detail (right-side swipe panes, bottom tab for Mood Board with LLM and manual edit), from `DS[11]`.

**Bazaar.Constellation.md (`DD-Const`)**

1. L29: mark pg_search PROPOSED (assistant research); verify AGPL claim.
2. Add: dual Git view (solar plus standard GitHub tree) and per-AI avatar on commits (`DS[29]`; note GitHub App avatars, `DD-Server` L21).
3. Add: Mood Board editing by both LLM and human (`DS[11]`).

**Bazaar.Linux.md (`DD-Linux`)**

1. Keep the client as low priority. Add an explicit note that the Linux *host* is descoped (`SEC` H36), so "Linux" in this doc means client only.

**Cross-file**

- Add a rejected-decisions appendix and a snapshot date to every doc.
- Add a single agent roster table that marks Charles's status as open.
- Add a "user-confirmed vs. assistant-proposed" tag to every DECIDED item.
