# Remote Access, Authentication, Authorization and Privacy

Status: DRAFT for owner review (2026-10-25) · Owner questions: 12
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md · Used by: 02-wire-protocol-and-sync.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 33-backup-dr-and-export.md

## Purpose
How a client reaches a home-hosted server, how humans prove who they are, what each principal may do, and how Bazaar behaves when an instance has several human members under privacy law. It implements the `IConnectionAuthenticator` hook in 02 and supplies the human identity that 15 requires for approvals.

## Decided inputs
- Remote access is the operator's choice; the setup guide documents Tailscale, Headscale, WireGuard and Cloudflare Tunnel. Tailscale's Personal plan is non-commercial per its terms (owner, 2026-10-24).
- No web client, ever; a read-only status page is allowed and never shows chat, code or Vault content (Server.md s1). A narrow interactive sign-in page (for example passkeys) is allowed, separate from the status page (owner, 2026-10-24).
- Device pairing by one-time QR/code mints a revocable per-device credential (owner).
- Other humans join as coworkers treated as seniors; DMs are two-member GCs, the owner reads all, others may request access (CLAUDE.md; 01).
- An instance is normally one person's or company's private workspace; with several human members Bazaar provides global privacy-law compliance settings (Server.md item 17).
- Erasure against the append-only log is OPEN; per-person encryption with key destruction is a research suggestion; counsel needed.
- Approvals come from a platform-signed human identity, never an agent (15).

## Design

### 1. Connection options and trust model
The server only listens; it opens no routes itself. Options are documentation plus config.

| Option | Third party in path | Inbound port | Notes |
|---|---|---|---|
| Tailscale | Control plane; data is WireGuard, relays see ciphertext | none | Personal plan non-commercial; company use needs a paid plan (needs counsel); seat per human |
| Headscale | operator only | none | no seats, operator runs control server |
| WireGuard | none | one UDP port | most manual; CGNAT blocks it |
| Cloudflare Tunnel | Cloudflare terminates TLS, sees plaintext | none | WebSocket fit unverified; weakest self-hosted posture |
| Port forward + DDNS | none | yes | discouraged |

[PROPOSED] Trust rule: the network is never the only gate. Overlay membership is reachability, not identity; every connection presents a per-device credential.

### 2. TLS for a home server [PROPOSED]
`ICertificateProvider` sources: operator PEM/PFX; ACME for an operator domain; overlay-issued certificate (unverified). Plain `ws://` only on loopback. Pairing may record the server key hash (trust-on-first-use pin). No mTLS in v1.

### 3. Pairing flow (RFC 8628 semantics, native approval) [PROPOSED]
RFC 8628 supplies the shape: a high-entropy polled `device_code`, a short `user_code`, approval by an already-trusted party. Its `verification_uri` is normally a web page; Bazaar keeps approval inside a native client (or the host CLI) so no approval web page exists.
1. A signed-in human creates an offer: `POST /pair/offers` (role, label, TTL default 10 min). Server returns an 8-char Crockford base32 `user_code` and QR payload `bazaar://pair?h=<host>&c=<user_code>&k=<key hash>`.
2. The new device calls `POST /pair/device-authorization` with the code, gets a 256-bit `device_code` and `interval` 5 s, and polls `POST /pair/token` (`authorization_pending`, `slow_down`, `expired_token`, `access_denied`).
3. The approver's native Approvals panel shows device name, class, IP and `user_code` and must confirm. The offer is bound to its creator and single use, which answers RFC 8628's remote-phishing risk.
4. Approval mints a device record and the credential pair (section 5).

Bootstrap: until the first owner device exists, the host console prints a one-time owner code.

### 4. Interactive sign-in page [PROPOSED, flagged]
**Tension:** CLAUDE.md says no web client, ever, allowing only a read-only status page. The owner's 2026-10-24 exception allows a narrow sign-in page. This doc fences it so it cannot grow into a client.

Purpose: sign a human in from a new device with a passkey, then hand off to a native client. Separate listener/routes from `/status` (02).
- Routes, exactly three: `/signin` (passkey assertion, recovery-code fallback), `/signin/register` (add a passkey, only after native approval), `/signin/done` (one-time handoff code or deep link `bazaar://signin?code=...`).
- Shown: server display name, the sign-in control, generic error text.
- Never shown: GC or agent names, member or device lists, counts, version beyond a build id, logs, links to data, any response distinguishing valid from invalid usernames.
- No session cookie beyond the ceremony. Output is a 60 s single-use handoff code redeemed at `/auth/handoff` for a normal device credential. No Bazaar content is ever rendered in a browser.
- Off by default; operator enables it and sets the bind address.

