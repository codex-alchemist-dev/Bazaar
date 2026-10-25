# Design decisions log

Owner answers to the cross-cutting conflicts in `CONFLICT-REGISTER.md`, in the order given. Each entry says what must change in the design docs; a reconciliation pass applies them. Status: **RESOLVED** (owner decided) or **FOLLOW-UP** (a detail still needs an answer).

## Round 1 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C03 VM topology | **RESOLVED** (earlier, owner 2026-10-24): every agent gets a Windows VM and a Linux VM. Spike S8 is for sizing only. | CLAUDE.md "Agent VMs" line already says so. Doc 16 Q13 and 35 Q4 follow it. |
| C09 GC log shape | **RESOLVED**: separate `gc_event` + `gc_message` + `gc_message_version` tables (doc 04's model), full copy per version. | Docs 01, 02, 15 and 22 must be redrafted to this model (they assumed one `entry` table with edits as entries). Trust columns and external-mail kinds need a home in 04's tables. |
| C10 Partitioning | **RESOLVED**: hash partitions from day 1 (by `gc_id`). | Doc 04 and Server.md §3 updated (no monthly partitioning). **FOLLOW-UP:** the partition count must be chosen before the first migration; restore policy (sequence floor + recovery entry, doc 33) is still PROPOSED, not yet confirmed. |
| C04 Runtime table ownership | **RESOLVED**: doc 06 owns agent, turn, step, lease and `inbox_item`; 07 owns Soul versions; 09 owns schedule and availability; 05 owns quota and model policy; 23 owns activity. Others add kinds, not tables. | Docs 03, 04, 07, 09, 13, 23, 24 drop their duplicate definitions. |
| C07 Pub/sub | **RESOLVED**: Postgres NOTIFY for GC fan-out; Valkey (Linux) / Garnet (Windows) for cache and presence. | Garnet stays in the first Windows host's dependencies, so spike S2 (StackExchange.Redis compatibility and persistence on Windows) gates the presence/cache work. Docs 02/03/04 keep NOTIFY for GCs; 11 and 23 may use the cache provider. |

## Round 2 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C01 Erasure | **RESOLVED**: personal fields are stored as per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone; structure, authorship and timestamps stay. Legal sufficiency still needs counsel. | The first migration (doc 04) needs a key table and encryptable `body`/personal fields; docs 01, 06, 08, 10, 22, 26, 32, 33 adopt it. Backup retention must equal the erasure window (doc 33). |
| C02 Mounted folder | **RESOLVED**: the per-agent folder holds metadata **plus a session-cookie profile**, detached automatically when the session is hard-tainted (doc 15's design). | This overrides the research and the recommended option (metadata only), so the owner has accepted the cookie-profile risk. Doc 14 changes: cookies are mountable, not only brokered; canary credentials and a separate share and OS account per agent (doc 15) become required. Doc 16 Q2's second share stays an explicit exception. |
| C05 Secret custody | **RESOLVED**: the Identity Service holds provider keys and GitHub App keys from the start; one server-side `ISecretStore` owned by doc 14; git pushes run host-side so tokens never enter a VM. | Docs 03 and 05 reduce their resolvers to bootstrap (KEK and database credential); 17 and 21 change pushes to host-side; 27 renames its client-side store interface. Windows KEK custody vs new-host recovery (doc 33 C1) is still open. |
| C06 Taint scope | **RESOLVED**: external content taints hard; agent text taints softly and propagates only from a tainted source session; Tier 2 actions are gated on hard taint; a fresh-session path with an approved summary exists. | Docs 12, 14, 15, 17, 21, 22, 36 align to this. Doc 36's "autonomous reply" still cannot work in the session that read the thread; a fresh-session reply path is needed. |

## Round 3 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C08 Waking | **RESOLVED**: an unmentioned message may wake an agent immediately when the Skimmer Bouncer scores it high, under a cap (doc 13's design). | Doc 06's rule "nothing else wakes an agent" changes: wake reasons gain "Skimmer-promoted". Doc 13 still starts every agent in shadow mode and promotes only after its miss rate is measured on labelled transcripts (13's gates), so the scored wake ships after that evidence. One digest, one table, one cadence (C04 gives 06 ownership). |
| C11 Thinking GCs | **RESOLVED**: a Thinking GC nested under each GC the agent works in (doc 01's model). Visibility: you always read; peers read on request; never injected into another agent's context; vote reasoning withheld until the reveal. | Doc 06's "each agent has one Thinking GC" changes to nested ones; 25 and 26 keep not publishing Thinking text. The Council exception stays. |
| C12 Git engine | **RESOLVED**: spike S23 first; LibGit2Sharp stays decided unless the spike shows it cannot do worktrees/merges/concurrency; hide the engine behind one `IGitProvider`. | Docs 17, 18, 21 adopt `IGitProvider`; CLI use is an alternative implementation, not the default. |
| C13 Fork-and-PR | **RESOLVED**: keep as decided (owner accepts the risk) even though GitHub's docs say the App must be installed on the source repo. | Doc 21 builds the flow as decided. Recorded for later: spike S22 is still the cheap way to find out whether it works; if the GitHub API refuses, the feature degrades to installed repos only. |

## Round 4 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C14 Claude lane | **RESOLVED**: the Claude member uses **both** the owner's Anthropic API key and the free tokens from FreeLLMAPI, by availability. | Supersedes "Anthropic API keys only" in CLAUDE.md. Caveats recorded, not re-asked: FreeLLMAPI maps Claude names to its own free pool and documents no pass-through of an operator's key, and its "Claude Code tiers" routing may involve subscription logins, which Anthropic's terms forbid to route; subscription (Pro/Max) logins stay off every route. Doc 05 keeps the `identity: native | pooled` label so usage and cost records show which source served each call. Docs 05, 07, 09, 35 adopt both-sources; Server.md item 11 and Bazaar.md §3 wording agree. |
| C17 Compute rule | **RESOLVED**: ratify the listed client-side exceptions: NPU inference (summaries, voice cleanup, prompt rewrite), the human's local terminal, the Roblox Studio bridge relay, and syncing the viewed branch to a local folder. Layout stays server-side. | Add the exceptions to CLAUDE.md and Bazaar.Server.md §1. Doc 19 stays server-side layout; docs 20, 28, 31 reference the ratified list. |
| C31 Terminal | **RESOLVED**: Bazaar.Server and Bazaar.Windows are completely separate apps. The server gives agents their own terminals inside their VMs (streamed by the server); Bazaar.Windows' terminal is a built-in replacement for Windows Terminal running locally. | Docs 17 and 30 separate the two; doc 28 owns the local terminal and the default-terminal role; doc 30's "terminal location" question is answered. |
| C23 Distribution | **RESOLVED**: OSI-only signed build: no paid Avalonia components, Postgres full-text search on Windows, free OSS signing route. | Docs 28, 30, 18: no Accelerate/Pro controls in the public build; signing route is SignPath; the Monaco hosting spike (C26) must use a free route or the HWND fallback. |
