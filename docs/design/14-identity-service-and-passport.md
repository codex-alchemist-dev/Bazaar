# Identity Service and Soul Passport

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 11
Depends on: `03-server-host-and-providers.md` (bootstrap secrets), `04-postgres-schema-and-migrations.md` (conventions, per-person GC keys), `15-security-taint-and-approvals.md` (taint, tiers, approvals, audit log), `16-agent-vms-and-environments.md` (VM pair, mount placement), `33-backup-dr-and-export.md`; deep-dives `Bazaar.Soul.md`, `Bazaar.Server.md` · Used by: `05-model-gateway.md`, `06-agent-runtime.md`, `07-soul-system.md`, `17-coding-agent-harness.md`, `21-github-integration.md`, `22-email-subsystem.md`, `28-windows-client.md` (approval UI)

## Purpose
The Soul Passport is the credential registry inside a Soul. The Identity Service is the only host-side component that holds, uses or releases those credentials, and it owns the single server-side secret store (`ISecretStore`). Agents ask for access to a named service. Raw secrets stay on the host, except the session-cookie profile the owner has explicitly allowed in the mounted folder. Every use is decided by owner policy and audited.

## Decided inputs
- **Passport contents** (`Bazaar.Soul.md` §1, §2): GitHub identity, emails, OAuth grants, browser sessions, API keys, SSH keys, site accounts, cookies, MFA metadata. The store lives outside the agent VM disk.
- **Mounted folder** (C02, round 2): metadata plus a session-cookie profile, detached on hard taint. Canary credentials and a separate share and OS account per agent are required (15). The owner accepted the risk the research advised against.
- **Secret custody** (C05, round 2): the Identity Service holds provider and GitHub App keys from the start. Git pushes run host-side.
- **Claude lane** (C14, round 4): the owner's Anthropic API key and FreeLLMAPI tokens, by availability. Subscription (Pro/Max) logins stay off every route.
- **Browser** (C22, round 7): full Chromium, a first-class Passport consumer. No blanket "Passport-free browser VM".
- **Taint and tiers** (C06, round 2; C18, round 6; C21, round 7): external content taints hard, agent text softly. Tier 2 is gated on hard taint. Tier names follow 15 (Tier 0 autonomous, 1 notice, 2 owner approves, 3 human performs, plus Deny). Reader sessions read external content and hand over summaries; actor sessions never read it.
- **Owner merge** (C25, rounds 8 and 12): merges happen in the apps on signed owner approval, with a dedicated owner GitHub App or the owner's linked credential, chosen in the setup wizard.
- **Erasure** (C01, round 2): personal fields are per-person encrypted; erasure destroys the key and appends a tombstone. The GC keys are defined in 04. The Passport keys below are a separate hierarchy.
- **GitHub identity** = GitHub Apps in org `codex-alchemist-dev` (`Bazaar.Server.md` §1). Fork-and-PR is built as decided (C13). Migrations use the explicit `migrate` command (C44).

## Design

**Components**
- **Passport store** [PROPOSED]: ciphertext rows in host Postgres, in a schema that agent-facing database roles cannot reach.
- **Identity Service** [PROPOSED]: a low-privilege host process. It is the only holder of decryption keys (through the KEK provider). It exposes `RequestAccess`, brokered calls, a credential-lease broker and a token minter.
- **`ISecretStore`** (C05): the one server-side store for every secret that is not a bootstrap value. It holds provider API keys (Anthropic, FreeLLMAPI, other gateway upstreams), GitHub App private keys, OAuth refresh tokens and owner-class credentials. Only the KEK reference and the database credential are bootstrap values, resolved by 03. Consumers are host-side: the gateway (05) and the git provider (`IGitProvider`, C12). No VM, agent tool or browser reads the store. [PROPOSED] Each read names a purpose that policy must allow.
- **Owner-class credentials:** the owner merge credential, in either form from C25. No agent lease can name an owner-class credential. The server performs merges only on signed owner approval (21, 28).
- **Approvals and audit** are owned by 15 (`approval`, `audit_log`). This doc calls them and defines no tables for either.

