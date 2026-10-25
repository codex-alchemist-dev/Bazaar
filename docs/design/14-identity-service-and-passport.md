# Identity Service and Soul Passport

Status: DRAFT for owner review (2026-10-25) · Owner questions: 12
Depends on: `docs/deep-dives/Bazaar.Soul.md`, `docs/deep-dives/Bazaar.Server.md` (no design docs yet for Server or Soul) · Used by: agent runtime, email subsystem, GitHub Apps integration, Bazaar.Server gateway (provider keys), Bazaar.Client.Windows (approvals)

## Purpose
The Soul Passport is the credential registry inside a Soul. The Identity Service is the only host-side component that holds, uses, or releases those credentials. Agents ask for access to a named service; they never hold the raw secret, and every use is decided by owner policy and written to an audit log.

## Decided inputs
- Passport contents: GitHub identity, emails, OAuth grants, browser sessions, API keys, SSH keys, site accounts, cookies, MFA metadata. Must live outside the agent VM disk. (`Bazaar.Soul.md` §1, §2)
- Owner (2026-10-24): Passport data may be mounted into the VM as a per-agent scoped folder. Security research advised against this; owner accepted the risk. (`Bazaar.Soul.md` §1)
- Agents sign up only where platform terms allow; otherwise a person creates the account. CAPTCHA, phone, ID and passkey steps are human-only. (`Bazaar.md` §2; `email_identity_accounts.md` KQ3)
- GitHub identity = GitHub Apps in org `codex-alchemist-dev`, not machine accounts. (`Bazaar.Server.md` §1)
- Claude lane: Anthropic API keys only; subscription credentials never go through the gateway. (`Bazaar.Server.md`, CLAUDE.md)
- Resource Economy is REJECTED; limits are quotas and the Work-Life Simulation, not money budgets. (CLAUDE.md)
- Passport and audit data must never be under a mounted path unless the owner approves a specific exception. (Conflict C1 below.)

## Design

**Components**
- **Passport store** [PROPOSED]: ciphertext rows in the host Postgres database, in a schema the agent VM cannot reach.
- **Identity Service** [PROPOSED]: a host process under a low-privilege service account. It is the only holder of decryption keys. It exposes a request API, a lease broker, and a token minter for GitHub Apps and OAuth.
- **Approval queue** [PROPOSED]: pending requests shown to the owner in the Windows dashboard, with a GC notice. Approvals are signed by a platform identity, never by an agent.
- **Audit log** [PROPOSED]: see Data and interfaces.

**Access flow** (`RequestAccess`) [PROPOSED]:
1. Account check: does a Passport entry exist for this agent and service? If not, return `NoAccount` and route to the owner (Tier 3 for creation, or Tier 2 if an agent-created account is allowed).
2. Session check: is there an active session or unexpired lease that covers the requested scope?
3. Policy check: owner policy maps (agent, service, action) to a tier.
4. Approval: Tier 0 and 1 proceed (Tier 1 notifies the owner after the fact); Tier 2 waits for a per-action owner approval, with no blanket "always allow"; Tier 3 is owner-only and the agent cannot start it.
5. Issue: return a lease (short TTL, one service, one scope) or a brokered result. Write an audit event first; if the audit write fails, nothing is issued.

**Action tiers** (from `email_identity_accounts.md`, mapped to capabilities in `agent_security_threat_model.md` §5): read; post; send; spend; create-account; grant-scope; merge; deploy. The server enforces the tier in the tool dispatcher. Prompts cannot change it. Sessions that ingested untrusted input (email, stranger issues, web) cannot call Tier 1+ actions without approval.

**Release modes** (in order of preference):
1. **Brokered call** [PROPOSED]: the Identity Service makes the upstream call and returns only the result (for example, a GitHub API response). No secret leaves the host.
2. **Injection** [PROPOSED]: a short-lived cookie or token is written into one browser session, for one service, for one lease.
3. **Mounted secret** [PROPOSED, owner exception]: the scoped folder holds metadata by default. Secret material may be mounted only for a credential class the owner names explicitly. Default: no class.

**Envelope encryption** [PROPOSED]
- Each Soul has a data key (DEK). Each secret is encrypted with the DEK (AEAD, unique nonce).
- DEKs are wrapped by a host key-encryption key (KEK). The KEK sits behind a provider interface, like the other OS-specific parts.
- Rotation re-wraps DEKs. It does not re-encrypt every row. KEK rotation is a re-wrap pass.
- Per-Soul DEKs make Soul deletion a crypto-shred (see Behaviour).

**KEK providers** (options; research findings in brackets)

