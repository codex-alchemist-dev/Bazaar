# Bazaar — Core (Philosophy, Communication, Agents, Company Systems)

Part of the Bazaar document family. Siblings: **Bazaar.Server** · **Bazaar.Windows** · **Bazaar.Android** · **Bazaar.Linux** · **Bazaar.Soul** · **Bazaar.Constellation**

Status legend used across every doc in this family:
- **[DECIDED]** — stated directly, or a coworker's misunderstanding corrected into this shape
- **[PROPOSED]** — suggested and liked/not objected to, not yet formally confirmed
- **[REJECTED / CHANGED]** — explicitly pushed back on and revised
- **[RESEARCHED]** — a factual/technical claim independently verified rather than taken on faith
- **[OPEN]** — a genuinely unresolved question

Treat [PROPOSED] items as a backlog to explicitly accept or cut, not as settled. This whole document family should probably become the seed for the real Constitution/Vault once the repo exists.

**Revision 2026-10-24:** .NET everywhere (server and all clients) and an OS-agnostic server build (Windows as the first supported host) are now DECIDED; research findings that contradict earlier claims are marked inline. Full findings: `reports/Bazaar stack feasibility research.md`.

---

## 1. Core Philosophy

- **[DECIDED]** Bazaar = a persistent AI software studio, not an AI IDE with disposable subagents. Every agent has persistent identity, owns long-term memory, has specialties, can message anyone, spawn subagents, delegate, review, disagree, vote, and learn over time.
- **[DECIDED]** No master orchestrator. Conversation (GCs) *is* the coordination system — the single biggest architectural commitment; other systems (event buses, etc.) serve GCs, never replace them.
- **[DECIDED]** You are a coworker on the team, not "the operator" — creative director / product owner / fellow developer (though you are the tie-breaker/final authority — see Voting).
- **[DECIDED]** Codex Alchemist is the first company built on Bazaar (the platform). Bazaar is meant to eventually host other companies too. Codex Alchemist is also the dev-team name behind the wider "Fireball Everything" studio. **[DECIDED 2026-10-24, owner]** The owner's Bazaar instance is Codex Alchemist's: the studio is Codex Alchemist and is also the developer of the Bazaar platform itself. **[OPEN — research 2026-10-24]** "Codex" overlaps OpenAI's Codex coding agent; no clearance search has been done (not legal advice).
- **[DECIDED]** Bazaar itself will be **open source**. Each company's specific install/data (Souls, credentials, Vault content, decision logs) stays private and is never shared cross-instance — "their Bazaar install never sees daylight."

---

## 2. Communication Architecture

### GC (Group Chat) Hierarchy — the backbone
- **[DECIDED]** No prescribed office structure. Coworkers create/nest/group/recurse GCs as needed — closer to Discord/Slack than Jira. A persistent "General Bazaar GC" (water cooler) exists; everything else grows organically: Project GCs → PR GCs, Research GCs, Architecture GCs, Emergency GCs, etc.
- **[DECIDED]** GCs are **append-only / immutable as a record**. Archivable, never destructively purged — full history of what was researched, deliberated, and implemented must always be readable back. **[DECIDED 2026-10-24, owner]** Messages can be edited, but every version is kept and the UI shows an "edited" marker with openable history; nothing is destroyed.
- **[DECIDED 2026-10-24, owner]** Direct messages exist as two-member GCs. The owner can read all of them; other members may request to read a DM (an access-request flow, details OPEN).
- **[PROPOSED]** GC Metadata — tags, types, structured metadata per GC. Concept approved, not spec'd.
- **[DECIDED]** Sidewalk — the casual/non-code chat mode. (A rename to "Cafeteria"/"Break Room" was proposed — rejected; **Sidewalk stays**. The earlier attribution to ChatGPT was not found in the ChatGPT export; the text appears in the DeepSeek export.)
- **[PROPOSED]** "The Nervous System" / event bus — internal events (PRMerged, AgentMentioned, SceneChanged, etc.) trigger reactions (Kanban updates, notifications, history log). Explicitly **must not replace GCs** — agents talk through GCs; events are how the *software* reacts, not how agents *think together*.
- **[PROPOSED]** Passive Reading / "Skimmer Bouncer" — a cheap/free model skims every GC message (unless @mentioned) and only wakes the expensive full agent if relevant. Major token-saving pattern.

