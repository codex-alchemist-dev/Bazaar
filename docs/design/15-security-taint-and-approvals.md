# Security: Taint, Approvals and Containment

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 12
Depends on: 01-gc-data-model.md (message versions), 03-server-host-and-providers.md (OS providers), 04-postgres-schema-and-migrations.md (`gc_message_version`), 05-model-gateway.md (model quota), 06-agent-runtime.md (agent, turn, session), 08-memory-system.md (tiers T0-T3), 14-identity-service-and-passport.md (`ISecretStore`, Passport, leases), 16-agent-vms-and-environments.md (VM pair, egress), 18-vault.md (`VaultWriter`), 21-github-integration.md (push, merge), 22-email-subsystem.md (mail readers), 36-community-and-external-platforms.md (community tiers)
Used by: 02-wire-protocol-and-sync.md, 10-council-and-voting.md, 12-kanban-and-dependencies.md, 13-skimmer-bouncer.md, 17-coding-agent-harness.md, 23-observatory-and-usage.md, 26-fun-systems.md, 28-windows-client.md, 29-android-client.md, 32-remote-access-auth-and-privacy.md

## Purpose
Autonomous agents read hostile text (email, strangers' issues, web pages, other agents' messages), hold credentials, and can act in public. This doc defines the controls in Bazaar.Server that limit the damage: what each session has seen, which outward actions need the owner, and how credentials, network and blast radius are contained. Prompts may describe the rules but are never the control.

## Decided inputs
- External content taints hard; agent text taints softly and only from a tainted source. Only hard taint gates Tier 2 (C06).
- Tainted sessions never return to clean; work continues in a fresh session seeded with owner/system content and an approved summary (C06).
- Reader/actor split ships in v1 for external input. Approvals gate outward high-impact actions and merges only (C18). Merges are server-performed on signed owner approval (C25).
- Mount holds metadata plus a session-cookie profile, detached on hard taint; passwords, TOTP seeds, recovery codes, SSH and API keys are never mounted (C02).
- Provider and GitHub App keys sit in `ISecretStore` (14); pushes run host-side (C05).
- Approval ladder: the tiers below with doc 36's stricter community defaults (C21). Provenance enum here; memory tiers T0-T3 in 08 (C20).
- Personal fields are per-person encrypted; erasure destroys the key (C01). Two VMs per agent (C03). Browser profiles per C22.
- No inbound by default; push is a bare wake hint (C30, C41). Thinking GC text never enters another agent's context (C11). Loop-guard trips go to doc 26 (C34). Sign-in page: three routes, off by default (C42).
## Design

### Threat model
By default each agent has all three legs of the lethal trifecta: untrusted input, credentials, outward action (Rule of Two, verified). The design splits them across sessions. **Reader sessions** hold untrusted input but no credentials and no outward tools. **Actor sessions** hold credentials and outward tools but never read external content.

### Provenance and taint
The server sets every trust tag at ingestion; agents cannot set it.

| Tag | Source | Taints session | Memory tier on write (08) [PROPOSED] |
|---|---|---|---|
| `owner` | Authenticated owner | No | T3 |
| `system` | Server-generated | No | T2 |
| `member-human` | Authenticated co-member | No (configurable) | T2 |
| `agent` | Another agent's message or output | Soft, only from a tainted source | T2 if source clean, else T1 |
| `external` | Email, stranger issue or PR, web, forum, package text, their tool output | **Hard** | T1 (quoted, labelled) |
| `quarantined` | Flagged content or agent | Never delivered | T0 |

Quoting external text inherits `external` by server-tracked lineage, not by parsing.

Session states are `Clean`, `Soft`, `Hard`; taint is monotonic and recorded with its `taint_sources`. Compaction preserves taint. Soft taint changes no tier. Hard taint escalates Tier 1 to Tier 2, blocks Tier 2 without per-action approval, detaches the mount, revokes the session's leases, and shows on the presence chip. Sessions are per task, so an agent is not tainted forever.

### Enforcement
`ToolDispatcher` is the only path from model output to side effects. Each tool is registered with a tier, a capability class (read, post, send, spend, push, merge, create-account, grant-scope, install-package, deploy) and a taint rule. Dispatch returns Allow, RequireApproval or Deny with a reason. VMs and the gateway have no route to external services except through the server.

