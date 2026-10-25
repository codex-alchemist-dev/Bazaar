# Remote Access, Authentication, Authorization and Privacy

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 33-backup-dr-and-export.md · Used by: 02-wire-protocol-and-sync.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 33-backup-dr-and-export.md

## Purpose
How a client reaches a home-hosted server, how humans prove who they are, what each principal may do, and how Bazaar behaves when an instance has several human members under privacy law. It implements the `IConnectionAuthenticator` hook in 02 and supplies the human identity that 15 requires for approvals.

## Decided inputs
- Remote access is the operator's choice; the setup guide documents Tailscale, Headscale, WireGuard and Cloudflare Tunnel. Tailscale's Personal plan is non-commercial per its terms (owner, 2026-10-24).
- Ingress: the home host accepts no inbound connections by default. It polls GitHub and mail providers; an overlay or tunnel is optional for real-time webhooks (owner, DECISIONS-LOG round 8, C30, 2026-10-25).
- No web client, ever (CLAUDE.md, owner 2026-10-24). A read-only status page is allowed and never shows chat, code or Vault content (Server.md §1). A narrow interactive sign-in page is allowed, separate from the status page (owner, 2026-10-24). Its three-route scope is decided (DECISIONS-LOG round 11, C42, 2026-10-25; section 4).
- Device pairing by one-time QR/code mints a revocable per-device credential (owner).
- Other humans join as coworkers treated as seniors; DMs are two-member GCs; the owner reads all, others may request access (CLAUDE.md; 01).
- An instance is normally one person's or company's private workspace; with several human members, global privacy-law compliance settings apply (Server.md item 17).
- Erasure: personal fields are stored as per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone; structure, authorship and timestamps stay (owner, DECISIONS-LOG round 2, C01, 2026-10-25). Legal sufficiency is OPEN (needs counsel).
- Approvals come from a platform-signed human identity, never an agent (15).
- Shared limits come from 15's quota table (owner, DECISIONS-LOG round 10, C36, 2026-10-25).
- Push wake-ups are bare hints; content is fetched over the authenticated connection (owner, DECISIONS-LOG round 5, C41, 2026-10-25).

## Design

### 1. Connection options and trust model
The server opens no inbound ports or tunnels itself. Options are documentation plus config.

| Option | Third party in path | Inbound port | Notes |
|---|---|---|---|
| Tailscale | Control plane; data is WireGuard, relays see ciphertext | none | Personal plan non-commercial; company use needs a paid plan (needs counsel); seat per human |
| Headscale | operator only | none | no seats, operator runs control server |
| WireGuard | none | one UDP port | most manual; CGNAT blocks it |
| Cloudflare Tunnel | Cloudflare terminates TLS, sees plaintext | none | WebSocket fit unverified; weakest self-hosted posture; operator option only |
| Port forward + DDNS | none | yes | discouraged |

[PROPOSED] Trust rule: the network is never the only gate. Overlay membership is reachability, not identity; every connection presents a per-device credential.

### 2. TLS for a home server [PROPOSED]
`ICertificateProvider` sources: operator PEM/PFX; ACME for an operator domain; overlay-issued certificate (unverified). Plain `ws://` only on loopback. Pairing may record the server key hash (trust-on-first-use pin). No mTLS in v1.

### 3. Pairing flow (RFC 8628 semantics, native approval) [PROPOSED]
RFC 8628 supplies the shape: a high-entropy polled `device_code`, a short `user_code`, approval by an already-trusted party. Its `verification_uri` is normally a web page; Bazaar keeps approval inside a native client (or the host CLI), so no approval web page exists. Route shapes are listed in the HTTP surface of 02 (C15); the rules here govern them.
1. A signed-in human creates an offer: `POST /pair/offers` (role, label, TTL from `pairing.offer_ttl_minutes`). Server returns an 8-char Crockford base32 `user_code` (format fixed in v1 [PROPOSED]) and QR payload `bazaar://pair?h=<host>&c=<user_code>&k=<key hash>`.
2. The new device calls `POST /pair/device-authorization` with the code, gets a 256-bit `device_code` and `interval` 5 s, and polls `POST /pair/token` (`authorization_pending`, `slow_down`, `expired_token`, `access_denied`).
3. The approver's native Approvals panel shows device name, class, IP and `user_code` and must confirm. The offer is bound to its creator and single use, which answers RFC 8628's remote-phishing risk.
4. Approval mints a device record and the credential pair (section 5). An owner device that is enrolled for approvals also gets its approval key here (section 5a).