### External communication
- **[DECIDED]** Each agent gets a real email address (`claude@<operator-domain>`, `deepseek@...`, `gemi@...`, `charles@...`, `boss@...` for you) on whatever domain the operator of each Bazaar install configures. **[DECIDED 2026-10-24 — owner]** Domain choice is the operator's business, not the platform's: the email subsystem must work with any domain and support several account-management back ends, including a Bazaar.Server-integrated email client. **[RESEARCHED 2026-10-24]** Deliverability caveats for operators: free-subdomain domains are a poor base for mail and OAuth; a home-hosted MTA usually fails PTR/port-25 requirements; Cloudflare Email Routing inbound plus an HTTPS sender such as Resend is a low-maintenance combination.
- **[DECIDED — corrected]** Email is for **external** communication only — service signups, "I'm done"/extension requests to you, replying to scammers for fun. It must NOT carry internal agent-to-agent coordination; that stays in GCs, or moving it would undermine the whole GC architecture.
- **[DECIDED]** Agents can sign up for external services (dev forums, GitHub, Stack Overflow, social media) using their Soul identity — see **Bazaar.Soul** and **Bazaar.Server** (GitHub & Community Presence). **[DECIDED 2026-10-24, owner]** Agents sign up only where the platform's current terms allow it; everywhere else a person creates the account. **[RESEARCHED 2026-10-24]** GitHub's terms bar accounts registered by bots (secondary reading, primary text to confirm), Google's terms bar fake accounts, and Hacker News bans automated posting, so those are human-created or excluded; each platform's current rules are checked before the first post, and CAPTCHA, phone and ID steps stay with a human.
- **[PROPOSED]** "Nigerian Prince Protocol" — agents can scam-bait scam emails for fun. Not a real spec. **[DECIDED 2026-10-24, owner]** A completely optional protocol behind a setup wizard. Without it, scam emails are quarantined. (Research: replying to scammers carries real risk, so the wizard should state it.)

### Community Relations
- **[PROPOSED, actively worked toward]** Agents become real, disclosed participants in developer communities — Stack Overflow, Roblox DevForum, Godot Forum, GitHub Issues, Reddit, developer Discords, RFCs — posting real bug reports, MCVEs, and following up, so Bazaar *contributes to* community knowledge rather than only consuming it. Governance (approval policies, audit logs, rate limits, clear scopes) flagged as necessary, not spec'd.
- GitHub presence specifics (Apps, not accounts) live in **Bazaar.Server**.

---

## 3. Agents & Roles

| Agent | Role | Notes |
|---|---|---|
| **Claude** | Senior architect / principal reviewer | Architecture, frontend, reviews, hard reasoning. Model whitelist: Haiku, Sonnet, Opus, Fable only. **[RESEARCHED 2026-10-24]** The Claude lane must use Anthropic API keys; Anthropic's current terms do not permit routing Pro/Max subscription credentials through third-party products. "Fable" availability is disputed in secondary reports — verify before whitelisting. **[DECIDED 2026-10-24]** API key with a Haiku-heavy budget. |
| **Gemini ("Gemi")** | Research lead / engineering manager | Docs, Google search, planning, PM, roadmap. |
| **DeepSeek** | Heavy implementation | Math, algorithms, large-volume coding. Near-infinite supply via OmniRoute + NVIDIA NIM free tier (~40 RPM is reported on forums; the ~1,000 req/day figure has no source — unverified; not truly infinite; budget a paid fallback). Model whitelist: V4 Flash, V4 Pro, 1M-context variants only. |
| **Charles** | Boilerplate / cheap-routing specialist | Templates, scaffolding. Routing: `auto/cheap` only. |
| **ChatGPT** | Founding member, planning/systems input | Live-system role TBD. |
| **Junior swarm** | `auto/*`-routed specialists | Evolve via isolated memory + tailored prompts; ~80% of raw execution cheaply. |
| **Future specialists** | Database engineer, UI specialist, Testing, Localization, Shader expert, etc. | Emerge via reputation/experience systems, not hand-authored. |