ASP.NET Core Identity passkeys (page updated 2026-10-13): "deliberately scoped to authentication scenarios", "isn't intended as a general-purpose WebAuthn library", needs a WebAuthn browser. So passkeys serve only this page, not native clients. Set the RP ID explicitly (`IdentityPasskeyOptions.ServerDomain`) rather than inferring it from the Host header; passkeys work only on the registered domain, so a hostname change breaks them (recovery codes cover it). Attestation state is unsigned JSON, so keep it server-side. Exact .NET 10 API names are unverified; confirm in a spike.

If the owner declines the exception: pairing by code plus recovery codes only; nothing else changes.

### 5. Tokens, revocation, WebSocket auth [PROPOSED]
| Credential | Lifetime | Stored |
|---|---|---|
| Access token (bearer, device + principal scope) | 15 min | memory |
| Refresh credential (256-bit, rotated each use) | 30 days sliding, 90 absolute | client: Android Keystore, Windows DPAPI, Linux secret service; server: hash only |
| Handoff code | 60 s, single use | server hash |

Reuse of a retired refresh credential revokes the device and audits it. Revocation is immediate: live sockets close 4403 and a device revision is checked on connect and `auth.refresh`. The owner revokes any device; a coworker revokes their own.

WebSocket: credential in the upgrade `Authorization` header, or in the first `hello` frame within 5 s (02). No cookies; `Origin` absent or allow-listed. Membership is re-checked per subscription, not only at connect.

### 6. Roles and permissions [PROPOSED]
Principals: `owner` (one per instance in v1), `coworker` (human, senior), `agent`, `service` (SDK/system). ASP.NET policy-based authorization over a Postgres membership table; no OpenFGA in v1.

| Capability | owner | coworker | agent |
|---|---|---|---|
| Members, devices, privacy settings, backups | yes | no | no |
| Read any GC/DM | yes (audited on DMs) | as member or by grant | as member |
| Create GC, invite | yes | yes | per policy |
| Give human approval (15) | yes | only scopes the owner delegates | never |
| Write audit/log | no | no | no |

Per-GC: `gc_member` decides reads and writes; owner read-all is a server rule, not a row. DM access request (01): the requester files `gc_access_request` plus a `system` entry; both DM members and the owner are notified; the owner may approve alone; access is read-forward from the grant `seq`; the grant is visible to both members. An agent's request or approval is never honoured as human consent.

### 7. Rate limiting [PROPOSED]
ASP.NET rate limiter, separate buckets for `/ws`, `/pair`, `/signin`, `/status`. Starting values (unsourced): pairing 5 attempts per IP per 10 min and 5 per offer, then the offer is burned; sign-in 10 per IP per 10 min; 20 frames/s and 1 MiB per socket; 4 sockets per device. Breaches are audited.

### 8. Audit
Hash-chained `IAuditLog` from 15. Events: offers, pairing, revocation, refresh reuse, sign-in, role change, DM access request/grant/deny, owner DM read, export, erasure.

### 9. Privacy settings model [PROPOSED]
One `privacy_settings` record per instance; one-person defaults, stricter profile when a second human joins.
- **Controller:** the operator (person or company); the Bazaar project receives no instance data and is not a processor (needs counsel). A privacy contact is named in settings.
- **Data inventory:** a registry of fields that can hold personal data (identity, device, GC content, mail, memory/Vault, audit, gateway logs, backups) with purpose, retention and erasure method; a test checks it against the schema.
- **Consent and notice:** a joining coworker sees what is recorded, that the owner can read DMs and that agents process messages; acceptance is audited. Optional items get per-item, withdrawable consent.
- **Retention:** per category; GC entries are kept by default.
- **Export:** per-person job (JSON + attachments) via 33.
- **Erasure hook:** `IErasureParticipant` per subsystem.
- **Profiles:** `personal`, `company-global`, overlays for EU/UK, California, Philippines (content needs counsel).

**GC-log erasure (OPEN).** Candidate, not decided: personal fields stored as per-person encrypted payloads; erasure destroys the key and appends a tombstone; structure, timestamps and authorship stay. Costs: key management; backups keep old keys until expiry, so retention must be documented or key copies purged; third-party names in agent text are hard to attribute; whether the remainder is still personal data and whether key destruction counts as erasure are legal questions with no court decision found. Alternatives: documented retention exemptions, or rewrite redaction (breaks append-only).