| Option | Fit | Research finding |
|---|---|---|
| Windows DPAPI, machine scope | Default for the first host | Protects from other OS users and from a copied disk. Does not protect from administrators or code running as the service account. [verified, Microsoft Learn] |
| DPAPI-NG | Not needed for one host | AD and web-credential principals; overkill. [verified] |
| Windows Credential Manager | Poor fit | Per-user store, no structure, no audit. [verified] |
| OpenBao transit | Possible if self-hosted | MPL-2.0. Windows server support is not documented on the fetched page. [unverified] |
| age or SOPS | Possible for offline escrow | [memory, unverified] |
| Linux equivalent (for example, TPM-bound or systemd credentials) | Needed for the Linux provider | Not researched. |

**Key escrow** [PROPOSED]: the KEK has an offline sealed copy held by the owner. Losing it means every credential is rotated (`backup_dr_soul_portability.md` §4). A restore drill decrypts from the offline copy, not the live server.

**GitHub Apps** [PROPOSED]
- One App per agent (for example `claude-bazaar[bot]`). The App private key is stored like any secret.
- The Identity Service mints installation tokens by signing a JWT with the App key. Each token is narrowed to the repositories and permissions needed for one lease. Installation tokens expire after one hour (verified).
- Rate limits to plan for: 5,000 requests per hour baseline. Content-creating requests: 80 per minute and 500 per hour (secondary limits, verified).

**OAuth device flow** [PROPOSED]: the owner starts each device flow. The user code appears in a GC or the dashboard for the owner to confirm on the device. The Identity Service polls the token endpoint (RFC 8628). Refresh tokens are stored encrypted. Agents never receive them.

**TOTP** [PROPOSED]: seeds are encrypted in the Passport. Codes are generated on demand for Tier 1 and 2 services. Money and identity services are Tier 3 (owner-only). Recovery codes are never agent-readable. Passkeys stay with the owner or on a hardware key.

**Browser profiles** [PROPOSED]
- A per-agent persistent browser profile holds session tokens, so it is encrypted at rest and stored host-side.
- Browsing runs in a browser VM with no Passport mount (see C2). The broker injects cookies one service at a time and records each use.

**Audit log** [PROPOSED]
- Host-side, append-only, hash-chained. The agent has no write access; the service role has INSERT only.
- Events: lease issued or denied, decrypt, injection, GitHub token mint, egress request (domain, method, size), tool call with trust tag, approval, revocation, kill switch.
- The hash head is exported periodically to a location the host cannot overwrite.

**Kill switch** [PROPOSED]
- Per agent: revoke all leases and App tokens, stop the VM, mark presence offline, quarantine output.
- Global: stop all egress and all Tier 1+ tools.
- Both are tested on a schedule.

## Data and interfaces

```
-- Sketches only; not migrations.
soul_key(soul_id, key_id, wrapped_dek bytea, kek_id, created_at, retired_at)
credential(credential_id, soul_id, service, kind, handle, ciphertext bytea,
           nonce bytea, key_id, tier, status, created_at, rotated_at, expires_at)
lease(lease_id, agent_id, credential_id, scope, issued_at, expires_at, revoked_at)
approval(request_id, agent_id, service, action, tier, state, decided_by, decided_at)
browser_profile(profile_id, agent_id, ciphertext bytea, key_id, updated_at)
audit_event(seq bigint, prev_hash bytea, hash bytea, ts, actor, kind,
            subject, detail jsonb, trust_tags text[])
```

```csharp
// Sketch only.
record AccessRequest(AgentId Agent, ServiceId Service, string Action, string Purpose);
abstract record AccessDecision;
sealed record Granted(LeaseId Lease, DateTimeOffset ExpiresAt) : AccessDecision;
sealed record NeedsApproval(ApprovalId Request, Tier Tier) : AccessDecision;
sealed record HumanRequired(string Reason) : AccessDecision;
sealed record Denied(string Reason) : AccessDecision;

interface IIdentityService {
    Task<AccessDecision> RequestAccessAsync(AccessRequest request, CancellationToken ct);
    Task<BrokeredResult> CallAsync(LeaseId lease, BrokeredCall call, CancellationToken ct);
    Task RevokeAsync(RevocationScope scope, CancellationToken ct);
}
```

## Behaviour and edge cases
- **Audit first.** If the audit write fails, the Identity Service issues nothing.
- **Lease TTL** [PROPOSED]: 15 minutes for brokered tokens; GitHub installation tokens are capped by the one-hour App limit.
- **Approvals expire** after a short window (default 24 hours). An expired approval is not reused.
- **Revocation is immediate** for brokered tokens. For a GitHub installation token already minted, the risk window is up to one hour; this is accepted, and the threat model lists the revocation behaviour as unverified.
- **Master key loss:** every credential is re-issued. Soul memory and history survive.
- **Replay and duplicates:** approval and lease IDs are single-use for state changes.
- **Restart:** leases and approvals are persisted, so a restart does not silently widen access.
- **Quarantined agents** cannot request new leases. Their existing leases are revoked.
- **Secrets in output:** brokered results are scanned for credential patterns before they reach a GC or a Vault entry.