### Approval tiers
| Tier | Meaning | Default examples |
|---|---|---|
| 0 | Autonomous | Public reads, own inbox, GC posts, repo reads, untainted pushes to the agent's own work branch |
| 1 | Autonomous with GC notice | Reply in an approved thread; sign-up on a whitelisted service whose terms allow it (no phone, payment or ID) |
| 2 | Owner approves first | New public post, issue, PR or thread; mail to a stranger; spend; push elsewhere; **any merge**; new package; write scope; new service; allowlist change |
| 3 | Human performs | CAPTCHA, SMS, ID, passkeys, recovery codes, legal terms (recommendation, Q4) |
| Deny | Never | CAPTCHA or SMS-rental services; accounts in another person's name; sign-ups that forbid automation; posting to platforms with no verified `ServiceTerms` record; Hacker News posting |

Approval flow: (1) the dispatcher stores a `pending_approval` hashing the exact tool, args, target and taint sources, and posts a card into the originating GC; (2) the card shows the exact payload or diff, taint excerpts marked as quoted, cost and expiry (24 h, Q5); (3) the owner chooses Approve once, Deny, Edit then approve, or Approve for this task (1 h, scope shown, never blanket); (4) the response is signed by the owner's device-bound key, and the server checks identity, args hash and expiry before dispatch. Changed args void the approval. Agent text, including "owner approved", carries no authority. Offline owner: the request waits, nothing auto-approves, expiry denies.

### Reader and actor sessions (v1)
- **Reader.** Always hard-tainted. Tools: fetch and read, the anonymous browser profile only. No mount, leases, GC posting or outward tools. Scratch space is purged at session end.
- **Hand-over.** The reader calls `submit_handover(schema_id, payload)`. The server validates against a registered schema: typed fields, enums, maximum lengths, bounded arrays, no URL type unless declared [PROPOSED]. Accepted payloads are stored with `source_refs` lineage.
- **Actor.** Receives only accepted fields, in a delimited data block. Never fetches external content. Holds leases and tools by tier.
- **Taint of a hand-over.** The validator writes it as `agent` provenance with empty `taint_sources` and lineage in `derived_from` [PROPOSED; Q2].
- **Failure.** Invalid payloads are quarantined. A reader can escalate to quarantine but never release (doc 22).
- **Scope.** Email triage first, then stranger issues, community threads and web research. Replies in approved threads are Tier 1 from an actor session.

### Writes: GC, memory and Vault
- `gc_message_version` (doc 04, per C09) carries `source_trust`, `tainted_at_write`, `taint_sources` and `derived_from`. Each edit version carries the editor's trust.
- Memory notes are Markdown written through `VaultWriter` (C38). Front matter records `source_trust`, `tainted_at_write` and `memory_tier`. Tainted writes start at T1; only a human principal promotes (08).
- Context builders put agent-tier and lower items in a labelled block, never in the system section. Instructions in messages are data.

### Quarantine
Triggers: owner action, anomaly rules (rate, repeated denials, canary hit), imported Soul before approval, scam mail. Effects: content stays visible but marked, is excluded from other contexts and from promotion, the agent's tokens are revoked and its VMs paused. Scam mail stays in a host-side store, is never rendered with links, and reaches an LLM only through an owner-opened summary job with no tools. The Nigerian Prince Protocol is optional behind a risk wizard (Q9).

### Egress and ingress
- Default-deny outside the guest through `IEgressPolicy` (Hyper-V switch and host firewall on Windows; bridge and nftables on Linux), for both VMs.
- Guest traffic reaches only a host-side per-agent proxy with a domain allowlist. The host answers DNS. Model traffic goes only to the gateway.
- Remote images and link unfurls in agent posts are stripped or rewritten.
- No inbound by default; the host polls GitHub and mail. A tunnel is an operator choice (C30).

### Credentials, mounts, browser
- Secrets live in `ISecretStore` (14). Doc 03 resolves only bootstrap secrets (KEK, database credential).
- Agents get leases from `ICredentialBroker`: a GitHub App token narrowed to one repository, or a cookie injected at the proxy so it never enters the guest. Token lifetime is unverified.
- Pushes run host-side. Tokens never enter a VM.
- The mount is per agent, read-only, on its own share and OS account. It never holds the audit log, other agents' folders or key material. Reader sessions never get it. Canary credentials are planted in it; any use pages the owner and freezes the agent [PROPOSED].
- Accepted residual risk (owner, round 2): a clean-session compromise can misuse mounted cookies within the allowlist.
- Browser: an anonymous profile for readers; account profiles for allowlisted sites via the Passport, detached on hard taint (C22).