Bootstrap: until the first owner device exists, the host console prints a one-time owner code.

### 4. Interactive sign-in page [DECIDED scope (C42, 2026-10-25); route details PROPOSED]
**Tension, resolved by the owner:** CLAUDE.md's "no web client, ever" allowed only a read-only status page. The owner's 2026-10-24 exception allows a narrow sign-in page, and round 11 approved this three-route scope (off by default, no content shown). CLAUDE.md's wording is still to be corrected (Conflicts). The fence below stops the page growing into a client.

Purpose: sign a human in from a new device with a passkey, then hand off to a native client. Separate listener and routes from `/status` (02).
- Routes, exactly three, covering sign-in, pairing approval and recovery:
  - `/signin`: passkey assertion.
  - `/signin/register`: pairing approval. A passkey is enrolled here only after the native approval in section 3 has been given. The approval itself stays native.
  - `/signin/recover`: recovery-code sign-in.
- Handoff: success redirects to `bazaar://signin?code=...`, a 60 s single-use handoff code. The native client redeems it at `/auth/handoff` (an API route on the 02 surface, not a page) for a normal device credential. There is no separate "done" page.
- Shown: server display name, the sign-in control, generic error text.
- Never shown: GC or agent names, member or device lists, counts, version beyond a build id, logs, links to data, any response distinguishing valid from invalid usernames.
- No session cookie beyond the ceremony. No Bazaar content is ever rendered in a browser.
- Off by default; operator enables it and sets the bind address.

ASP.NET Core Identity passkeys (page updated 2026-10-13): "deliberately scoped to authentication scenarios", "isn't intended as a general-purpose WebAuthn library", needs a WebAuthn browser. So passkeys serve only this page, not native clients. Set the RP ID explicitly (`IdentityPasskeyOptions.ServerDomain`) rather than inferring it from the Host header; passkeys work only on the registered domain, so a hostname change breaks them (recovery codes cover it). Attestation state is unsigned JSON, so keep it server-side. Exact .NET 10 API names are unverified; confirm in a spike.

### 5. Tokens, revocation, WebSocket auth [PROPOSED]
| Credential | Lifetime | Stored |
|---|---|---|
| Access token (bearer, device + principal scope) | 15 min | memory |
| Refresh credential (256-bit, rotated each use) | 30 days sliding, 90 absolute | client: Android Keystore, Windows DPAPI, Linux secret service; server: hash only |
| Handoff code | 60 s, single use | server hash |

Reuse of a retired refresh credential revokes the device and audits it. Lifetimes are settings (`auth.*`, Settings table). Revocation is immediate: live sockets close 4403, a device revision is checked on connect and `auth.refresh`, and the device's approval key (5a) is revoked with it. The owner revokes any device; a coworker revokes their own.

WebSocket: credential in the upgrade `Authorization` header, or in the first `hello` frame within 5 s (02). No cookies; `Origin` absent or allow-listed. Membership is re-checked per subscription, not only at connect.