## Testing
- Real Postgres: the audit chain is intact after concurrent writes; a broken chain is detected; INSERT-only role cannot UPDATE or DELETE.
- Fake KEK provider and fake GitHub endpoint: token mint, scope narrowing, expiry.
- Policy table tests: every (tier, session taint) combination.
- Approval flow: Tier 2 blocks until approved; Tier 3 cannot be started by an agent; an agent cannot approve its own request.
- Crypto-shred: after a Soul's DEK is destroyed, its ciphertext is unreadable.
- Injection: a cookie for service A is never returned for service B.
- Kill switch: after the per-agent switch, no lease succeeds.
- Mounted folder: no file under the mount path matches a Passport or audit schema.
- Restore drill: decrypt from the offline KEK copy.

## Work packages
1. Schema and migrations for the tables above (real-Postgres test fixture).
2. Audit log: hash chain, INSERT-only role, export of the head.
3. KEK provider interface, with a DPAPI provider on Windows and a fake provider for tests.
4. DEK wrap and rotation; crypto-shred.
5. Policy engine: tier table and session taint.
6. Approval queue and approval records; the approval API for dashboard clients.
7. `RequestAccess` and lease store, with audit-first ordering.
8. GitHub App token minter (JWT signing, narrowed scopes), with fake GitHub HTTP.
9. OAuth device-flow runner.
10. TOTP code generator.
11. Browser profile store and cookie injection (depends on 1, 2, 4).
12. Kill switch and revocation scopes (depends on 7, 8).
13. Mounted-folder writer that enforces metadata-only content (depends on 1).

Dependencies: 1 before everything; 2 and 3 in parallel; 4 after 3; 5 and 7 after 1 and 2; 8 to 10 after 7; 11 to 13 last.

## Conflicts
- **C1. Mounted Passport vs "outside the VM disk".** `Bazaar.Soul.md` §1 says the Passport lives outside the VM disk; §2 mounts Soul data into the VM. The threat model's rule is: Passport and audit never under a mount. The owner's accepted exception (mounted Passport) is recorded in Decided inputs. This draft narrows it to metadata by default (release mode 3). It is still the risk the research advised against.
- **C2. Host-side browser profile vs sandboxing.** `email_identity_accounts.md` puts a persistent Playwright profile on the host. Browsing untrusted pages with session cookies on the host conflicts with the isolation goal. This draft moves browsing to a Passport-free VM (PROPOSED).
- **C3. Fork-and-PR flow.** `Bazaar.Server.md` says no upstream installation is needed. The GitHub fork docs say the App must be installed on the source account with access to the source repository. Installation tokens cannot reach repositories the installation was not granted. Issue creation on an uninstalled public repo rests on one community answer. Test on a throwaway public repo before the DECIDED flow is relied on.
- **C4. Erasure vs "never purged".** CLAUDE.md says GCs are never destroyed. Crypto-shredding of personal fields (`backup_dr_soul_portability.md` §6) conflicts with that rule, and backups keep old keys until expiry. Needs counsel.
- **C5. Gateway keys.** The threat model infers that agents must never see Anthropic or provider keys. The Bazaar.Server design does not say this explicitly. This draft treats it as required (inference).
- **C6. Identity Service status.** `Bazaar.Soul.md` lists it as PROPOSED/OPEN; the implementation plan puts it in Wave 3 and the owner has placed it in v1 scope. Treat this as planned, not settled, until the owner confirms.

## Questions for the owner
1. **Mounted slice contents.** Default: metadata only (service names, handles, expiry); no secret material mounted for any class. Alternative: name a credential class that may be mounted with a short TTL. Which, if any?
2. **Browser location.** Default: a Passport-free browser VM per agent, with the profile restored host-side per session. Alternative: browsing inside the agent's own VM with broker-injected tokens only. Which do you want?
3. **Fork-and-PR.** Default: keep the DECIDED flow and run the throwaway-repo test before any build. If the test fails, may we switch to direct branches on org-owned repos only?
4. **KEK on Windows.** Default: DPAPI machine scope. Alternative: OpenBao transit (after a Windows install test). Which?
5. **Offline KEK escrow.** Default: a sealed copy held by you. Should a second holder also exist?
6. **TOTP tiers.** Default: Tier 2 per use for ordinary services; Tier 3 for money or identity. Confirm, or name other services.
7. **Approval channel.** Default: approve in Bazaar.Client.Windows, with a GC notice. Alternative: GC-only approval. Which?
8. **Lease TTL.** Default: 15 minutes for brokered tokens; approvals expire after 24 hours. Other values?
9. **Audit anchor.** Default: export the hash head daily to a location the host cannot overwrite. Where should that be?
10. **Erasure.** Default: crypto-shred on Soul deletion, and personal fields in GCs under per-person keys, pending counsel. Confirm you accept the C4 conflict for now.
11. **Passkeys.** Default: owner-held only; never in the Passport. Confirm.
12. **Tier 1 sign-up whitelist.** Default: empty until each platform's terms are checked. Which services should be checked first?
