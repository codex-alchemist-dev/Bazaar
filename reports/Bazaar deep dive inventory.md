# Aug 7 decisions outpace Bazaar's design documents

**No source contains a list titled "deep dives."** The word appears only as a per-system Vault document type in the Constellation design doc (ZIP Bazaar.Constellation.md §3, tagged DECIDED) and in an example vault tree in the DeepSeek export. The 28 named research threads in Section 1 are the closest equivalent: they were compiled from the three notes files and re-checked against the uploads. Bazaar (also called Codex Alchemist) is a planned platform for persistent AI agents that work as coworkers and coordinate through group chats, with no master orchestrator. Of the 28 threads, **10 are completed, 6 are in progress, 6 are proposed, 5 are unclear, and 1 rests on a single unverified source**. The live questions are the agent VM design and the server language. The newest source, a team conversation dated 2026-08-06 to 2026-08-07, departs from the zip design docs on both, and neither change has been ratified. Third-party facts, including licence histories, Windows licensing, NVIDIA NIM rate limits and OmniRoute features, remain unverified.

## Ten threads are closed, and the two live ones decide the build

The completed threads cluster in data and tooling. PostgreSQL with pgvector is decided, and standard indexing is judged enough for Discord-style GC chat ([ZIP Bazaar.Server.md §3]). Valkey was chosen over Redis on licence grounds, and the Redis licence history is a third-party claim that I did not verify. ParadeDB `pg_search` was chosen for search, with a flag that its AGPL licence needs a legal check before open-sourcing. GitHub Apps replaced machine accounts, but the claim that machine accounts are a ToS dead end is unverified. The client framework moved to Avalonia for headless testing, but the hands-on spike that would validate it has not been run ([SEC ~L1703–L1955]). Several "decided" items were introduced by the assistant rather than the founder: `pg_search`, LibGit2Sharp, Protobuf and .NET. The notes flag this as an ownership caveat, and I agree.

The two live threads are the backend language (T01) and the agent VM design (T02, T03). The zip server doc still lists the language as undecided across Bun, Go and Elixir/Phoenix ([ZIP Bazaar.Server.md §4]). The team conversation adds .NET as the frontrunner, citing Hyper-V management and LibGit2Sharp, and suggests a Council vote ([SEC ~L1193–L1209]). For VMs, the zip Soul doc still marks a single nested Windows Server Core plus WSL2 VM per agent as DECIDED ([ZIP Bazaar.Soul.md L24–L26]). The founder proposed two separate VMs per agent instead, and the assistant endorsed it ([SEC ~L2620–L2666]). I found no explicit adoption, so the status stays in progress.

| ID | Thread | Notes status | Revised status | Basis for revision |
|---|---|---|---|---|
| T01 | Server backend language | In progress | In progress | .NET frontrunner, not ratified; zip still undecided |
| T02 | Agent VM hypervisor and guest OS | In progress | In progress | Hyper-V chosen for Windows host; Cloud Hypervisor untested |
| T03 | VM topology (nested vs two VMs) | Proposed | Proposed, conflicts with zip DECIDED | Founder proposed two VMs; no explicit yes |
| T04 | Hyper-V isolation mode | Abandoned | **Proposed (deprioritize)** | Assistant's recommendation; founder did not confirm |
| T05 | Shared files between an agent's two VMs | Proposed | Proposed | Shared block disk rejected; SMB share suggested |
| T06 | Host-to-guest command passthrough | Unclear | Unclear | HvSocket suggested; no decision |
| T07 | RAM and CPU capacity | In progress | In progress | Estimates only, not measured; NUC RAM type unknown |
| T08 | Windows Server licensing | In progress | In progress | Datacenter likely; CAL question open |
| T09 | Server-to-client transport | In progress | In progress | Zip says WebSocket and Protobuf DECIDED; reopened |
| T10 | Primary database and vector store | Completed | Completed | Zip DECIDED; scale claim is an assistant research claim |
| T11 | Cache and pub/sub | Completed | Completed (decision); licence history unverified | Valkey chosen; Redis history third-party |
| T12 | Document search engine | Completed | Completed; AGPL check open | `pg_search` AGPL needs legal review |
| T13 | OmniRoute gateway | Completed | Completed (decision); claims unverified | Token-saving claims self-reported |
| T14 | Free-tier supply limits (NIM) | Completed | **Unverified** | Single Gemini answer; the "practically infinite" wording is self-contradictory |
| T15 | RTK and Caveman compression | Proposed | Proposed | No test result recorded |
| T16 | GitHub identity | Completed | Completed; one sub-claim untested | Issue creation on uninstalled repos untested |
| T17 | CI/CD engine | Proposed | Proposed | Concept agreed, no engine chosen |
| T18 | Build vs adopt for v0.001 coder | In progress | In progress | Claude Code as coder discussed; no formal decision |
| T19 | v0.001 scope and sequencing | Unclear | Unclear | Question posed; no answer recorded |
| T20 | Client UI framework | Completed | Completed (decision); spike open | Avalonia chosen; no hands-on spike run |
| T21 | Headless GUI-testing survey | Completed | Completed | Superseded by T20 |
| T22 | Editor and terminal embedding | Unclear | Unclear | Monaco decided; terminal library and paid tier open |
| T23 | Android feasibility and foldables | Completed | Completed (research); spike open | Foldable spike not run |
| T24 | Local NPU target by platform | Unclear | Unclear | Windows vendor conflicts (Intel vs unnamed) |
| T25 | Linux client feature scope | Proposed | Proposed | Low priority |
| T26 | GUI control and GUI session pool | Unclear | Unclear | Founder reportedly rejected a GUI pool; I did not re-check that line |
| T27 | Constellation rendering | Completed | Completed | Skia via Avalonia |
| T28 | Engine headless verification | Completed | Completed | Godot headless and Luau static analysis |