### 5a. Owner approval signing key [PROPOSED]
Doc 14 and doc 28 defer the key class for a signed owner approval to this section. A merge approval (15's `approval.signature`; 21's squash merge; 29's approve card) needs a key that the server can check and that no agent or server process can use.
- **Class `owner-approval`.** One key pair per owner device enrolled for approvals. The private key is generated on the device and never leaves it. The server stores only the public key, device id and human id. The key signs approvals; it never authenticates a session and is not a bearer credential.
- **Storage floor (not a setting).** Hardware-backed where the platform offers it. Android: Keystore, with a fresh biometric prompt per signature (29). Windows and Linux: TPM or platform key store (unverified; spike). A software-only key cannot approve a merge.
- **Algorithm:** ECDSA P-256, chosen for hardware support [PROPOSED; unverified per platform].
- **Signed payload:** approval id, action, `args_hash`, PR head SHA where the action is a merge (a new head invalidates the approval, 29), deciding human id, device id, issued-at, expiry and a server nonce. The server checks signature, key not revoked, nonce unused and expiry before it acts.
- **Holders:** the owner only in v1 (Q1). A coworker gets an approval key only if the owner delegates a scope (`approval.coworker_scopes`). Agents never hold one.
- **Lifecycle:** enrolment happens inside pairing (section 3) and is audited. Device revocation revokes the key at once. Keystore loss or device reset forces re-pairing (29). Rotation means a new pairing.
- **Data:** `approval_key` (section Data and interfaces). Enrolment, each use and revocation go to `audit_log` (15).

### 6. Roles and permissions [PROPOSED]
Principals: `owner` (one per instance in v1), `coworker` (human, senior), `agent`, `service` (SDK/system). ASP.NET policy-based authorization over a Postgres membership table; no OpenFGA in v1.

| Capability | owner | coworker | agent |
|---|---|---|---|
| Members, devices, privacy settings, backups | yes | no | no |
| Read any GC/DM | yes (audited on DMs) | as member or by grant | as member |
| Create GC, invite | yes | yes | per policy |
| Give human approval (15) | yes | only scopes the owner delegates | never |
| Write audit/log | no | no | no |

Per-GC: `gc_member` (01) decides reads and writes; owner read-all is a server rule, not a row. DM access request (01): the requester files a `gc_access_request` and a `system` event in the GC log; both DM members and the owner are notified; the owner may approve alone; access is read-forward from the grant `seq`; the grant is visible to both members. An agent's request or approval is never honoured as human consent.

The owner's merge approval is a signed human approval (15), signed by the key in section 5a. The GitHub credential that performs the merge is held by 14 (a dedicated owner GitHub App or the owner's linked credential, chosen in setup; DECISIONS-LOG round 12, C25).

### 7. Rate limiting [PROPOSED]
ASP.NET rate limiter, separate buckets for `/ws`, `/pair`, `/signin`, `/status`. Shared values (socket frames, sockets per device) belong in 15's quota table (C36). This doc's own pairing and sign-in limits are the `pairing.*` and `signin.*` settings above; their starting values are unsourced draft values. Socket starting values (20 frames/s, 1 MiB per socket, 4 sockets per device) move to 15. Breaches are audited.

### 8. Audit
Hash-chained audit log `audit_log`, owned by 15 (`IAuditLog`). Events: offers, pairing, revocation, refresh reuse, sign-in, role change, DM access request/grant/deny, owner DM read, export, erasure.

### 9. Privacy settings model [PROPOSED]
One `privacy_settings` record per instance; one-person defaults, stricter profile when a second human joins.
- **Controller:** the operator (person or company); the Bazaar project receives no instance data and is not a processor (needs counsel). A privacy contact is named in settings.
- **Data inventory:** a registry of fields that can hold personal data (identity, device, GC content, mail, memory/Vault, audit, gateway logs, backups) with purpose, retention and erasure method; a test checks it against the schema.
- **Consent and notice:** a joining coworker sees what is recorded, that the owner can read DMs and that agents process messages; acceptance is audited. Optional items get per-item, withdrawable consent.
- **Retention:** per category; GC entries are kept by default.
- **Export:** per-person job (JSON + attachments) via 33.
- **Erasure hook:** `IErasureParticipant` per subsystem.
- **Profiles:** `privacy.profile` (`personal`, `company-global`) and `privacy.overlays` for EU/UK, California, Philippines (content needs counsel).

**GC-log erasure [DECIDED mechanism, C01, 2026-10-25; legal sufficiency OPEN].** Personal fields are per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone; structure, timestamps and authorship stay. The first migration (04) carries the key table and encryptable fields. Still open: (a) backups keep old keys until expiry, so backup retention must equal the erasure window (33); (b) third-party names in agent text are hard to attribute; (c) whether the remainder is still personal data and whether key destruction counts as erasure (no court decision found; counsel).

