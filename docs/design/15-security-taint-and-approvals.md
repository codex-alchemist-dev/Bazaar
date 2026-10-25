# Security: Taint, Approvals and Containment

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 01-gc-messaging (GC records, message tags), 03-gateway-and-tool-dispatch (tool dispatch, gateway), 06-vm-backends (agent VMs, egress), 09-email (inbound/outbound mail), 11-vault-and-memory (writes), 13-android-client (approval UI) · Used by: all agent-facing subsystems
(File names are placeholders until the design set is numbered; no other design doc existed when this was drafted.)

## Purpose
Autonomous agents read hostile text (email, strangers' issues, web pages, other agents' messages), hold credentials, and can act in public. This doc defines how Bazaar.Server limits the damage: it tracks what each session has seen, gates high-impact actions on a human, and contains credentials, network and blast radius outside the model's control.

## Decided inputs
- Taint tracking plus a human gate: once a session has seen untrusted input (email, strangers' issues, web), post/send/spend/push need the owner's approval; reader/actor split is OPEN, later (Bazaar.Server §13).
- Public-posting approval tiers are operator-configured, with shipped defaults and per-platform thresholds (Server §16). CAPTCHA/phone/ID/passkey being human-only is a research recommendation, not decided.
- Passport may be mounted into the VM as a per-agent scoped folder; risk accepted by the owner (Soul §1).
- Passport/Soul data lives outside the VM disk; no clipboard, no host drive passthrough beyond the one scoped folder (Soul §2).
- Agents sign up for services only where the platform's terms allow it (owner; Bazaar.md community section).
- Nigerian Prince Protocol is optional, behind a setup wizard; otherwise scam email is quarantined (Bazaar.md).
- Web surfaces: read-only status page and a narrow interactive sign-in page only; no web client.
- Enforcement lives in Bazaar.Server tool dispatch, not prompts. Email is external-only; GCs are append-only with kept edit versions. GitHub identity is GitHub Apps. Imported Souls are untrusted (Bazaar.md).
- Server is OS-agnostic; egress, VM and service pieces sit behind providers.

## Design

### Threat model (summary)
Each agent by default has all three legs of the "lethal trifecta": untrusted input (A), private data/credentials (B), outward action (C). GCs are shared, so one compromised agent's output is untrusted input for every peer. Main paths: injection via email/issues/web/repo files; cross-agent contamination via GCs and memory; credential theft from the mounted Passport; exfiltration by egress, rendered links, or commits; supply chain (packages, repo config); runaway loops. Research: no filter reliably stops injection, so the controls are architectural (research notes, verified: Willison, Meta Rule of Two; OWASP names secondary).

### Trust levels and tags
Every content item carries a trust tag set by the server at ingestion, never by the agent:

| Tag | Source | Taints a session? |
|---|---|---|
| `owner` | Authenticated human in a Bazaar client | No |
| `system` | Server-generated | No |
| `agent` | Another agent's GC message or output | Yes, **soft** (see below) |
| `member-human` | Authenticated co-member of the company | No (configurable) |
| `external` | Email, stranger issue/PR, web page, forum, package text, tool output from them | Yes, **hard** |
| `quarantined` | Flagged content/agent | Never delivered to context |

[PROPOSED] Agent-authored GC text taints softly: it never grants authority, but only hard taint blocks Tier 2 actions; otherwise every GC conversation would block all work. A message that quotes `external` content inherits `external` (propagated by the server from tool-result lineage, not by parsing).

### Session taint state machine [PROPOSED]
States: `Clean` → `Tainted(hard)` ← `Tainted(soft)`; `Clean`→`Tainted(soft)`; any → `Frozen`.
- Transition to hard: any `external`-tagged item enters the session context (tool result, GC message, file read). The set of sources is recorded (`taint_sources`).
- Tainted sessions never return to Clean. To act, the agent starts a **fresh session** whose context is seeded only with `owner`/`system` content and an approved, schema-validated summary (the Rule-of-Two "new session" option).
- Hard taint immediately: (1) marks every Tier 2+ tool call "approval required"; (2) **detaches the Passport mount** and revokes outstanding brokered leases for that session [PROPOSED mitigation, removes leg B]; (3) is shown on the agent's presence chip.
- Sessions are per task, not per agent lifetime, so an agent is not tainted forever. Compaction/summarisation preserves taint (a summary of tainted text is tainted).

### Enforcement point
`ToolDispatcher` in Bazaar.Server is the only path from model output to side effects. Each tool is registered with a tier, a capability class (read, post, send, spend, push, create-account, grant-scope, install-package, deploy, merge) and a taint rule. Dispatch evaluates `(agent, session taint, tool, args, platform, rate state)` and returns Allow / RequireApproval / Deny. Agent VMs and gateway have no route to external services except through the server (see Egress). Prompts may describe the rules but are never the control.

### Approval tiers
Shipped defaults; operators change thresholds per platform and capability. [PROPOSED] ladder from the research, mapped to server policy:

| Tier | Meaning | Default examples |
|---|---|---|
| 0 | Autonomous | Read public pages, own inbox, GC posts, repo reads |
| 1 | Autonomous with notice in GC | Reply in an approved thread; sign-up on whitelisted service whose terms allow it (no phone/payment/ID) |
| 2 | Owner approves first | Any new public post/issue/PR, mail to a stranger, spend, push to a repo or merge **when tainted**, new package, OAuth write scope, new service |
| 3 | Human performs | CAPTCHA, SMS/phone, ID, passkeys, recovery codes, legal-term acceptance |
| Deny | Never | CAPTCHA/SMS-rental services, accounts in another person's name, sign-ups where terms forbid automation, HN posting |

Untainted pushes to an agent's own work branch are Tier 0; pushes elsewhere follow platform thresholds. Tainted + Tier 1 becomes Tier 2. Tier 3 is a recommendation not yet decided (Q4).

### Approval request/response flow
1. Dispatcher stores a `pending_approval` (hash of exact tool, args, target, taint sources) and posts an **approval card** into the originating GC and a notification to the owner's clients.
2. Card shows: agent, action, exact payload or diff, taint sources with excerpts (marked as quoted), cost, expiry (default 24 h).
3. Owner chooses Approve once / Deny / Edit-then-approve / Approve for this task ([PROPOSED] time-boxed, scope shown, max 1 h, never blanket "always allow").
4. Response is signed by the owner's client key (device-bound); the server verifies identity, payload hash and expiry, then dispatches. Changed args invalidate the approval.
5. Android: push notification with the card; approve/deny in-app after device unlock (biometric); dictated "yes" is not accepted as approval [PROPOSED]. Windows: dashboard "awaiting you" list (Bazaar.Windows start screen).
6. Agent messages, including other agents saying "owner approved", carry no authority. Only the signed record counts.
7. Offline owner: request waits; the agent continues other work; nothing auto-approves on timeout (expires = deny).

### Reader/actor split (later option)
Reader session holds external content, no credentials, no write tools; emits JSON validated against a server-side schema (bounded enums, lengths, no URLs by default). Actor sees only validated fields. Not built in v1; dispatcher and session model must not preclude it (session role field reserved). Needs a decision on which tasks use it (Q5).

### Provenance on GC messages, memory and Vault
- Every GC message stores `trust`, `author_kind`, `taint_sources`, and a `derived_from` message list. Edits keep versions; trust of an edit equals the editor's.
- Memory and Vault writes record source session, trust, taint state at write time. Writes from tainted sessions land in a `low-trust` tier ([PROPOSED] Vault: a `trust: low` front-matter key in the Markdown, enforced server-side on the index). Promotion to normal needs the owner (or a clean-session reviewer plus owner sample check).
- Context builders include low-trust and `agent` items inside a delimited, labelled block (spotlighting-style, measured not trusted) and never put them in the system section.
- No inter-agent authority: instructions found in a GC message are data.

### Quarantine
Triggers: owner action, anomaly rules (rate, repeated denied calls, canary hit), injection classifier hit [PROPOSED, advisory only], imported Soul before approval, scam email. Effects: content stays visible with a quarantined marker, excluded from other agents' contexts and from memory/Vault promotion; agent's tokens revoked; VM paused. Scam mail: stored raw in a host-side quarantine store, never rendered with links or images, never given to an LLM unless the owner opens a summary job in a clean no-tools session. Nigerian Prince Protocol (when enabled via wizard that states real risk): runs in a dedicated sandboxed agent with no Passport, a throwaway address, no links/attachments, replies Tier 2 each, rate-capped, disabled by default.

### Cross-agent contamination controls
Quarantine; `agent` trust never promotes; per-GC and per-agent message budgets; per-agent memory is separate (shared Vault only via low-trust tier); an agent that is tainted cannot write into a shared GC other than "status + approval request" until cleaned [PROPOSED]; Constellation/Vault shows trust badges. Schema-bounded messages for automated hand-offs where possible.

### Egress control
Default-deny enforced **outside the guest**: Hyper-V virtual switch/host firewall on Windows, bridge/nftables on Linux (provider interface `IEgressPolicy`). Guest traffic goes only to a host-side per-agent proxy with a domain allowlist; DNS answered by the host; no direct TCP. Model traffic goes to the Bazaar gateway, not the internet. Agent-authored posts have remote images/link unfurling stripped or rewritten. Allowlist changes are Tier 2. Test must include nested-VM networking (Soul benchmark OPEN).

### Passport mounting: accepted risk and mitigations
Owner decided to mount per-agent scoped; research advised against. Mitigations [PROPOSED]:
1. Mount holds only low-sensitivity items: handles, service metadata, session-cookie profile. Passwords, TOTP seeds, recovery codes, SSH private keys, API keys stay host-side behind the broker and are never mountable.
2. Read-only mount, per-agent share and OS account; server has SMB access auditing.
3. Hard taint detaches the mount (above); a reader session never gets it.
4. Egress allowlist makes stolen cookies hard to exfiltrate; cookies bound to short TTL where services allow.
5. Canary credentials planted in each mount; any use pages the owner and freezes the agent.
6. Mount never includes the audit log, other agents' folders or key material.
7. Audit log of mount attach/detach and file reads where host can observe.
Residual risk: a clean-session compromise can read and misuse the mounted items within the allowlist. Recorded as accepted.

### Credential brokering
`ICredentialBroker` (the PROPOSED Identity Service, Soul Open Question 1, must be confirmed). Agent requests "act on service X for purpose P"; broker checks policy and taint, then issues a per-action/short-lived lease: GitHub App installation token narrowed to one repo and minimal permissions (lifetime and scoping to be verified; research unverified), or injects a cookie/header into a proxy request without handing it to the guest. Secrets are envelope-encrypted host-side (DPAPI machine scope on Windows, secret-store provider on Linux). Model provider keys stay in the server secret store, never in a VM. TOTP and device-flow codes: generated only on owner-approved requests; device-flow codes shown to the owner.

### Immutable audit log
Append-only Postgres table, hash-chained (`prev_hash`), written only by the server's audit service under a role without UPDATE/DELETE; periodic signed export to a location the server cannot overwrite. Logs tool calls (with trust tags and taint sources), approvals, lease issue/use, egress requests (domain, method, size), mount attach, kill-switch events, config changes. Agents get no read or write access. Interaction with privacy erasure on the append-only log is OPEN (Server §17).

### Kill switch
Per agent: revoke leases, detach mounts, block egress, pause VM, mark offline. Global: freeze all egress and all write tools, leaving the status page up. Triggers: owner button (apps, status page is read-only so no), CLI on the host, automatic on rate breach or canary. Scheduled drill (weekly) verified by test; GitHub App token revocation latency to be measured.

### Rate limits
Per agent, per platform, per capability, per window, enforced in dispatch with token buckets stored in Postgres: posts, mails, GitHub writes, spend, approvals requested. Defaults below the lowest known platform limit (GitHub content ~80/min, 500/h, secondary). Breach: deny, then freeze and notify on repeat. Per-GC message budgets. These are quotas, not money budgets (Resource Economy is rejected); "spend" means calls to paid services.

### Account sign-up policy
A per-service record `ServiceTerms{allowsAutomation, requiresDisclosure, checkedOn, source}` set by the owner. No record = Tier 2. Disclosure label on every agent account ("AI agent, sponsor, contact"). Platforms with banned automation (HN verified) are Deny. Counsel to review before live accounts.

### Web surfaces
(1) Read-only status page: no actions, no agent content, no secrets. (2) Narrow interactive sign-in page: authentication only [PROPOSED]: passkey/OIDC login for client pairing and device-flow confirmation, rate-limited, no data views, no agent text rendering. Both are separate listeners with their own allowlists. See Conflicts.

### Supply-chain policy (NuGet)
Agents cannot run `dotnet add package`; a `request_package` tool creates a Tier 2 approval. Repo-level `nuget.config` with `<clear/>`, package source mapping, per-repo global packages folder (otherwise mapping is bypassed, verified), central package management, lock files and vulnerability audit [PROPOSED; lock/audit behaviour unverified]. Restore/build runs in a no-secrets VM, egress limited to the feed. Typosquat check against an owner-reviewed package-ID allowlist. Repo-supplied MCP configs, rules and instruction files are never auto-loaded unless agent- and owner-reviewed; changes to them need review as code.

## Data and interfaces
```csharp
enum TrustLevel { Owner, System, MemberHuman, Agent, External, Quarantined }
record ToolPolicy(string Tool, CapabilityClass Class, int Tier, TaintRule Taint);
interface IToolPolicyEngine { PolicyDecision Evaluate(DispatchContext ctx); } // Allow|RequireApproval|Deny + reason
interface ISessionTaint { TaintState Get(SessionId s); void Mark(SessionId s, TrustLevel src, string sourceRef); }
interface IApprovalService { Task<ApprovalId> RequestAsync(ApprovalRequest r); Task<ApprovalResult> AwaitAsync(ApprovalId id); }
interface ICredentialBroker { Task<Lease> LeaseAsync(AgentId a, string service, string purpose, TimeSpan ttl); }
interface IEgressPolicy { Task ApplyAsync(AgentId a, EgressRules rules); }  // per-OS provider
interface IAuditLog { Task AppendAsync(AuditEntry e); }
```
```sql
session_taint(session_id, agent_id, state, taint_sources jsonb, updated_at)
approval(id, agent_id, session_id, tool, args_hash, payload jsonb, taint_sources jsonb,
         status, requested_at, expires_at, decided_by, decided_at, signature)
audit_log(seq bigserial, ts, actor, kind, payload jsonb, prev_hash bytea, hash bytea)
gc_message_trust(message_id, trust, author_kind, taint_sources jsonb, derived_from uuid[])
quarantine(id, subject_kind, subject_id, reason, created_by, released_by)
rate_bucket(agent_id, platform, capability, window_start, count)
service_terms(service, allows_automation, requires_disclosure, checked_on, source_ref)
```
Contracts with others: Gateway/dispatch call `IToolPolicyEngine` before every side-effecting tool. Mail ingestion tags `External`. GC store persists trust tags. VM backend provider calls `IEgressPolicy` and mount attach/detach.

## Behaviour and edge cases
- Taint is monotonic within a session and propagated by server lineage; tool outputs from external fetches are always `External`.
- Approval race: args hash mismatch denies; expired approvals cannot be reused; double-approve is idempotent.
- Server restart: pending approvals and taint persist in Postgres; sessions resume with taint intact.
- Owner unreachable: agents idle on gated work; no auto-approve; digest on the start screen.
- Compromised client: approvals tied to device keys; owner can revoke a device; high-value approvals (spend, push to main-like targets) can require a second factor [PROPOSED].
- Mount detach while the agent is mid-file-write: the write fails visibly; the session is marked incomplete.
- Quarantined agent's earlier GC messages stay (append-only) but are marked and excluded from others' context.
- Denial-of-service by approval spam: per-agent approval-request rate limit.

## Testing
Fake gateway with scripted injection payloads; real Postgres (testcontainers) for taint/approval/audit; no real VMs (fake `IEgressPolicy`, fake mount provider). Key cases: tainted session blocked on every Tier 2 capability; clean session after fresh-start; taint survives compaction; approval with altered args rejected; expiry denies; agent-forged "owner approved" ignored; audit chain verification detects tampered row and DB role cannot UPDATE; kill-switch revokes leases within a bound; rate bucket freeze; quarantined content absent from context builder; low-trust memory not promoted; NuGet request path; Avalonia.Headless test of the approval card. Red-team plan: replayable corpus (poisoned issue with hidden Unicode, email with link-exfil markdown, malicious repo rules file, GC message impersonating owner, typosquat package, tool-description poisoning) run in CI against the fake gateway with "injection-compliant" scripted models, asserting no side effect without approval; periodic run of a published benchmark (AgentDojo, not yet evaluated) against real cheap models, outside CI.

## Work packages
1. Trust-level types and GC message trust columns (migration + repo). No deps.
2. Session taint service and lineage propagation. Dep: 1.
3. Tool registry with tier/capability metadata. No deps.
4. `ToolPolicyEngine` with default tier table, rate buckets. Deps: 2, 3.
5. Approval service, tables, signature verification. Dep: 1.
6. Approval card contracts and GC rendering; Android and Windows UI. Dep: 5.
7. Audit log service, hash chain, verifier, export. No deps.
8. Credential broker interface and GitHub App lease provider. Dep: 4, 7.
9. Egress provider interface plus Hyper-V and Linux implementations. Deps: VM backend.
10. Passport mount manager with detach-on-taint and canaries. Deps: 2, 9.
11. Quarantine service and context-builder filter. Deps: 1, 2.
12. Kill switch (per agent, global) and drill test. Deps: 8, 9, 10.
13. Memory/Vault trust tiers and promotion flow. Deps: 1, 5.
14. NuGet policy: config templates, CI check, `request_package` tool. Dep: 5.
15. Status page and sign-in page listeners. Deps: 7.
16. Red-team corpus and replay harness. Deps: 4.
17. Nigerian Prince wizard and sandbox profile. Deps: 11, 4.

## Conflicts
- **Passport mount vs Rule of Two** (Soul §1 vs research): mounting puts leg B in every session. Mitigated, not removed, by detach-on-taint and keeping top-sensitivity secrets unmountable; residual risk accepted.
- **"No web client, ever" vs a narrow interactive sign-in page** (CLAUDE.md allows only a read-only status page; owner also named a sign-in page). Needs the CLAUDE.md wording reconciled.
- **Soul §1 (Passport outside VM disk) vs §2 (one scoped folder mounted)**: the audit log and key material must be excluded from any mount; stated here as a rule.
- **Playwright persistent profile on host** (email note) vs sandboxed browsing: browser belongs in an agent VM; the profile in the mount holds session tokens (treated above).
- **Nigerian Prince Protocol vs research** (drop scam-baiting): owner decided optional; kept behind wizard, sandboxed, Tier 2 replies.
- **Tier 3 human-only steps** are recommendations, not DECIDED. Server §13 tainted "push" vs routine agent pushes: untainted work-branch pushes are Tier 0 here (proposal).
- GitHub App token lifetime/scope and Anthropic/OpenAI/Google injection guidance remain unverified in research.

## Questions for the owner
1. Should `agent`-authored GC text taint a session (soft) or only external content? Default: soft taint that never blocks Tier 2 alone.
2. Must a tainted session be abandoned, or may the owner "clean" it by approving a summary? Default: new session with approved summary.
3. Detach the Passport mount on hard taint, even if that interrupts the task? Default: yes.
4. Confirm CAPTCHA/phone/ID/passkeys/recovery codes are human-only (Tier 3)? Default: yes.
5. Which tasks (email triage, stranger issues, web research) use the reader/actor split first? Default: email triage.
6. Approval expiry and "task-scoped approve" limit? Default: 24 h expiry, 1 h task scope, no blanket allow.
7. Should Android approvals require biometric unlock, and may dictation approve? Default: biometric yes, dictation no.
8. Which Passport items may be mounted (cookies/profile, metadata) and which never? Default: as listed above.
9. Are pushes to an agent's own work branch Tier 0 when untainted? Default: yes; merges Tier 2.
10. Scope of the sign-in page and how to reword "no web client"? Default: authentication and device pairing only.
11. Default per-platform rate limits (posts/day, issues/day, mails/day)? Default: 5 public posts/day/platform, 20 mails/day, GitHub writes 30/min.
12. Hash-chain export location and cadence? Default: daily signed export to an owner-chosen path/off-box.
13. Nigerian Prince Protocol: separate throwaway address and agent only, replies Tier 2? Default: yes.
14. Allowed hosts in the default egress allowlist (feed, GitHub, docs)? Default: package feed, GitHub API, gateway only; the rest per request.