**Access flow** (`RequestAccess`) [PROPOSED]
1. **Account check:** is there a Passport entry for this agent and service? If not, return `HumanRequired` for creation. Agent-created accounts, if the owner allows them, are Tier 2 by default, and unverified platforms are Denied (15, 36; C21).
2. **Session check:** read the session's taint from 15.
3. **Policy:** 15's tier table maps (agent, service, action) to a tier.
4. **Approval:** Tier 0 proceeds. Tier 1 notifies the owner after the fact. Tier 2 waits for a per-action owner approval, with no blanket "always allow". Tier 3 is human-performed and the agent cannot start it. A hard-tainted session cannot obtain a Tier 2 lease. It must use the fresh-session path with an approved summary (15).
5. **Issue:** write the audit event first. If that write fails, nothing is issued. Then return a credential lease or a brokered result.

**Release modes** (in order of preference)
1. **Brokered call** [PROPOSED]: the Identity Service makes the upstream call and returns only the result. This is the default for provider keys and GitHub API calls.
2. **Injection** [PROPOSED]: a short-lived cookie or token is written into one browser session, for one service, for one lease.
3. **Mounted session-cookie profile** (owner decision, C02): the only mounted secret class. See Mounted folder.

**Mounted folder** (C02)
- Contents: (a) metadata (service names, handles, expiry); (b) the session-cookie profile for allowlisted account profiles, materialised for active sessions.
- Never mounted: API keys, App keys, OAuth refresh tokens, TOTP seeds, recovery codes, passkeys, DEKs, the Passport store, audit data.
- **Canary credentials** [PROPOSED]: decoy entries seeded in each folder. Any use is a detection event that 15 turns into hard taint.
- Detached automatically on hard taint (15's gate). Each agent has its own share and OS account (15).
- [PROPOSED] The cookie profile is encrypted at rest on the host and decrypted into the mount only while a session is active.

**Browser** (C22)
- Full Chromium inside the agent's existing VMs. No separate browser VM by default. Placement is decided in 16.
- **Anonymous profile:** no credentials, used to read arbitrary pages. This is the reader side of C18.
- **Account profiles:** allowlisted sites only. Cookies come from the mount or are injected by the broker. Both are detached on hard taint.
- [PROPOSED] The allowlist is set by owner policy. Profile isolation is proven by a spike (35).

**Envelope encryption** [PROPOSED]
- One data key (DEK) per Soul, with an installation-scope DEK for provider and App keys. Each secret is AEAD-encrypted with a unique nonce.
- DEKs are wrapped by a host key-encryption key (KEK) behind a provider interface.
- Rotation re-wraps DEKs; it does not re-encrypt every row. Soul deletion destroys that Soul's DEK (crypto-shred).
- GC per-person keys (C01) are separate keys defined in 04. They may use the same KEK provider.

| KEK option | Fit | Research finding |
|---|---|---|
| Windows DPAPI, machine scope | Default for the first host | Protects from other OS users and a copied disk, not from administrators. [verified, Microsoft Learn] |
| OpenBao transit | Possible if self-hosted | Windows server support not documented on the fetched page. [unverified] |
| Linux (TPM-bound or systemd credentials) | Needed for the Linux provider | Not researched. |

**Key escrow** [PROPOSED]: a sealed offline KEK copy held by the owner. Losing it means every credential is re-issued. The restore drill decrypts from that copy.

**GitHub Apps** [PROPOSED]
- One App per agent. Its private key is stored in `ISecretStore`.
- Installation tokens are minted host-side by signing a JWT. Each token is narrowed to the repositories and permissions of one lease.
- Token lifetime: one hour per GitHub's documentation. **Unverified** here; 21 C3 tracks it and no revocation claim relies on it until checked.
- Rate limits (verified): 5,000 requests per hour baseline; content-creating requests are limited to 80 per minute and 500 per hour.
- Git pushes use a minted token inside the host-side git provider. The token never enters a VM.
- Fork-and-PR is built as decided (C13). If GitHub refuses the fork call, the feature degrades to installed repositories only (21).

**Owner merge credential** (C25) [PROPOSED]: either a dedicated owner GitHub App with merge rights, or the owner's linked GitHub user token from device flow. The setup wizard chooses it and settings can change it. Either form is stored as owner-class in `ISecretStore`.

**OAuth device flow** [PROPOSED]: the owner starts each flow. The user code appears in a GC or the dashboard for confirmation on the device. The Identity Service polls the token endpoint (RFC 8628). Refresh tokens are stored encrypted. Agents never receive them.

**TOTP** [PROPOSED]: seeds are encrypted in the Passport. Codes are generated on demand for allowed services. Recovery codes are never agent-readable. Passkeys stay with the owner or on a hardware key.

**Kill switch** [PROPOSED]
- Per agent: revoke all credential leases and App tokens, stop the agent's VMs, mark presence offline, quarantine output.
- Global: stop all egress and all Tier 1+ tools.
- Both are tested on a schedule.

## Data and interfaces

```
-- Sketches only; not migrations. Migrations are applied by `migrate` (C44).
soul_dek(scope, dek_id, wrapped_dek bytea, kek_id, created_at, retired_at)
   -- scope = soul_id, or 'install' for provider and App keys
credential(credential_id, soul_id null, service, kind, handle, ciphertext bytea,
           nonce bytea, dek_id, tier, status, created_at, rotated_at, expires_at)
   -- kind: provider_key | app_key | owner | oauth | cookie | ssh | totp | recovery
credential_lease(lease_id, agent_id, credential_id, scope, issued_at, expires_at, revoked_at)
browser_profile(profile_id, agent_id, profile_kind, ciphertext bytea, dek_id, updated_at)
```

Renamed or removed for consistency: `soul_key` is now `soul_dek`, to keep it apart from 04's GC keys. `lease` is now `credential_lease`, because 06 owns the runtime `lease` (C04). `audit_event` and `approval` are removed; they are 15's (C16).

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
- **Audit first:** the event is written to 15's `audit_log` before any lease is issued.
- **Lease TTL** [PROPOSED]: 15 minutes for brokered credentials. GitHub tokens are capped by their own lifetime (unverified).
- **Approvals** expire after 24 hours [PROPOSED]; an expired approval is never reused.
- **Revocation** is immediate for brokered calls and leases. A minted GitHub token stays valid until it expires (up to one hour, unverified); this is accepted.
- **Hard taint:** the mount and account browser profiles detach, and Tier 2 leases are refused. Canary use is a 15 detection event that applies hard taint (Question 10).
- **Erasure:** Soul deletion destroys its DEK; a tombstone is appended. Backups keep old DEKs until the erasure window ends (33). Legal sufficiency needs counsel.
- **Master-key loss:** every credential is re-issued; Soul memory and history survive.
- **Replay, restart, quarantine:** approval and lease IDs are single-use. Leases and approvals persist across restarts, so no silent widening. Quarantined agents cannot request leases, and their existing leases are revoked.
- **Reader sessions** are tool-less and request no leases [PROPOSED].
- **Secrets in output:** brokered results are scanned for credential patterns before they reach a GC or the Vault.

## Testing
- **Real Postgres:** agent-facing roles cannot read the Passport schema. Crypto-shred leaves ciphertext unreadable. The restore drill decrypts from the offline KEK copy.
- **Fakes:** KEK provider and GitHub endpoint cover token mint, scope narrowing, expiry, and a push token that never reaches a VM path.
- **`ISecretStore`:** an agent-facing caller asking for a provider or App key is denied; host-side callers succeed.
- **Policy and approvals:** every (tier, taint) combination via 15's table. A hard-tainted session cannot obtain a Tier 2 lease. Tier 2 blocks until approved. An agent cannot approve its own request.
- **Injection:** a cookie for service A is never returned for service B.
- **Mounted folder:** only metadata and the cookie profile. No file matches a key, DEK, Passport or audit schema. Canaries are present. The mount detaches on simulated hard taint.
- **Browser:** the anonymous profile holds no cookies; account cookies go only to allowlisted sites.
- **Audit first:** a failed audit write yields no lease. **Kill switch:** after the per-agent switch, no lease succeeds.

## Work packages
1. Schema and migrations for `soul_dek`, `credential`, `credential_lease`, `browser_profile` (real-Postgres fixture).
2. KEK provider interface, DPAPI provider on Windows, fake provider for tests.
3. DEK wrap, rotation and crypto-shred (after 2).
4. `ISecretStore` with bootstrap separation and purpose checks (after 1, 2).
5. `RequestAccess` and credential-lease store, audit-first (after 1, 15's policy and audit).
6. GitHub App token minter and host-side push token (after 4, 5).
7. OAuth device-flow runner, including owner-class credentials (after 4).
8. TOTP generator (after 5).
9. Browser profile store, cookie materialisation, injection, canary seeding (after 1, 3, 5).
10. Mounted-folder writer enforcing the allowed-class list (after 9).
11. Kill switch and revocation scopes (after 5, 6).

## Conflicts
- **Windows KEK custody vs recovery on new hardware** (33 C1, open): DPAPI machine scope needs a second master-key copy to recover. Question 1.
- **Erasure legal sufficiency** (C01): key destruction is not yet validated by counsel.
- **GitHub App token lifetime** is unverified (21 C3).
- **Cookie profile in backups** (for 33): 33 snapshots Soul folders, and the mount holds cookies. Backups must exclude the mount, or hold it under the Passport's DEK and retention rule.
- **Runtime `lease`** (for 06): C04 gives 06 the runtime `lease`. This doc uses `credential_lease`; 06 must not use credential leases for work claims.
- **Canary detection and per-agent share and OS account** (for 15): 15 must define the detection event that canary use raises.
- **Already decided, for other docs:** 16 and 17 drop any blanket Passport-free browser VM (C22). 16 Q2's second share stays an explicit exception (C02). 03 and 05 reduce their resolvers to bootstrap values (C05). 27 renames its client store interface (C05).

## Questions for the owner
ANSWERED: the mounted folder holds metadata plus a session-cookie profile, detached on hard taint (DECISIONS-LOG round 2).
ANSWERED: the browser is full Chromium, with anonymous and account profiles inside the existing VMs (DECISIONS-LOG round 7).
ANSWERED: fork-and-PR stays as decided; if GitHub refuses, the feature uses installed repositories only (DECISIONS-LOG round 3).
ANSWERED: erasure uses per-person keys and tombstones; legal sufficiency still needs counsel (DECISIONS-LOG round 2).

1. **KEK on Windows.** Default: DPAPI machine scope, with the recovery-on-new-hardware gap in 33 C1 accepted for now. Alternative: OpenBao transit, after a Windows install test. Which, and where should the recovery copy live?
2. **Offline KEK escrow.** Default: a sealed copy held by you. Should a second holder also exist?
3. **TOTP tiers.** Default: Tier 2 per use for ordinary services; Tier 3 for money or identity. Confirm, or name other services.
4. **Approval channel.** Default: approve in Bazaar.Client.Windows, with a GC notice. Alternative: GC-only approval. Which?
5. **Lease TTL.** Default: 15 minutes for brokered credentials; approvals expire after 24 hours. Other values?
6. **Audit anchor.** Default: export the audit hash head daily to a location the host cannot overwrite. This is 15's audit log. Where should the anchor be?
7. **Passkeys.** Default: owner-held only, never in the Passport. Confirm.
8. **Tier 1 sign-up whitelist.** Default: empty until each platform's terms are checked. Which services are checked first?
9. **Cookie profile at rest.** Default: encrypted on the host and decrypted into the mount only while a session runs. Confirm, or name a different rule.
10. **Canary response.** Default: a canary hit applies hard taint, detaches the mount and holds the agent's sessions for review. Confirm. Which services get canaries?
11. **Tier 2 gating.** C06 gates Tier 2 on hard taint, and 15's ladder requires per-action approval. Default: per-action approval always applies (15), and hard taint additionally blocks Tier 2 in the tainted session. Confirm.