**For counsel (research, not advice):** EU/UK lawful basis for employer reading of DMs, Article 17(3) exceptions, controller/processor roles with model providers, transfers to US gateways; California employee data scope and notices; Philippines Data Privacy Act ss. 4 and 6, NPC registration and breach duties; Tailscale and Cloudflare commercial terms.

## Data and interfaces
Tables owned here (C16). `gc_member`, `gc_access_request` and the GC log are owned by 01; `audit_log` by 15.
```sql
human(id, display_name, role, status)
device(id, human_id, label, class, refresh_hash, revision, revoked_at)
pairing_offer(id, created_by, role, user_code_hash, device_code_hash, expires_at, state, attempts)
passkey_credential(id, human_id, credential_id, public_key, sign_count)
recovery_code(id, human_id, hash, used_at)
privacy_settings(instance_id, profile, overlays jsonb, contact, retention jsonb)
privacy_request(id, subject_ref, kind, status, due_at, completed_at)
approval_key(id, human_id, device_id, public_key, alg, created_at, revoked_at)
```
```csharp
interface IPairingService { Task<PairingOffer> CreateOfferAsync(PrincipalRef by, Role role, TimeSpan ttl); Task<DeviceGrant> RedeemAsync(string deviceCode); }
interface ITokenService { Task<TokenPair> RefreshAsync(string refresh, DeviceInfo d); Task RevokeDeviceAsync(Guid deviceId, PrincipalRef by); }
interface IAuthorizer { ValueTask<bool> CanAsync(PrincipalRef p, string action, ResourceRef r); }
interface IErasureParticipant { Task<ErasurePlan> PlanAsync(PersonRef p); Task ExecuteAsync(ErasurePlan plan); }
```

### Settings
Registered in doc 03's settings registry and surfaced in the setup wizard or advanced settings (docs 27, 32). Every change is audited (15). "Yes" means the change needs owner approval through 15's flow. Shipped floors that are not settings: the sign-in page shows no content; approval keys are never software-only; agents never approve.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `auth.access_token_minutes` | int | 15 | install | owner | no |
| `auth.refresh_sliding_days` | int | 30 | install | owner | yes, if lengthened |
| `auth.refresh_absolute_days` | int | 90 | install | owner | yes, if lengthened |
| `pairing.offer_ttl_minutes` | int | 10 | install | owner | no |
| `pairing.max_attempts_per_ip_10min` | int | 5 | install | owner | yes, if raised |
| `pairing.max_wrong_codes_per_offer` | int | 5 | install | owner | yes, if raised (then the offer burns) |
| `pairing.coworker_approves` | enum: `own_devices`, `none` | `own_devices` | install | owner | yes, if widened |
| `approval.coworker_scopes` | list | empty (no coworker approvals) | install | owner | yes |
| `signin.enabled` | bool | false | install | owner | yes, when enabled |
| `signin.bind_address` | string | unset (required when enabled) | install | owner | yes |
| `signin.attempts_per_ip_10min` | int | 10 | install | owner | yes, if raised |
| `signin.handoff_ttl_seconds` | int | 60 | install | owner | yes, if raised |
| `tls.certificate_source` | enum: `pem`, `acme`, `overlay` | `pem` | install | owner | no |
| `recovery.printed_codes_enabled` | bool | true | install | owner | no |
| `privacy.profile` | enum: `personal`, `company-global` | `personal` | install | owner | yes; needs counsel |
| `privacy.overlays` | list: `eu-uk`, `california`, `philippines` | all three | install | owner | yes; needs counsel |
| `privacy.owner_dm_read_marker` | bool | true | install | owner | yes, if turned off; needs counsel |

## Behaviour and edge cases
- Lost device: revoke from another device, or from the host CLI, which also issues a new bootstrap code.
- Revoked coworker: devices close; they keep what they saw (01).
- Privacy requests carry a due date; missed ones notify the owner.
- Push wake-ups carry no content and no ids; the device fetches over the authenticated connection (02, 29).
- Public ingress: GitHub and mail are polled (21, 22); no port is opened for them by default.