**For counsel (research, not advice):** EU/UK lawful basis for employer reading of DMs, Article 17(3) exceptions, controller/processor roles with model providers, transfers to US gateways; California employee data scope and notices; Philippines Data Privacy Act ss. 4 and 6, NPC registration and breach duties; Tailscale and Cloudflare commercial terms.

## Data and interfaces
```sql
human(id, display_name, role, status)
device(id, human_id, label, class, refresh_hash, revision, revoked_at)
pairing_offer(id, created_by, role, user_code_hash, device_code_hash, expires_at, state, attempts)
passkey_credential(id, human_id, credential_id, public_key, sign_count)
recovery_code(id, human_id, hash, used_at)
privacy_settings(instance_id, profile, overlays jsonb, contact, retention jsonb)
privacy_request(id, subject_ref, kind, status, due_at, completed_at)
```
```csharp
interface IPairingService { Task<PairingOffer> CreateOfferAsync(PrincipalRef by, Role role, TimeSpan ttl); Task<DeviceGrant> RedeemAsync(string deviceCode); }
interface ITokenService { Task<TokenPair> RefreshAsync(string refresh, DeviceInfo d); Task RevokeDeviceAsync(Guid deviceId, PrincipalRef by); }
interface IAuthorizer { ValueTask<bool> CanAsync(PrincipalRef p, string action, ResourceRef r); }
interface IErasureParticipant { Task<ErasurePlan> PlanAsync(PersonRef p); Task ExecuteAsync(ErasurePlan plan); }
```

## Behaviour and edge cases
- Lost device: revoke from another device, or from the host CLI, which also issues a new bootstrap code.
- Revoked coworker: devices close; they keep what they saw (01).
- Privacy requests carry a due date; missed ones notify the owner.

## Testing
Real Postgres, fake clock, in-memory WebSocket host, Avalonia.Headless for the Approvals panel. Cases: offer single use, expiry, burn after wrong codes; `slow_down`; refresh reuse revokes device; revoked socket closes 4403; header and first-frame auth; rate caps; DM unreadable until granted; owner DM read audited; agent cannot approve; sign-in page snapshot has no forbidden content; handoff single use; inventory matches schema; erasure plan on fake participants.

## Work packages
1. Schema and migrations (needs 04).
2. `TokenService` with rotation and reuse detection.
3. `PairingService`, `/pair/*`, bootstrap console code.
4. `IConnectionAuthenticator` implementation (needs 2, 3, 02).
5. `IAuthorizer` over `gc_member`; DM access-request flow (needs 01).
6. Rate-limiter configuration.
7. Approvals panel in Client.Core (needs 3, 27).
8. Sign-in page, passkeys, handoff, recovery codes (needs 2; blocked on question 1).
9. `ICertificateProvider`, TLS and network setup guide.
10. Audit integration (needs 15).
11. Privacy settings, data-inventory registry, export job (needs 33).
12. Erasure participants and GC-log mechanism (blocked on question 9).

## Conflicts
- Sign-in page vs "no web client, ever": reconciled only by the owner's exception; section 4 is the proposed fence.
- Cloudflare Tunnel puts a third party in the TLS path against the self-hosted posture.
- Tailscale Personal terms vs a company instance.
- Append-only log vs erasure rights; backups defeat key destruction until expiry.
- Owner reads all DMs vs coworkers' expectations and employer-monitoring law.

## Questions for the owner
1. Approve the sign-in page exception with the three-route scope in section 4? Default: yes, off by default; otherwise pairing by code only.
2. May a coworker approve pairing of their own devices? Default: yes, own devices only; the owner approves new humans.
3. One owner per instance in v1? Default: yes.
4. May any coworker give human approvals (15)? Default: none unless the owner delegates a scope.
5. Token lifetimes 15 min / 30 days sliding / 90 absolute? Default: yes.
6. Pairing code 8 characters, 10-minute TTL? Default: yes.
7. Can the other DM member approve an access request alone? Default: owner approves; the other member is notified and can object (matches 01 Q4).
8. Should the owner's DM read leave a visible marker for its members? Default: yes (monitoring-law risk; counsel).
9. Erasure direction for the GC log: per-person encrypted fields, retention exemption, or counsel first? Default: make personal fields encryptable per person in the schema, decide the legal position after counsel.
10. Region overlays in v1: EU/UK, California, Philippines? Default: those three.
11. TLS default: ACME on an operator domain or operator PEM? Default: both supported, overlay certificates documented.
12. If every device is lost: host console only, or also printed recovery codes? Default: both.