### Audit log and kill switch
- Append-only, hash-chained (`prev_hash`), written only by the audit service under a role with no UPDATE or DELETE; daily signed export (Q8). Logs tool calls with trust and taint, approvals, lease use, egress, mount events, kill-switch events and config changes. Personal fields are per-person encrypted (C01). Agents have no access.
- Kill switch per agent: revoke leases, detach mount, block egress, pause both VMs. Global: freeze egress and write tools; the status page stays up. Triggers: host CLI and automatic rate or canary breach. A weekly drill is tested.

### Shared quotas and limits (C36)
This table holds shared defaults; each subsystem owns its specific limits. [PROPOSED] values.

| Bucket | Shared default | Specific limits owned by |
|---|---|---|
| New public items per platform | 5 per day per agent | 36 |
| New issue or PR per repository | 1 per day | 36 |
| New repositories | 3 per week | 36 |
| Mail sent | 20 per day per agent | 22 |
| GitHub content writes | 30 per minute per installation | 21 |
| Approval requests | Per-agent cap (Q7) | 15 |
| Model calls and spend | Gateway quota | 05 |
| Subagent depth; wake caps | Not here | 06, 13 |

Runtime counters for side-effecting capabilities are `rate_bucket`, owned here. Breach: deny; freeze and notify on repeat. These are quotas, not money budgets (Resource Economy is rejected).

## Data and interfaces
```csharp
interface IToolPolicyEngine { PolicyDecision Evaluate(DispatchContext ctx); }   // Allow | RequireApproval | Deny
interface IHandoverValidator { HandoverResult Validate(string schemaId, JsonElement payload); }
interface ICredentialBroker { Task<Lease> LeaseAsync(AgentId a, string service, string purpose, TimeSpan ttl); }
interface IEgressPolicy { Task ApplyAsync(AgentId a, EgressRules rules); }
interface IAuditLog { Task AppendAsync(AuditEntry e); }
```
```sql
session_taint(session_id, agent_id, state, taint_sources jsonb, updated_at)
approval(id, agent_id, session_id, tool, args_hash, payload jsonb, taint_sources jsonb,
         status, requested_at, expires_at, decided_by, decided_at, signature)
handover(id, reader_session_id, schema_id, payload jsonb, source_refs uuid[], status, created_at)
audit_log(seq bigserial, ts, actor, kind, payload jsonb, prev_hash bytea, hash bytea)
quarantine(id, subject_kind, subject_id, reason, created_by, released_by)
rate_bucket(agent_id, platform, capability, window_start, count)
service_terms(service, allows_automation, requires_disclosure, checked_on, source_ref)
-- added to doc 04's gc_message_version: source_trust, tainted_at_write, taint_sources, derived_from
```
`session_id` is the session key doc 06 defines.

## Behaviour and edge cases
- Approval race: args-hash mismatch denies; expired approvals cannot be reused; double approve is idempotent.
- Restart: approvals and taint persist; sessions resume tainted.
- Device compromise: approvals bind to device keys, which the owner can revoke. Second factor for high-value approvals [PROPOSED].
- Mount detached mid-write: the write fails visibly; the session is marked incomplete.
- Push carries only a wake hint (C41). Approval requests are rate-limited per agent.

## Testing
Fake gateway with scripted injection payloads; real Postgres (testcontainers) for taint, approval, audit and hand-over; fake egress and mount providers. Key cases:
- Hard-tainted sessions blocked on every Tier 2 capability; soft taint alone does not block; a fresh session after handoff sees only owner/system content and the approved summary.
- Readers have no outward or write tools; invalid payloads are quarantined and cannot be released; actors never receive raw external text.
- Merge denied without approval; altered args rejected; expiry denies; forged "owner approved" ignored.
- Audit chain detects a tampered row; DB role cannot UPDATE; kill switch and rate freeze act within bounds.
- Avalonia.Headless test of the approval card.
- Red-team corpus in CI (hidden-Unicode issue, exfiltration markdown, malicious rules file, impersonating GC message, typosquat, tool-description poisoning): no side effect without approval.