## Testing
Real Postgres, fake clock, in-memory WebSocket host, Avalonia.Headless for the Approvals panel. Cases: offer single use, expiry, burn after wrong codes; `slow_down`; refresh reuse revokes device; revoked socket closes 4403; header and first-frame auth; rate caps; DM unreadable until granted; owner DM read audited; agent cannot approve; sign-in page exposes exactly three routes; sign-in page snapshot has no forbidden content; handoff single use; inventory matches schema; erasure plan on fake participants, including key destruction and tombstone. Owner approval key: signature verifies; wrong device, revoked key, reused nonce, expired approval and a changed PR head are each rejected; a software-only key is refused for merge; the Android path requires a fresh biometric prompt (fake Keystore).

## Work packages
1. Schema and migrations (needs 04).
2. `TokenService` with rotation and reuse detection.
3. `PairingService`, `/pair/*`, bootstrap console code.
4. `IConnectionAuthenticator` implementation (needs 2, 3, 02).
5. `IAuthorizer` over `gc_member`; DM access-request flow (needs 01).
6. Rate-limiter configuration (shared values from 15).
7. Approvals panel in Client.Core (needs 3, 27).
8. Sign-in page, passkeys, handoff, recovery codes (needs 2; scope decided, route details to spike).
9. `ICertificateProvider`, TLS and network setup guide.
10. Audit integration (needs 15).
11. Privacy settings, data-inventory registry, export job (needs 33).
12. Erasure participants and the per-person key table in the first migration (needs 04; legal sufficiency with counsel).
13. Owner approval key enrolment, `approval_key` table and signature verification (needs 3, 15 package 3; section 5a).

## Conflicts
- CLAUDE.md still reads "No web client, ever (a read-only server status page is allowed)" without the sign-in exception (C42). Fix in the CLAUDE.md pass; the scope itself is decided.
- Cloudflare Tunnel puts a third party in the TLS path against the self-hosted posture. Remains as an operator option only (C30).
- Tailscale Personal terms vs a company instance (needs counsel).
- Append-only log vs erasure rights: the mechanism is decided (C01), but backup retention (33) and legal sufficiency remain open.
- Owner reads all DMs vs coworkers' expectations and employer-monitoring law (`privacy.owner_dm_read_marker`, `privacy.overlays`; needs counsel).
- For 02 (C15): its HTTP surface must list `/pair/*`, `/signin`, `/signin/register`, `/signin/recover`, `/auth/handoff`, and its "socket is the only channel" line must go.
- For 01 (C09): the `gc_access_request` and `system` event for DM access must exist in its log model.
- For 33 (C01): backup retention must equal the erasure window; its "until every snapshot expires" wording must follow.
- Section 3 keeps approval native, while the decided scope includes a `/signin/register` pairing step. Read as: the browser enrolls a passkey only after native approval (Question 3).

## Questions for the owner
1. Is the owner the only human approver in v1 (one owner per instance)? Default: yes. Changing this changes the principal model (section 6) and the approval key holders (5a).
2. May the other DM member approve an access request alone? Default: no; the owner approves, and the other member is notified and can object (matches 01 Q4). This touches the owner-reads-all DM rule.
3. Is `/signin/register` (passkey enrolment after native approval) the "pairing approval" route of the decided scope, with the approval itself staying native? Default: yes.

MOVED TO SETTINGS: auth.access_token_minutes, auth.refresh_sliding_days, auth.refresh_absolute_days, pairing.offer_ttl_minutes, pairing.coworker_approves, approval.coworker_scopes, tls.certificate_source, recovery.printed_codes_enabled, privacy.overlays, privacy.owner_dm_read_marker (DECISIONS-LOG round 13)

ANSWERED: The sign-in page exception is approved with the three-route scope (sign-in, pairing approval, recovery), off by default, showing no content (DECISIONS-LOG round 11, C42).
ANSWERED: GC-log erasure uses per-person encrypted personal fields from day 1, with key destruction and a tombstone; the legal position goes to counsel (DECISIONS-LOG round 2, C01).