## Revisions the deep dives need, and which checks passed

The notes group the zip design docs as "deep dives," and the knowledge check compares them against the chats. I re-checked the highest-stakes claims against the sources, and most held. The zip Soul doc does mark the nested Windows Server Core plus WSL2 design as DECIDED, and it does keep a QEMU/KVM Linux-host path ([ZIP Bazaar.Soul.md L24–L25]). The zip server doc does rate Protobuf as DECIDED, and it does list the backend language as undecided without .NET ([ZIP Bazaar.Server.md L37, L40]). The NIM line in the zip server doc does say "practically infinite" next to roughly 40 requests per minute and 1,000 per day ([ZIP Bazaar.Server.md L39]). The only source for those rate figures is a Gemini answer that cites aggregator blogs and is itself hedged ("almost, but not quite") ([GEM L309–L314]). The chat record also shows the Protobuf choice first appearing as an assistant suggestion rather than a founder decision ([SEC ~L1123]).

Several revisions follow from these checks. The Soul doc should move the two-VM design into the record as proposed and assistant-endorsed, with the nested design marked superseded pending confirmation. The Server doc should downgrade Protobuf to proposed and add .NET as a candidate. The NIM line should drop DECIDED and the "practically infinite" wording, and mark the limits unverified. The Windows NPU line in the client doc names DirectML and QNN, but the founder said Windows uses the Intel NPU and does not need Snapdragon support ([DS entry 13]), so the Windows line needs correcting. The knowledge check also proposes a rejected-decisions list covering workspace profiles, the agent wallet, the fun-versus-substrate split and the GUI session pool. I agree with that list, but I did not re-check each rejection against its quoted line.

Two of my own corrections are needed. The overview says "Work-Life" has zero hits in all three chat exports, but the ChatGPT export has a heading named "Work-life simulation" ([GPT L339]), so the name's origin is less uncertain than the overview says. The notes' team-file line numbers also drift by a few lines against the original upload, so cite that file by turn and section where possible.

## What Bazaar is, and which parts are settled

The zip master doc frames Bazaar as "a persistent AI software studio, not an AI IDE with disposable subagents" ([ZIP Bazaar.md §1], DECIDED). Each agent has persistent identity, memory, specialties, and the ability to message, delegate, review, vote and learn. There is no master orchestrator. The conversation itself is the coordination system, which the zip calls the single biggest architectural commitment. Codex Alchemist is the first company hosted on the platform, and it is also the dev team behind the "Fireball Everything" name ([ZIP Bazaar.md §1]). The founder casts themselves as a coworker and tie-breaker rather than an operator. Bazaar itself is intended to be open source, while each company's install, with its agents' credentials and Vault contents, stays private.

The layering rules are the most useful part of the record, and most trace to the founder's own words in the ChatGPT and DeepSeek exports. The group chat is the authoritative event and decision record, and it is never purged, only archived ([SEC ~H5 turn; ZIP Bazaar.md §2]). Kanban is a dependency and scheduling board, so a later task waits for its prerequisite. The Constellation and the Vault are two views of one knowledge graph, which is why a separate knowledge-graph feature was rejected. Events may react to software activity, but they must never replace group chats. Decision logs record reasoning as well as outcomes, and the team file records a requirement that personality-driven judgment calls be auditable too. The founder also rejected a fun-versus-substrate split, saying the Soul's personality is part of what differentiates agents ([SEC ~L277–L282, per knowledge check]).

The named systems split cleanly. About a dozen are marked DECIDED: the Constellation (renamed from "Mood Board"), Reverse Gravity Layout, Git Solar System (naming only), the Vault, Kanban, the group chat, Sidewalk, the Soul System and Soul Passport, Work-Life Simulation, Council (not yet built), the Engine Integration API (direction only), and external-only email. About 27 are PROPOSED or unclear, including Skimmer Bouncer, the event bus ("Nervous System"), Psych Ward, the mandatory vent post, the Company Constitution, Resource Economy, Observatory, Identity Service and the Bazaar SDK. Office Spaces was rejected. The DECIDED tag is a design-doc status, not a ratification log, and the team file says decisions are not yet formally logged. Several names have uncertain origins. DeepSeek coined Sidewalk and the founder accepted it. The "Soul" name for the identity bundle is not confirmed by the founder, per the knowledge check. The Work-Life name appears in the ChatGPT heading, but the concept comes from the founder.

