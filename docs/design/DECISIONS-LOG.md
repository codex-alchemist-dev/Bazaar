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

## Round 5 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C26 Monaco hosting | **RESOLVED**: spike first with free routes only. If it does not work, the project may move off Avalonia to another open-source, modern, performant UI platform; if no .NET option exists, the owner is willing to migrate (language/platform) rather than ship a compromised client. | The Monaco-in-WebView spike becomes a GATE for the Avalonia decision as a whole, not just a Windows detail. A contingency survey of alternatives is in `docs/research/notes/Bazaar UI framework contingency/`. Docs 27, 28, 29, 30 note the gate; CLAUDE.md's "Clients: Avalonia" is DECIDED but conditional on this spike. |
| C27 Fold layout | **RESOLVED**: the server sends layout variants for all postures; the client switches locally with no round trip and caches the last profile for offline cold start. | Docs 02 and 29 align (doc 29's "phone arranges panes itself" is dropped); doc 27 owns the offline fallback shell. |
| C41 Push content | **RESOLVED**: bare wake hint, no chat id or sequence number; three wake kinds plus opt-in quota alerts; content is fetched over the authenticated connection. | Docs 02, 29 and 32 use the bare payload. |
| Remaining entries | **FOLLOW-UP**: the owner chose to walk through them in groups (C15, C16, C18-C22, C24, C25, C28-C30, C32-C36, C37-C45). | Continuing in groups of four. |

## Round 6 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C15 Aux transport | **RESOLVED**: WebSocket for realtime plus authenticated HTTP for blobs, search and pairing, specified in doc 02. | Doc 02 owns the HTTP surface; docs 18, 19, 20, 22, 26, 29, 32 reference it. |
| C16 Schema ownership | **RESOLVED**: each area's design doc owns its tables; doc 04 keeps conventions and core tables. | Doc 04 trims duplicates; the owner docs are canonical for their tables (C04 sets the runtime tables). |
| C18 Reader/actor | **RESOLVED (owner statement)**: agents must function as workers completely independently, with no human input, for the daily workflow; PRs and similar outward steps are a separate issue. **Claude's interpretation, to confirm:** the reader/actor split is the mechanism that makes this possible, so it ships in v1 for external input (email, issues, web, forums): reader sessions read untrusted content and hand over summaries; actor sessions never read external content directly and so are not tainted by it; the approval gate (C06) applies to outward high-impact actions (posting, sending, spending, repo push/merge), not to routine internal work. | Docs 15, 22, 36, 17, 12 change: the "approve everything after an external read" fallback is dropped; doc 15 designs the reader-session primitive and its hand-over format; docs 22 and 36 route inbound email and community content through reader sessions. The PR/merge approval path is designed separately (doc 21). |
| C19 GC types | **RESOLVED**: a GC type registry table with the v1 types seeded (new types are rows, not code changes). | Doc 01 defines the registry; docs 26 (ward, sandbox, brainstorm, sidewalk), 21 (PR), 01 (project, DM, thinking) register their types. |

## Round 7 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C20 Trust model | **RESOLVED**: two scales with clear roles: doc 15's enum for provenance (where content came from), doc 08's T0-T3 for memory promotion tiers; mapped at the boundary. | Docs 08 and 15 each state the mapping; the other docs use the term for their role. |
| C21 Approval tiers | **RESOLVED**: doc 15's ladder with doc 36's stricter community defaults (new posts, accounts and outreach need approval); operators can change thresholds. | Docs 14 and 36 conform to 15's tier names. |
| C24 Decision records | **RESOLVED**: `decisions/NNNN-slug.md` in the Vault, written only through the single `VaultWriter`. | Docs 10 and 18 conform; the Council service asks VaultWriter. |
| C22 Browser environment | **RESOLVED (owner correction)**: the browser is a full Chromium (like Claude's and Copilot's) and is one of the Passport's first customers: logged-in sessions are a core use. The register's "Passport-free" wording was wrong as a blanket rule. **Design direction:** two browser profiles in a full Chromium: (1) an anonymous profile for reading arbitrary web pages, with no credentials (this is the reader side of C18); (2) account profiles for allowlisted sites, using the Passport (the session-cookie profile allowed in C02, broker-injected tokens), detached when the session is hard-tainted. A third VM is added only if a spike shows it is needed; the default is profiles inside the agent's existing VMs. | Docs 14, 15, 16, 17, 22, 36 change: the browser is a Passport consumer, not a Passport-free island. Doc 16 Q about a third VM becomes "only if needed". A spike should confirm Chromium headed/headless in the VM pair and the profile isolation. |

## UI framework contingency (research note, 2026-10-25)

`docs/research/notes/Bazaar UI framework contingency/ui_framework_alternatives.md`: Monaco is explicitly unsupported on mobile (its own FAQ), so the Android client cannot depend on Monaco under any framework (diffs and editing on the phone need a native surface); no free, verified route puts Monaco in an Avalonia WebView; Avalonia's pricing page and its Accelerate docs conflict about whether a WebView control is free (needs a written answer from Avalonia); within .NET the most mature WebView host is MAUI Blazor Hybrid (no official Linux desktop target); Tauri hosts Monaco reliably on Windows and Linux but needs a "no web client" ruling; AvaloniaEdit is the native-editor fallback. Recommended spike order: Windows WebView2 Monaco, then Linux WebKitGTK, then a native Android surface, then AvaloniaEdit.

## Round 8 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C25 Owner merge | **RESOLVED (owner statement)**: Bazaar.Windows and Bazaar.Android contain a full replacement of GitHub's UI, so managing PRs and merging all happen inside the apps. The server performs the merge on the owner's signed approval from a client. **FOLLOW-UP:** the credential used (the owner's linked GitHub credential vs a dedicated owner GitHub App with merge rights; the App avoids storing a personal token). | Docs 21, 28, 29 specify the in-app PR review and merge flow and the squash-merge `Signed-off-by` line; doc 14 holds the credential. |
| C28 Done meaning | **RESOLVED**: for a card tied to a PR, Done = the PR is merged; reviewer approval is a visible flag. | Doc 12 changes (it used reviewer approval as Done). |
| C29 Model seam | **RESOLVED**: Bazaar's `ModelEvent` types are the seam, with an `IChatClient` adapter shipped in v1. | Docs 05, 06 conform; the adapter lets Microsoft libraries plug in. |
| C30 Ingress | **RESOLVED**: the home host accepts no inbound connections by default: it polls GitHub and mail providers; a tunnel or overlay is optional for real-time webhooks. | Docs 21, 22, 32 default to pull/poll; the Cloudflare Worker-posts-to-home design in the email note is an optional upgrade. |

## Round 9 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C32 Promotion and role management | **RESOLVED (owner statement)**: members, roles and similar are managed entirely by the group, even with no human present. Bigger changes need a vote from the boss only when the team vote is not already perfect (unanimous) or is tied. **FOLLOW-UP:** define "bigger changes" (candidates: new agent, promotion to senior, retirement, changing a model whitelist) and who counts as an eligible voter for "perfect". | Docs 07, 10, 11, 24: promotions, hiring (Talent Pool) and retirement run as Council votes; a unanimous vote passes without the owner; split or tied votes on bigger changes escalate to the owner. Doc 11's weights feed the debate, not an owner approval step. |
| C33 Usage table | **RESOLVED**: one gateway-owned `gw_usage` table with 5-minute and 1-hour cache-write columns; `usage_record` is dropped. | Docs 04, 05, 23. |
| C34 Loop guard trip | **RESOLVED**: the agents involved go to the Psych Ward on any trip (no separate 15-minute break). | Doc 26 stands; docs 06, 09 and 15 drop their own "mandatory walk" definitions in favour of the ward. |
| C35 Interrupts | **RESOLVED**: doc 24's per-agent interrupt stack (depth 2); an owner interrupt bypasses vacation only when tagged `emergency`. | Doc 06's flat `Interrupt` is replaced by doc 24's push contract; doc 09 adopts the `emergency` tag. |

## Round 10 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C36 Shared defaults | **RESOLVED**: one quota/limits table in doc 15 holds shared defaults; each subsystem doc owns its own specific limits. | Docs 02, 05, 06, 13, 22, 32, 36 reference doc 15's table for shared limits. |
| C37 CLAUDE.md gaps | **RESOLVED**: approve adding the new project names, tools and the verification rule as DECIDED: the server runs clean builds and tests and agent self-reports never count. | Applied to CLAUDE.md in the reconciliation pass. |
| C39 Embeddings | **RESOLVED**: one embedding profile per install, chosen at setup, stored as `halfvec`. | Docs 04 and 08 drop the fixed 1024-dimension `vector`. |
| C38 Company knowledge | **FOLLOW-UP (owner asked a clarifying question):** "Want memory itself, even per agent, always a part of vault?" The register entry was only about company-wide and cross-project knowledge, not each agent's private memory; being re-asked with clearer options. | Held until answered. |

## Round 11 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C38 Where knowledge and memory live | **RESOLVED (owner statement; verified in the original export)**: almost anything saved or written goes in the Vault (the Obsidian / Constellation-style knowledge graph). This includes each agent's persistent memory. Evidence: the DeepSeek export's Soul table lists "Memory Vault: Per-agent persistent memory" and the feature list says "Memory Vault: Obsidian-like docs with [[wiki-links]]"; agents "can access the project's Obsidian vault". **Principle (Claude's reading, to confirm):** the Vault holds every durable written artifact (agent memories, decisions, plans, research, summaries, the Constitution, Digital Twin pages, the Archive); PostgreSQL holds the append-only GC log, structured operational state (tasks, usage, scores, presence) and the search/embedding indexes over Vault files. | **Doc 08 (memory) must be redrafted**: memory notes are Markdown files (per-agent private area plus shared areas) written through the VaultWriter, with pgvector only as an index; docs 04, 07, 18, 25, 33 follow. New consequences to design: per-agent privacy inside one git repo needs an access layer in the Vault service (git itself has no ACLs; the owner can read all); every memory write is a commit, so VaultWriter needs batching and a queue; provenance/trust tags go in front matter; erasure (C01) for personal data inside Markdown files and git history needs its own answer (encrypted fields or a history-rewrite policy). |
| C42 Sign-in page | **RESOLVED**: doc 32's three-route scope (sign-in, pairing approval, recovery), off by default, showing no content. | CLAUDE.md wording about the sign-in page is corrected in the reconciliation pass. |
| C43 Presence | **RESOLVED**: eight labels (working, reviewing, thinking, in meeting, sleeping, researching, on vacation, helping another project) plus overlays such as "on break" and "waiting for approval". | Docs 09 and 23 adopt it; Bazaar.md §4 records the list. |
| C44 Migrations | **RESOLVED**: an explicit `migrate` command; the server at startup only checks the schema version and refuses to run on a mismatch. | Docs 03 and 04. |

## Round 12 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C45 Calendar | **RESOLVED**: doc 24 owns `calendar_event`; doc 09 consumes it. | Docs 09 and 24. |
| C40 Status endpoints | **RESOLVED**: doc 02 defines the wire/status format and protocol version, doc 03 the live/ready probes, doc 23 the contents of the status page. | Docs 02, 03, 23. |
| C10 follow-up: partition count | **RESOLVED**: the number of GC-log hash partitions is configurable in the setup wizard and in advanced settings. Doc 04 supplies the default. Changing it after data exists needs a planned data move (the setting is fully free only before the first data). | Doc 04 and the setup wizard (doc 27/32). |
| C25 follow-up: merge credential | **RESOLVED**: both supported: a dedicated owner GitHub App with merge rights, or the owner's linked GitHub credential. Chosen in the setup wizard and changeable in settings. | Docs 14, 21. |

## Round 13 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| Configurability rule | **RESOLVED (owner)**: wherever the design leaves a detail open (a default, limit, cadence, window, threshold, list, or policy choice), make it configurable by the Bazaar.Server operator instead of asking the owner to pick one value. Ship a safe default, expose it in the setup wizard and/or advanced settings, and record changes in the audit log. **Claude's reading of the limits (to confirm):** (1) DECIDED items and security invariants are not settings: hard-taint rules, key custody in the Identity Service, deny-by-default model policy, no subscription credentials, the owner's reading of all DMs. Settings may tighten these, never loosen below the shipped floor. (2) Shape choices that change a schema, the wire protocol or file layout (for example the edit cursor in doc 02, `gc_event` structure) cannot be runtime settings: they get one answer, and are listed as questions only when no safe default exists. (3) Settings that change legal or privacy exposure (erasure in Vault history, backup retention vs the erasure window, public-posting tiers) are operator choices among documented options, with the shipped default the most protective one and a "needs counsel" note. (4) Settings that affect other people (Council electorate, "bigger change" classes, Hive grants) are per-install policy that the operator sets and the Council can propose to change. | Every doc: each remaining owner question that is a plain value or policy choice becomes a setting with a default. Doc 03 owns a settings registry [PROPOSED]: typed key, default, scope (install, project, agent), who may change it, whether a change needs approval, and an audit entry on change. Docs 27 and 32 surface it in the setup wizard and advanced settings. |

## Round 14 (2026-10-25)

| Item | Decision | Effect on the docs |
|---|---|---|
| Versions | **RESOLVED (owner)**: SemVer `vMAJOR.MINOR.PATCH`; pre-release builds are `v0.x` with the placeholder codename "Beta"; v1.0.0 is a deliberate jump. Inspired by RESIBO V3. | `docs/VERSIONING.md`; `VersionPrefix` in `Directory.Build.props`; `Bazaar.Contracts.ProductVersion`. |
| Codenames | **RESOLVED (owner)**: majors named Z to A, theme "words that belong to both alchemy and the stars". Owner rejected Claude's first pick "Zodiac" and chose **"Zuhal"** (Arabic name of Saturn; Saturn is lead in alchemy) for v1.0.0; owner accepted Yliaster (v2), Xerion (v3) and Venus (v5) and rejected "Wandering Star" (v4); the owner rejected "Wolf" and accepted Wormwood (v4). | `docs/VERSIONING.md` section 2. |
| Channels | **RESOLVED (owner)**: LTSC, Stable, Beta, Alpha and an owner-only Latest-Commit channel; the operator picks per install. Stable, Beta and Alpha are immutable tags on `main`; LTSC is a real branch. | `docs/VERSIONING.md` section 4; doc 30. |
| Merging | **RESOLVED (owner)**: during full development Claude may merge PRs that pass the requirements (green CI on the head, no conflict, no blocking review). DCO sign-off for these merges is OPEN until before the first public release. | CLAUDE.md repo rules; `docs/VERSIONING.md` section 3. |
| Roadmap | **RESOLVED (owner)**: stay in Beta (v0.x) for a long while, through the owner's first projects on Bazaar; v1.0.0 only when truly ready, with no date. **PROPOSED**: Beta milestones v0.1 to v0.9, extendable. | `docs/ROADMAP.md`. |
| Provenance placement | **RESOLVED by Claude (schema shape, within C09)**: `source_trust`, `tainted_at_write`, `taint_sources`, `derived_from` live on `gc_event`; an edit is a `message_edit` event, so each version carries its editor's provenance. | Docs 01, 04, 15 aligned. |

## Round 15 (2026-10-25)

| Item | Decision | Effect on the docs |
|---|---|---|
| Wire format (S6, doc 02 question 2) | **RESOLVED (owner)**: JSON, `System.Text.Json` source generation, behind `IWireCodec`. A non-.NET client is a fallback only, used if .NET cannot do something needed, and then it would be Rust (serde reads the JSON; no schema tooling). No Protobuf tooling is added. `Bazaar.Contracts` stays plain records (camelCase, explicit nullables, no polymorphic or tuple payloads) so a binary or schema-first codec can be added later behind `IWireCodec` with a mapping layer. Evidence: `docs/spikes/S6.md` (part 1; Protobuf was 43% smaller and about 5x faster to decode but equal under gzip, and no JSON-specific failure was found). Android part 2 and the NUC are unmeasured; only a JSON-specific failure there reopens this. | Docs 02 and `Bazaar.Server.md` s3; CLAUDE.md; `docs/implementation-plan.md`. Unblocks W1 and W2. |

## Status after round 14

All 45 register entries (C01 to C45) are resolved. Still open as FOLLOW-UP: the definition of "bigger changes" that need the owner's vote (C32); confirmation of Claude's reading of C18 (reader/actor split as the autonomy mechanism); the C10 restore policy (sequence floor plus recovery entry, doc 33) is PROPOSED, not confirmed; erasure for personal data inside Markdown Vault files and git history (consequence of C38); Windows KEK custody vs recovery on new hardware (doc 33 C1). Reconciliation of the 36 docs, the deep-dive docs and CLAUDE.md with this log is the next step.