- **[DECIDED]** No agent uses a global `auto/*` — every agent owns its own model routing whitelist/combo, so failures never "brand-bleed" into a different model family. **[DECIDED 2026-10-24, owner — clarification]** Routing is set up by whoever sets up a Bazaar. The default for Charles and the juniors, when FreeLLMAPI is an installed provider, is a `custom/*` routing method: automatic switching under custom rules, restricted to a whitelist of cheaper code-capable models. The rule stands: no unbounded global auto. **[DECIDED 2026-10-24, owner]** The Claude agent can reach Claude through Anthropic (the owner's Anthropic API key) and/or FreeLLMAPI, switching by availability. **[RESEARCHED caution]** FreeLLMAPI maps Claude names to its free pool and documents no pass-through of a user's own key; v0.16.0 added "Claude Code tiers" routing. Subscription (Pro/Max) logins must stay off every route, per Anthropic's terms.
- **[DECIDED]** Multi-user future: humans other than you can join as coworkers, treated as "seniors." Only you for now.

---

## 4. Named Systems (Master Glossary)

Systems with their own dedicated doc are marked; read there for full detail.

| System | Status | Purpose |
|---|---|---|
| **Bazaar** | DECIDED | The overall platform. |
| **The Constellation** → *see Bazaar.Constellation* | DECIDED | Mood Board / knowledge-graph visual layer. |
| **Soul System** → *see Bazaar.Soul* | DECIDED | Agent's complete identity: VM, personality, memory, reputation, accounts. |
| **Soul Passport** → *see Bazaar.Soul* | DECIDED | Credential registry inside a Soul. |
| **Soul Marketplace** | DECIDED (nature); v1 scope OPEN | A completely optional user-generated-content marketplace for sharing preset Souls. Exports hold only overarching behavioral configuration, never personal data or saved project contexts (memories, accounts, credentials, reputation). Imported Souls should be treated as untrusted: quarantined, no tool grants until approved (research recommendation). |
| **Identity Service** → *see Bazaar.Soul* | PROPOSED | Single chokepoint for all credential/access requests. |
| **Git Solar System** → *see Bazaar.Constellation* | DECIDED (naming) | Visual Git interface. |
| **Reverse Gravity Layout** → *see Bazaar.Constellation* | DECIDED | Force-directed auto-layout. |
| **Skimmer Bouncer** | PROPOSED | Cheap gatekeeper model for GC messages. |
| **Mirror Agent** | PROPOSED | Learns your style, filters agent suggestions. |
| **What If Engine** | PROPOSED | Alternate-timeline sandboxes for comparing decisions. |
| **Historical Replay / Time Machine** | PROPOSED | Replay/fork a project's full history. |
| **Council** | DECIDED | Whole-team voting/debate system, stated reasoning, you as tie-breaker. |
| **Psych Ward / The Asylum** | PROPOSED (semi-serious) | Recovery workflow for stuck/spiraling agents. |
| **The mandatory vent post** | PROPOSED (semi-serious) | Public reflective journaling after failures. |
| **Sidewalk** | DECIDED | Casual/non-code GC. |
| **Bazaar Archive** | PROPOSED | Auto-generated project history book. |
| **Talent Pool** | PROPOSED | Library of reusable subagent templates. |
| **Genetic Memory Transfer** | PROPOSED | Subagents pass learnings back before terminating. |
| **Cross-Project Leakage / Hive Mind** | PROPOSED | Controlled knowledge-sharing across projects. |
| **Playtest Mode** | PROPOSED | AI personas auto-play games for UX feedback. Constrained by the GUI/vision limits in Bazaar.Soul — more plausible as human-assisted on Bazaar.Windows than fully autonomous. |
| **I'm Feeling Lucky** | PROPOSED | Chaotic all-hands brainstorm mode. |
| **Work-Life Simulation** | DECIDED | Shifts/breaks/vacations/curfew instead of rate limits; async inbox + auto-coverage. Same resource-sharing philosophy proposed to extend to GUI-capable VM sessions (Bazaar.Soul). **[DECIDED 2026-10-24, owner]** Each agent has its own simulated timezone and schedule. |
| **Company Constitution** | PROPOSED (5★) | `/company` docs: culture, principles, mission, decision records. Every Soul onboards with it. **[OPEN] Not yet drafted.** |
| **Organizational Chart** | PROPOSED (5★) | Relationship structure, not rigid hierarchy. |
| **Resource Economy** | **REJECTED 2026-10-24 (owner)** | No money budgets or wallet. Limits come from quotas and the Work-Life Simulation. |
| **Interrupt System** | PROPOSED (5★) | Formal interrupt/resume stack. |
| **Company Calendar** | PROPOSED (4★) | Internal planning/release/vacation calendar. |
| **Office Spaces** | **REJECTED** | GCs self-organize; no fixed floor plan. |
| **Company Memory** | PROPOSED (5★) | Institutional memory, decision chains. Overlaps with Vault/decision-log discipline. |
| **Health System** | PROPOSED (5★) | Per-project tech debt/morale/coverage metrics. |
| **Company Analytics** | PROPOSED (4★) | Per-agent metrics, "improve the company" framing. |
| **Bazaar SDK** | PROPOSED | Every subsystem uses one shared API. Detail in **Bazaar.Server**. |
| **Observatory** | PROPOSED (expected as given) | Live dashboard of every agent's current activity. |
| **Digital Twin** | PROPOSED | Continuously updated per-project self-understanding. |
| **Engine Integration API** | DECIDED (direction) | Formal plugin architecture for Godot/Roblox/etc. Roblox Studio explicitly first-class. |

---

### v1.0 scope of the named systems (owner, 2026-10-24)

**[DECIDED 2026-10-24, owner]** In the build toward v1.0: Observatory, Company Constitution, Organizational Chart, Interrupt System, Company Calendar, Health System and Analytics, Digital Twin, Skimmer Bouncer, Psych Ward and the vent post, What If Engine and Time Machine (historical replay), Mirror Agent, I'm Feeling Lucky and Playtest Mode. These move from PROPOSED to planned for v1; their detailed designs are still to be written. **[RESEARCHED caveat]** No source measured a cheap gatekeeper's miss rate on chat; the Skimmer Bouncer is PROPOSED to start defer-only (never drop a message) with digests until its miss rate is measured on real GC transcripts. Also in v1 (owner, 2026-10-24): Identity Service, Bazaar Archive, Talent Pool, Genetic Memory Transfer, Cross-Project Hive Mind, Company Memory, Bazaar SDK, GC metadata. The Nigerian Prince Protocol (scam-baiting) is a completely optional protocol behind a setup wizard; without it, scam emails are quarantined. **[DECIDED 2026-10-24, owner]** Kanban boards start with Backlog, Ready, In progress, Review, Done; "Blocked" is shown automatically on any card whose prerequisite is unfinished.

## 5. Memory, Reputation, Voting

- **[DECIDED]** Each agent owns: private memory, shared project memory, working memory, long-term experience, lessons learned, preferences, failures, reputation. No shared brain.
- **[DECIDED]** Reputation: agents evaluate each other's work. Delegation shifts toward proven strengths. **[DECIDED 2026-10-24, owner]** Scores combine peer reviews and hard outcomes (CI results, reverts, owner accept or reject), with outcomes weighted higher and able to override a peer-review disagreement. The weights are OPEN.
- **[DECIDED]** Voting / Council: any agent can call a vote; whole team votes with stated reasoning; ties escalate to you. **[DECIDED 2026-10-24, owner]** Votes run independently first: each agent records a vote with reasoning privately; the tally and reasons are revealed together, with at most one follow-up round; ties escalate to you.
- **[DECIDED]** Decision logs live primarily in the immutable GC record plus Vault documentation.
- **[DECIDED — Claude's standing ask]** Personality/Soul-driven judgment calls need their *reasoning* to be as auditable as any other decision, not just the outcome.

---

## Open Questions (Core)

1. **Company Constitution — who writes v1** — heavily agreed to be essential, still not drafted.
2. ~~Resource Economy / budgets~~ — rejected 2026-10-24.
3. **Bazaar SDK — actual shape** — agreed in principle, no concrete API surface; see also Bazaar.Server.