## The server, the clients and the agent roster

The server is meant to run the company. It runs all compute, Git, VM and model work, and it should keep running even when nobody connects for 48 hours ([ZIP Bazaar.Server.md §1] per the overview). The clients are thin renderers over a WebSocket state-sync link, with two exceptions: local NPU work for summaries and voice cleanup, and syncing a branch to a local folder for live playtesting. The founder's wording for each client is distinct. Windows is for building products and should feel like the best AI IDE. Android is a handheld development console for staying connected, not a mobile IDE. Linux is a low-priority client. There is no web interface, ever. The server has no NPU, and the notes record that summarization runs on clients.

The team file adds a scope change. The founder said, in effect, that the server will be Windows for now and Linux hosting may come later. The zip Soul doc still describes a QEMU/KVM Linux-host path, and I could not re-locate the exact quoted sentence by search, so I accept the notes' reading. The naming collision between the server and the Windows client, "Bazaar.Server.Windows" versus "Bazaar.Windows," is raised but not decided.

| Agent | Role per zip (Bazaar.md §3) | Model lane | Status |
|---|---|---|---|
| Claude | Senior architect and principal reviewer; also the hands-on v0.001 coder through Claude Code | Haiku, Sonnet, Opus, Fable (Fable unverified) | Founding member |
| Gemi | Research lead, planning and PM | No whitelist defined | Founding member |
| DeepSeek | Heavy implementation | V4 Flash and V4 Pro (one answer says "V3 cheapest") | Founding member |
| ChatGPT | Planning and systems input | No whitelist; live role "TBD" | Founding member |
| Charles | Boilerplate and scaffolding | auto/cheap only | Junior per zip; team file says not founding |
| Junior swarm | Specialists such as DB, UI, testing | Per-agent auto routing | Emergent, reputation-based |
| Founder | Product design, tie-breaker | Not a model | Human |

The routing rule is settled: there is no global auto model, and each agent owns a bounded whitelist, so a failure in one model family does not spread to others. The stated reason is token economics, since the founder wants other agents to spare Claude tokens. The routing tag names are inconsistent across sources (at least eight variants), so they must be verified against OmniRoute before anyone writes a config.

## Where the sources contradict each other

The most important contradictions are structural. The team file's two-VM design conflicts with the zip's DECIDED nested design, and the Linux-host status conflicts with the zip. The backend language conflicts across the zip, which omits .NET, and the team file, which favours it. The NUC is called the server in one place and a 32 GB production candidate in another, and the team file says the host is Windows. Smaller conflicts include the Windows NPU vendor (Intel per the founder, DirectML and QNN per the zip), the Charles roster status, and the Android gesture spec, where the zip and the founder's DeepSeek-era wording do not agree on hold-to-dictate versus radial menu. Three different per-agent email domains appear across sources, so that setting remains open, and I have omitted the addresses here.

## Conclusion

The next revision of the deep dives should be a dated snapshot, not a rolling edit. It should carry a rejected-decisions list, and it should separate the founder's statements from the assistant's recommendations, because the two are mixed under the same DECIDED tag. Four items need the founder's explicit yes or no before they are written as decided: the two-VM design, the .NET backend, the Windows-only server host, and Protobuf. Until then, the honest state is that the VM design and backend are in progress with a recommendation, not a decision.

The sources also carry a handling constraint. The team file says to keep the material confidential, and the notes folder, this report, and the uploads all sit inside the Bazaar git repository, which the project intends to open-source. None of it should go to a public branch, and the team file's own filename date (2026-10-25) is later than today's date, so the internal timestamps should be the reference.

**Source key.** ZIP: `/tmp/claude-0/-home-user-Bazaar/c63e21e3-9139-50bc-9f48-e82954f68a61/scratchpad/files-zip-3/` (byte-identical to `files-zip` and `files-zip-2`). SEC: `/root/.claude/uploads/c63e21e3-9139-50bc-9f48-e82954f68a61/bdf692f3-Secret_project_team_assembly-2026-10-25.md`. GPT: `.../c9f71868-chatgpt-conversation.md`. GEM: `.../7092de89-gemini-conversation-2026-08-01-01-30-44.md`. DS: `.../a4bc545f-deepseek-conversation.json`, by entry index. Notes: `/home/user/Bazaar/research_notes/Bazaar deep dive inventory/` (deep_dive_inventory.md, knowledge_check.md, project_overview.md). Line numbers for SEC are approximate, since the notes were drawn from a cleaned copy.