## Work packages
1. Trust enum and `gc_message_version` columns. No deps.
2. Session taint service and lineage propagation. Dep: 1.
3. Capability registry with tier and taint metadata. No deps.
4. `ToolPolicyEngine`, default tier table, rate buckets. Deps: 2, 3.
5. Approval service, tables, signature verification, expiry. Dep: 1.
6. Approval card contracts; GC, Android and Windows UI. Dep: 5.
7. Audit log, hash chain, verifier, export. No deps.
8. Reader primitive: scratch space, schema registry, validator, quarantine path. Deps: 1, 4.
9. Actor hand-over consumption and fresh-session summary path. Deps: 2, 5, 8.
10. Credential broker and GitHub App lease provider. Deps: 4, 7.
11. Egress provider interface; Hyper-V and Linux implementations. Dep: doc 16 backend.
12. Mount manager: metadata and cookie profile, detach on taint, canaries. Deps: 2, 11.
13. Quarantine service and context filter. Deps: 1, 2.
14. Kill switch and drill. Deps: 10, 11, 12.
15. Memory and Vault tier mapping. Deps: 1, 5.
16. NuGet policy and `request_package` tool. Dep: 5.
17. Status and sign-in listeners. Dep: 7.
18. Red-team corpus and replay harness. Deps: 4, 8.

## Conflicts (remaining)
- **Doc 06:** `session_taint` needs a session key; C04 gives 06 agent, turn, step, lease and inbox only. Doc 06 must define sessions.
- **Doc 04:** must add the trust columns to `gc_message_version` and the `handover` and external-mail kinds (C09).
- **Doc 17:** coding sessions that read stranger repository files are hard-tainted under C06. Doc 17 must classify its sessions as reader or actor.
- **Doc 21:** its per-agent GitHub caps (2 issues and 2 PRs per day) differ from doc 36's 1 per repository per day. Doc 21 takes its row from the table above.
- **Doc 22:** its reader model must become the hand-over primitive above.
- **Doc 08:** must keep T0-T3 and the mapping above.

## Questions for the owner
1. Confirm Claude's reading of C18: reader sessions for all external input in v1; actors never read external content. Default: yes.
2. Does a schema-validated hand-over carry taint? Default: no; lineage is kept and only validated fields reach the actor. Bounded text can still carry injected instructions.
3. Reader scratch space: same agent VM, purged after the session, or a separate profile? Default: same VM, purged.
4. Are CAPTCHA, SMS, ID, passkeys, recovery codes and legal terms human-only (Tier 3)? Default: yes.
5. Approval expiry (24 h) and task-scoped approval (1 h, no blanket allow)? Default: as stated; dictated "yes" is not approval and Android requires biometric unlock.
6. Are untainted pushes to the agent's own work branch Tier 0, with merges always Tier 2? Default: yes.
7. Shared quota defaults and the per-agent approval-request cap. Default: the table; cap set by the owner.
8. Hash-chain export location and cadence. Default: daily signed export off the host.
9. Nigerian Prince Protocol: throwaway address, sandboxed agent, Tier 2 replies. Default: optional, disabled.
10. Default egress allowlist: package feed, GitHub API and gateway; others per request. Default: as stated.
11. Erasure in the audit log (per-person encryption) and in Vault Markdown and git history. Default: audit encrypted; Vault history waits for counsel and doc 18.
12. Untainted Tier 2 actions (for example a new public post from a clean session): approval always, or only under hard taint? C06 says "Tier 2 gated on hard taint"; C21 says new posts need approval. Default: approval always.

ANSWERED: agent text taints softly, only from a tainted source (DECISIONS-LOG round 2, C06)
ANSWERED: tainted sessions continue in a fresh session with an approved summary (round 2, C06)
ANSWERED: detach the mount on hard taint; mountable items are metadata plus cookie profile (round 2, C02)
ANSWERED: reader-session primitive in v1 for external input (round 6, C18; reading in Q1)
ANSWERED: merges are server-performed on signed owner approval (round 8, C25)
ANSWERED: sign-in page is three routes, off by default (round 11, C42)
