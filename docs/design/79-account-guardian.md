# 79. Account Guardian: the self-service security and privacy centre

Status: PROPOSED. The owner's direction of 2026-11-19 (relayed by the coordinating session) to adopt Resibo's Account Guardian as Bazaar module `account-guardian` is DECIDED. Every detail below it is PROPOSED or OPEN. The optional Notifications module (section 12) and the optional Billing module (section 13) are PROPOSED only. Nothing here is built: no code, migration, route or pane exists yet. Legal text and legal sufficiency are counsel-gated and marked OPEN throughout.
Depends on: 03-server-host-and-providers.md (settings registry, provider seam), 04-postgres-schema-and-migrations.md (migrations, NOTIFY), 14-identity-service-and-passport.md (keys, per-person key custody), 15-security-taint-and-approvals.md (audit log, approvals, step-up), 32-remote-access-auth-and-privacy.md (devices, tokens, recovery codes, privacy model), 40-core-feature-modules-audit.md and BAZAAR-COMPONENTS.md (module shape, `Principal`, per-component migrations), 44-data-portability.md (export bundles, erasure interaction), 46-kodo-health-module.md (health contract), 48-gap-decisions-security-identity.md (roles, biometrics, retention, privacy law).
Used by: 28, 29, 30 (client heads that host the pane), 45 (onboarding consent step), 51 (web admin section, read-only), 33 (backup window for erasure).
Source: Resibo (sister program) `core/account_guardian/CLAUDE.md`, `docs/apis/v3-deepdive-06-account-guardian-api.md`, `v3-deepdive-09-notifications-inbox-api.md`, `v3-deepdive-22-billing-subscription-api.md`; inventories `resibo-01-09.md`, `resibo-20-29.md` and CONVERGENCE.md rows 21 and 30.

## 1. Purpose

Every human who uses a Bazaar instance (the owner and any coworker) gets one place, a pane in every client, where they can see and control what the platform holds about them and how they are signed in:

- **Sessions and devices:** every device paired to me, when it last refreshed, which are connected now, and a revoke button for each, plus "sign out everywhere else".
- **Recovery options:** which sign-in methods I have (passkeys, printed recovery codes, approval keys), how many codes are left, and a way to ask for help when I have lost everything.
- **Data subject requests:** export my data, delete my data (with a grace period and a hold hook), correct my data, and see my access report.
- **Consent:** the notices and documents I accepted, by version, with withdrawal for optional items.
- **Notification preferences:** which wake-ups and which channels I want.

Resibo built this as a thin orchestration layer over its Auth, Persistence, Billing and Notifications APIs. Bazaar keeps that shape: Guardian owns the request lifecycles, the consent ledger and the pane. It does not own sessions, keys, erasure mechanics, exports, the audit log or the notification channels. Those stay with the modules that already own them (section 9).

## 2. Decided inputs

- Owner, 2026-11-19: incorporate everything from Resibo that can benefit Bazaar, overengineer, make conflicts separate modules, account for everything. DECIDED.
- Owner (DECISIONS-LOG round 2, C01, 2026-10-25): GC-log erasure destroys the per-person key and appends a tombstone. Structure, authorship and timestamps stay. Legal sufficiency is OPEN (counsel). DECIDED mechanism.
- Doc 48 section 9 (DECIDED): soft delete first, legal hold blocks removal and queues the request, crypto-erase yields a signed proof stored in the audit chain, backups keep old keys until the backup window ends.
- Doc 48 section 12 (DECIDED): the platform ships data-subject tools for export, deletion and access, with a due date on each request, and records consent and notice in the audit chain.
- Doc 48 sections 4 and 8 (DECIDED): roles are custom; at least one human owner always exists; biometric step-up is required for sign-in on a new device, recovery and high-risk actions; templates never leave the device.
- Doc 44 (DECIDED principles D1 to D4): everything is exportable; exporters are modules; exports carry ciphertext, not keys, with plaintext personal fields off by default.
- `BAZAAR-COMPONENTS.md` section 4.2 (owner, 2026-11-19): actors are recorded as a `Principal` (`kind:id`).
- `BAZAAR-COMPONENTS.md` section 2.1: server and client halves are separate modules.
- Configurability rule (DECISIONS-LOG round 13): undecided values are operator settings with a safe default. DECIDED items and security invariants are not settings.
- CLAUDE.md: instances are private by default; with several human members the global compliance profile applies; the sign-in page is narrow (three routes, doc 32 section 4); no web client.

## 3. What this doc does not decide

- Legal text, jurisdictions and whether key destruction counts as erasure. Packs (section 8) carry the text as data and every pack needs counsel before release. Doc 48 section 12 lists the jurisdiction list as OPEN, and it stays OPEN.
- Erasure inside Vault history and git history (doc 48 section 9 OPEN; doc 33 Q6). Guardian reports it as "not covered" in the proof until that is decided.
- Owner recovery (backup-owner quorum, hardware keys, time lock): doc 48 section 7 owns it. Guardian only links to it and shows its state.
- The channel mechanics of push (doc 29, doc 02 wake hints) and of email (doc 22). Section 12 reuses them.
- Pricing, tiers and the commercial licence (doc 47 M10). Section 13 is a seam, not an offer.
- The numbering of the module in the doc index, the pane mnemonics (doc 41 registry), and the settings page layout.

## 4. Identity, naming and shape

| Item | Value |
|---|---|
| Module id | `account-guardian` (settings section `Bazaar:AccountGuardian`) |
| Server projects | `Bazaar.Server.AccountGuardian`, `Bazaar.Server.AccountGuardian.Abstractions` |
| Client project | `Bazaar.Client.AccountGuardian` (pane and view models; references `Bazaar.Client.Core` only) |
| Pack provider modules | `Bazaar.Server.AccountGuardian.Legal.<Pack>`, one per jurisdiction pack, for example `...Legal.EuUk`, `...Legal.California`, `...Legal.Philippines` (section 8) |
| Wire namespace | `AccountGuardian` inside `Bazaar.Contracts`; routes under `/api/guardian/v1/` on the doc 02 surface |
| Kind | `feature` (server), `client-pane` (client), `provider` (packs) |
| Migration namespace | `account_guardian` (folder `Data/Sql/account_guardian/`). The folder must use an underscore because the runner maps `account-guardian` to `account_guardian`; doc 40 item 7 records what a mismatch costs |
| Hard dependencies | `audit`, `remote` (devices and tokens), `erasure` (via abstractions), `jobs` |
| Soft dependencies (`SoftDependsOn`) | `notifications` (section 12), `billing` (section 13), `data-portability` (doc 44 exporters), `kodo` |
| Licence | AGPL-3.0 as first-party code; the Legal packs are data and may be relicensed by their authors under the module exception (doc 47) |

Guardian does not appear in Bazaar.Server or Bazaar.Client.Core. It adds no code there.

### 4.1 Who it serves

Only human principals (`user:<id>`). Agents (`agent:`), services and `system:` never open, advance or approve a data subject request, a consent or a recovery case on their own authority. This restates the doc 32 rule that an agent's request or approval is never honoured as human consent. The server rejects a Guardian call whose caller is not a `user` principal acting on its own `subject`, except for the owner-assisted flows in section 7.

## 5. Sessions and devices

### 5.1 What a "session" is in Bazaar

Resibo had cookie-style sessions in its Auth `SessionStore`. Bazaar has devices (doc 32 `device`, one row per paired device), a rotating refresh chain per device (`refresh_credential`), 15-minute access tokens, and live WebSockets. So a Guardian session is a device. The device table does not carry a last-seen column, so Guardian derives three facts from existing sources and writes none of them:

| Fact | Source |
|---|---|
| Label, class, created, revoked | `IRefreshCredentialStore.FindDeviceAsync` (`Bazaar.Server.Remote.Abstractions`) |
| Last token refresh | newest `refresh_credential.issued_at` for the device |
| Connected now | the connection directory of the wire layer (`DeviceInfo` and `PrincipalRef`, doc 02) |
| Is this device | the caller's own device id from the token |

OPEN: a listing method on the Remote abstractions (`ListDevicesForHumanAsync`) does not exist yet; `IRefreshCredentialStore` only finds one device. Resibo's design keeps Guardian a thin view over the owning module's session store (deep-dive 06 section 4) and never works around a missing capability. Guardian does the same: the first work package adds the method to `Bazaar.Server.Remote.Abstractions`, owned by that module, and Guardian never queries `device` directly.

### 5.2 Contract

```csharp
namespace Bazaar.Server.AccountGuardian;

public sealed record DeviceSession(
    Guid DeviceId, string Label, string DeviceClass, DateTimeOffset CreatedAt,
    DateTimeOffset? LastRefreshAt, bool ConnectedNow, bool IsCurrent,
    string ClientBuildSummary, bool HasApprovalKey);   // never a raw user agent or IP

public interface IAccountSessions
{
    Task<IReadOnlyList<DeviceSession>> ListAsync(Principal subject, CancellationToken ct);
    Task<RevokeResult> RevokeAsync(Principal actor, Guid deviceId, CancellationToken ct);
    Task<RevokeAllResult> RevokeAllOthersAsync(Principal actor, Guid keepDeviceId, CancellationToken ct);
}
```

Rules taken from Resibo and kept: ownership is checked (a person revokes only their own devices; the owner may revoke any device, doc 32 section 5); a revoke that cannot reach the store returns an error and never a silent no-op, so nobody believes a lost phone is logged out when it is not; the listing shows a parsed summary, never the raw client string, to limit fingerprinting detail.

Rules added for Bazaar: revoke calls `ITokenService.RevokeDeviceAsync`, which closes live sockets with 4403 and revokes the device's approval key (doc 32 sections 5 and 5a), so Guardian does not re-implement revocation. "Sign out everywhere else" is the same call per device, not a bulk SQL update. Revoking the device the caller is on signs that client out and is allowed. Revoking the last device of the last owner is refused with code `last_owner_last_device` (doc 48 section 4.1 floor); the owner must enrol another device first or use the recovery flow.

### 5.3 My security activity (PROPOSED)

A read-only timeline of events about the caller: sign-ins, failed attempts against their account, device pairing and revocation, recovery-code use, role changes, DM access requests and grants, and owner DM reads that concern them. It is a filtered view of the audit log (`PrivacyAuditKinds`: `signin.*`, `device.revoked`, `recovery.code_used`, `role.changed`, `access_request.*`, `dm.owner_read`). OPEN: the audit abstractions today expose append only (`IAuditLog`); a per-subject read port is needed from doc 48 section 5. Until it exists the section shows "unavailable". This is a Bazaar-specific addition: Resibo had no such view, but doc 32 already promises that DM grants are visible to both members.

## 6. Recovery options

### 6.1 What Guardian shows and lets a person do

- Sign-in methods (a provider list, section 6.3): passkeys with creation date and revoke; printed recovery codes with the count remaining and "regenerate" (shows the new set once, invalidates the old set, needs step-up); owner approval keys by device (doc 32 section 5a), read-only here because they are enrolled and revoked with their device.
- A warning when a person has exactly one way in ("one passkey, no codes").
- For the owner, a state card for owner recovery (doc 48 section 7): backup owners named, threshold, time-lock length, any recovery in progress with a cancel button available to existing owner devices. Guardian renders it from the recovery module's port; it does not run it.

### 6.2 Lost everything: the recovery case (coworkers)

Resibo's lesson, which fits Bazaar: with no working sign-in method, identity cannot be proved by software, so recovery is a staff-mediated case, not an automated transfer. Bazaar's staff is the owner. Because the sign-in page is a fenced three-route page (doc 32 section 4), Guardian adds no unauthenticated intake route. Instead:

1. The coworker contacts the owner out of band (the owner's normal channels).
2. The owner opens a **recovery case** for that member from the Guardian admin view (capability `admin.members`, doc 48 section 4), naming the lost methods.
3. The case carries Resibo's verification checklist, adapted: (a) a prior recovery contact the member registered, if any; (b) knowledge of account detail that is not guessable (creation date, a GC they were added to); (c) vouching by a second human with a role that can vouch, where the install has more than one human. No single check suffices; the owner records the checklist result.
4. A `time_lock_until` applies (setting, section 10) and the member's other live devices, if any, are notified. They may cancel. This mirrors doc 48 section 7's delayed alerting at member scale.
5. On resolution the owner issues a replacement credential through the existing enrolment path (new recovery codes or a new passkey enrolment code). Guardian never sets a credential itself.
6. Every step is audited (section 14). Resolution needs the owner's signed approval (`IOwnerSignatureVerifier`, action `guardian.recovery.resolve`) and, once biometrics ship, a biometric check (doc 48 section 8).

The owner's own recovery is doc 48 section 7 and is not routed through this case type. A recovery case may not target the last owner.

### 6.3 Provider seam for sign-in methods

Resibo supported SSO, passkeys, email codes and SMS codes, plus linking a second SSO provider. Bazaar today has passkeys and recovery codes (doc 32). To account for Resibo's second-SSO linking (deep-dive 06) without choosing, Guardian lists methods through a seam:

```csharp
public interface ISignInMethodProvider
{
    string MethodId { get; }                       // "passkey", "recovery_code"
    Task<IReadOnlyList<SignInMethodInfo>> ListAsync(Principal subject, CancellationToken ct);
    Task<bool> TryRevokeAsync(Principal actor, string methodItemId, CancellationToken ct);
}
```

`passkey` and `recovery_code` providers are registered by the Remote module (OPEN: confirm the registration site with that module's owner). A future OIDC or SSO module registers a provider and gets linking and unlinking in the pane for free. No SSO provider is proposed here: the owner has not asked for one, and there is no sign-in code to attach it to.

## 7. Data subject requests

Four request kinds, matching the existing `privacy_request.kind` check (`access`, `deletion`, `portability`, `correction`). Doc 32 created `privacy_request` (remote namespace, owned by Remote) as a minimal record with a due date. Guardian owns the lifecycle and links to it; see section 15 for the ownership question.

### 7.1 Case lifecycle

All four kinds share one case table and one append-only event table (section 10). Each kind has its own stage set.

**Export (portability) and access:** `requested` -> `processing` -> `ready` -> `delivered` or `expired`, or `failed`. The export is built by the doc 44 pipeline as a per-person export job. Guardian never reads tables itself, so completeness is the exporters' job and is tested (section 17). The artifact is a blob with an expiry (setting). Delivery is in-app download, and a Notifications entry when that module is present (Resibo's delivery method was in-app only; email delivery waits for channels, same here).

**Deletion:**

| Stage | Meaning | Cancel? |
|---|---|---|
| `requested` | accepted, due date set from the active legal pack | yes |
| `grace_period` | cancellable window, default 30 days (setting) | yes |
| `on_hold` | blocked by one or more holds (section 7.3) | yes |
| `processing` | irreversible: key destruction and participants running | no |
| `complete` | proof stored | no |
| `cancelled` | withdrawn by the subject or refused by the owner under a floor | terminal |

Resibo's `BILLING_HOLD` generalises to `on_hold` with a list of hold kinds, so billing is one hold among several. A request that has no holds goes `requested` -> `grace_period` directly. At most one non-terminal deletion case per person is enforced by a partial unique index (PROPOSED, Bazaar's design, not from Resibo).

**Correction (rectification):** Resibo had no rectification flow. Bazaar needs one because doc 48 section 12 names data-subject tools and doc 04 stores personal fields. `requested` -> `in_review` -> `applied`, `partially_applied` or `declined` (with a reason code). Self-editable fields (display name, notification contact) are applied immediately by the owning module through `IDataCorrectionParticipant` and the case is a record of it. Fields that the platform cannot change (for example what others wrote about the person in GC messages: GC is append-only, edits keep every version) cannot be rectified in place. A `declined` or `partially_applied` outcome states that, and where possible a correction note can be attached to the person's profile. Legal sufficiency of that answer is OPEN (counsel).

### 7.2 Deletion processing and the append-only GC

This is the one place the Resibo design and Bazaar's design genuinely differ. Resibo's deep-dive 06 routes deletion through Persistence's normal, Historian-logged write path. Bazaar's GC log is append-only and never purged. Guardian therefore does not delete anything. At `processing` it calls one port:

```csharp
// Bazaar.Erasure.Abstractions (new, small; the coordinator stays in Server until its extraction, doc 40)
public interface IPersonErasureRunner
{
    Task<PersonErasureOutcome> RunAsync(Guid personId, Principal requestedBy, CancellationToken ct);
}
```

The implementation is the existing `ErasureCoordinator` (`Bazaar.Server.Components.Erasure`), which already: checks `ILegalHoldReader.IsPersonHeldAsync` first and fails closed; destroys the person's key (`IPersonKeyEraser`); then runs each `IErasureParticipant` (Vault memory files, the memory store and others) in order, each idempotent. The effect on the GC (DECIDED C01): every personal encrypted field of that person becomes unreadable; the message rows, sequence numbers, timestamps and the authorship id stay, and a tombstone marks what was erased. Other members' messages stay as written. DM co-members keep their own side. The person's `human.status` becomes `removed` and their devices are revoked by `ITokenService`.

What this means for the subject, which the pane must state in plain words before the request is confirmed: the structure of conversations stays; your name and your words in encrypted fields become unreadable; a pseudonymous id remains as author; copies you exported in plaintext are outside erasure (doc 44 section 10 item 2); backups keep the key until the backup window ends (`erasure.backup_window_days`, placeholder 35, doc 44 section 10); Vault page text and git history are not covered until the doc 48 section 9 OPEN is decided; third-party names inside agent text are not attributable. Whether key destruction counts as erasure in each jurisdiction is OPEN, counsel-gated (C01 (c)). The confirmation text comes from the active legal pack, so counsel owns the wording.

The outcome maps onto the case: `HeldQueued` returns the case to `on_hold` with hold kind `legal_hold` (queued, not refused, doc 48 section 9). A participant `Failed` keeps the case in `processing` and the sweep retries (a failed erasure attempt is not downgraded back to a cancellable stage; consistent with Resibo's rule that cancellation ends when processing starts, deep-dive 06 section 6.2). On success Guardian stores the signed erasure proof reference (subject as a salted hash, key ids destroyed, scopes, the backup expiry date, time, signing key, and the export manifest ids that held this person's ciphertext, doc 44 section 10 item 3) and writes `erasure.completed`.

Floors (not settings): the last owner cannot self-delete (doc 48 section 4.1; the case ends `cancelled` with code `last_owner`); an agent cannot request or advance a deletion; deletion of a person who is under a legal hold is never executed.

### 7.3 Hold hook

```csharp
public interface IDeletionHold
{
    string HoldKind { get; }                 // "billing", "legal_hold", "ownership", or "module:<id>"
    Task<DeletionHoldVerdict> CheckAsync(Principal subject, CancellationToken ct);
}
public sealed record DeletionHoldVerdict(bool Clear, string? ReasonCode, string? UserVisibleDetail);
```

Any module may register a hold (the module's manifest must declare it, doc 42). First-party holds: `legal_hold` (wraps `ILegalHoldReader`), `ownership` (the person is the last owner, or owns something that has no other owner such as a company-wide integration; the owner may transfer it), and `billing` when the Billing module is enabled (section 13). With no hold provider registered the answer is clear: a business-process gate with no backend has nothing to resolve (PROPOSED, Bazaar's design). A hold provider that throws or times out is treated as not clear (fail closed, which is stricter than Resibo's billing default and is chosen because Bazaar's `legal_hold` is a legal gate).

The sweep that advances cases is a registered job on the `jobs` module (`IJobHandler`), as Resibo registered its sweep in Background Workers. A case in `grace_period` with nothing checking it would sit there forever, which is the gap Resibo's deep-dive itself recorded. The sweep is idempotent: calling it on a case that is not due, or already resolved, is a no-op.

### 7.4 Access report

The "what do you hold about me" report lists the data categories held about the person, purposes, recipients (model providers and other processors, doc 05 and 32), and retention. It reads the doc 32 section 9 data inventory (PLANNED; a test checks the inventory against the schema) and doc 48 section 9 retention table. It is generated, not certified, and each report states its scope and date. Instance-level compliance reports (GDPR records of processing, SOC 2 style evidence) stay with doc 48 section 12 and are not duplicated.

### 7.5 Step-up, due dates and deadlines

Requesting deletion, regenerating recovery codes and producing a plaintext export need step-up (`IStepUpVerifier`, backed by the doc 48 section 8 assertion when it exists; OPEN: the service does not exist yet, so until it does these actions use the signed-session reauth that doc 32 defines). Each case gets `due_at` from the active legal pack (section 8). A missed due date notifies the owner (doc 32 behaviour) and raises the Kodo probe `guardian.dsr.overdue`.

## 8. Legal packs: legal text is data, not code

Resibo's deep-dive is grounded in the Philippine Data Privacy Act (RA 10173): a 30-day practical benchmark, erasure at no cost to the subject, and an NPC registration and DPO trigger at 1,000 individuals with sensitive data. Bazaar's privacy model is global and operator-chosen (doc 32 section 9 overlays `eu-uk`, `california`, `philippines`; doc 48 section 12 profiles `personal` and `company-global`). So nothing jurisdictional is hardcoded in Guardian. A **legal pack** is a provider module:

```csharp
public interface ILegalPackProvider
{
    string PackId { get; }                                     // matches a privacy.overlays value
    string Jurisdiction { get; }
    IReadOnlyList<ConsentDocumentTemplate> Documents(string locale);   // notice, privacy text templates
    TimeSpan ResponseDeadline(DsrKind kind);                   // sets due_at
    DeletionRules Deletion { get; }                            // min and max grace, allowed fee (none), confirmation text
    ExportRules Export { get; }
    IReadOnlyList<OperatorObligation> OperatorObligations { get; }  // informational checklist, never enforced
    string Version { get; }
    bool CounselApproved { get; }                              // false until counsel signs off
}
```

- Packs hold contracts and data only, no third-party dependencies. Each pack is its own module, enabled by the operator through the `privacy.overlays` list, and several may be active at once. When two packs disagree the stricter deadline and the more protective rule win (never the looser; settings may tighten, never loosen below the floor).
- The three overlay ids doc 32 already names map to packs `eu-uk`, `california`, `philippines`. The Philippines pack is where Resibo's RA 10173 material goes (the 30-day benchmark, no fee for erasure, the NPC registration and DPO checklist item). It enters as a draft carrying Resibo's research, labelled unverified for Bazaar, with `CounselApproved = false`.
- A pack with `CounselApproved = false` is shown to the operator as a draft and, under `company-global`, cannot be the only active pack for a public release (the check is a release gate, doc 47 counsel review). All deadline numbers in this doc are placeholders until a pack states them; the only one inherited from existing design is the 35-day erasure backup window placeholder (doc 44).
- No default pack text is presented as legal advice. Operators may replace pack text with their own, as Resibo's self-hosted operators replace its Terms and Privacy text.
- Resibo's hard pre-launch gate carries over: no real member signs up against unreviewed legal text for a public or hosted instance. For a private personal instance the pack default is the shipped draft with a visible "not reviewed by counsel" banner. OPEN: the owner to confirm this split.

## 9. Interaction with existing modules (what is reused, what is not duplicated)

| Existing | Used for | Not duplicated |
|---|---|---|
| `Bazaar.Server.Remote` and `.Abstractions` | `IRefreshCredentialStore` (devices), `ITokenService.RevokeDeviceAsync`, `DeviceInfo`, `PrincipalRef`; `privacy_request` table; passkey and recovery-code tables via providers | Session and token mechanics. Guardian keeps no session state |
| `Bazaar.Erasure`, `.Abstractions` | `ILegalHoldReader`; `ErasureCoordinator` through the new `IPersonErasureRunner`; `IErasureParticipant` | Key destruction, participants, hold storage |
| `Bazaar.PrivacyAudit` | `PrivacyAuditKinds` for existing kinds (`device.revoked`, `signin.*`, `recovery.code_used`, `export.completed`, `erasure.completed`, `access_request.*`, `role.changed`, `dm.owner_read`); `AuditPayloadGuard` for all payloads | Audit writing rules. New `guardian.*` kinds live in Guardian's own `GuardianAuditKinds`, since PrivacyAudit lists only kinds a doc names |
| `Bazaar.Approvals` | `IOwnerSignatureVerifier` for owner-resolved recovery and holds | Owner signature crypto |
| Doc 44 (data portability) | Per-person export jobs and bundle format; exporters are modules | Export formats. Guardian only creates the job and tracks the case |
| Doc 22 (email), doc 29 push, doc 15 approval cards | Reused by the Notifications module as channels and card references (section 12) | Mail pipeline, wake hints, cards |
| `Bazaar.Server.Jobs` | The deletion and export sweeps | A private scheduler |
| `Bazaar.Client.Push` | The device registers its push token; Guardian shows the push provider in the notification section | Push registration |
| Doc 43 (telemetry) | Guardian displays the company telemetry consent mode (off, ask, auto) read-only; the mode stays a per-company setting | Telemetry consent. It is per company, not per person, so it is not a Guardian consent record |
| Doc 48 roles | `admin.members` and `admin.privacy` capabilities gate the owner views | Roles |
| Kodo (doc 46) | Health contract and probes (section 16) | Health |

**Overlaps and conflicts found.** (1) `privacy_request` is owned by Remote and has no stage detail; Guardian adds `guardian_dsr_case` instead of writing Remote's table (rule: components never write another's tables). (2) Doc 44 section 4.6 produces plaintext personal fields only for the owner's session and defaults off, but a coworker's own access right needs a readable copy. Proposal: a subject may always receive plaintext of their own personal fields, to themselves only, flagged and audited, setting `account-guardian.export.plaintext_for_subject` default true; the instance-level default in doc 44 stays false. The owner decides (OPEN). (3) `PrivacyAuditRecorder` documents actors as `human:...`; `Principal` is `user:...` and a one-time mapping covers old values, so Guardian writes `user:`. (4) The existing `ErasureCoordinator` has a TODO to record `erasure.completed` once components merge; Guardian records the case-level events and leaves the coordinator's own TODO to its owner.

## 10. Data, settings and contracts

### 10.1 Tables (namespace `account_guardian`, SQL sketch)

```sql
-- 0001_dsr.sql
CREATE TABLE guardian_dsr_case (
    id               uuid PRIMARY KEY,
    subject_id       uuid NOT NULL,                    -- human id; no FK (no cross-module FK)
    kind             text NOT NULL CHECK (kind IN ('access','portability','deletion','correction')),
    stage            text NOT NULL,                    -- per-kind stage set, checked by the service and a CHECK per kind
    legal_pack_ids   text[] NOT NULL DEFAULT '{}',
    requested_at     timestamptz NOT NULL DEFAULT now(),
    due_at           timestamptz NOT NULL,
    grace_ends_at    timestamptz,
    hold_kinds       text[] NOT NULL DEFAULT '{}',
    artifact_blob_id uuid,                             -- export result, with expiry
    artifact_expires_at timestamptz,
    privacy_request_id uuid,                           -- link to remote.privacy_request, nullable
    proof_ref        text,                             -- audit-chain reference to the erasure proof
    completed_at     timestamptz
);
CREATE UNIQUE INDEX guardian_one_active_deletion ON guardian_dsr_case (subject_id)
    WHERE kind = 'deletion' AND stage NOT IN ('complete','cancelled');
CREATE INDEX guardian_dsr_due ON guardian_dsr_case (due_at) WHERE completed_at IS NULL;

CREATE TABLE guardian_dsr_event (                      -- append-only, trigger refuses update and delete (as legal_hold_event)
    id        bigserial PRIMARY KEY,
    case_id   uuid NOT NULL,
    at        timestamptz NOT NULL DEFAULT now(),
    actor     text NOT NULL,                           -- Principal canonical text "kind:id"
    from_stage text, to_stage text NOT NULL,
    detail    jsonb NOT NULL DEFAULT '{}'              -- codes and ids only; AuditPayloadGuard rules apply
);

-- 0002_consent.sql
CREATE TABLE guardian_consent_document (
    kind text NOT NULL, version text NOT NULL, locale text NOT NULL,
    requires_reconsent boolean NOT NULL DEFAULT false,
    required boolean NOT NULL DEFAULT true,            -- false = optional item with withdrawable consent
    text_ref text NOT NULL,                            -- Vault page or blob id; the text is operator content
    text_sha256 bytea NOT NULL,                        -- pins the exact text a person accepted
    pack_id text, published_at timestamptz NOT NULL, published_by text NOT NULL,
    is_current boolean NOT NULL DEFAULT false,
    PRIMARY KEY (kind, version, locale)
);
CREATE UNIQUE INDEX guardian_consent_current ON guardian_consent_document (kind, locale) WHERE is_current;

CREATE TABLE guardian_consent_event (                  -- append-only ledger; state = latest event per (subject, kind)
    id bigserial PRIMARY KEY, subject_id uuid NOT NULL, kind text NOT NULL, version text NOT NULL,
    action text NOT NULL CHECK (action IN ('granted','withdrawn')),
    at timestamptz NOT NULL DEFAULT now(), device_id uuid,
    ip_hash bytea                                      -- null by default (setting)
);

-- 0003_recovery.sql
CREATE TABLE guardian_recovery_case (
    id uuid PRIMARY KEY, subject_id uuid NOT NULL, opened_by text NOT NULL,
    lost_methods text[] NOT NULL, stage text NOT NULL,
    checklist jsonb NOT NULL DEFAULT '{}', time_lock_until timestamptz,
    resolved_by text, resolved_at timestamptz, outcome text
);
```

Personal fields: none of these tables hold free personal text. `subject_id` is a pseudonymous uuid, notes are code-only, and the checklist stores check names and yes or no, not the answers the person gave. So crypto-shred needs no special handling here. OPEN (counsel): whether consent events and case events must outlive erasure as evidence of lawful handling. The shipped default keeps them, because they are pseudonymous and doc 48 section 9 keeps the erasure proof in the audit chain.

### 10.2 Consent service

```csharp
public sealed record ConsentState(string Kind, string? AcceptedVersion, string CurrentVersion,
                                  bool RequiresReconsent, bool Required, bool Satisfied);

public interface IConsentService
{
    Task<IReadOnlyList<ConsentState>> ListAsync(Principal subject, CancellationToken ct);
    Task GrantAsync(Principal subject, string kind, string version, CancellationToken ct);
    Task WithdrawAsync(Principal subject, string kind, CancellationToken ct);   // optional items only
    Task<ConsentGate> CheckAsync(Principal subject, CancellationToken ct);      // allowed, or blocked with the open items
    Task PublishAsync(Principal owner, ConsentDocumentDraft draft, bool requiresReconsent, CancellationToken ct);
}
```

Taken from Resibo: acceptance is tied to a specific version; publishing a new version never counts as an old acceptance; `requires_reconsent` is a plain flag the owner sets per revision, never inferred from a diff (no algorithm reliably separates material from cosmetic legal changes); exactly one current version per kind enforced by a partial unique index; the document text is operator content (here a Vault page or blob), and Guardian stores a pointer and a SHA-256, never legal text of its own; `text_ref` is pinned so a record proves which text was shown.

Changed for Bazaar: the ledger is append-only with `granted` and `withdrawn` events because doc 32 section 9 requires per-item, withdrawable consent for optional items (Resibo's was accept-only); the joining-coworker notice that the owner can read DMs and that agents process messages (doc 32 section 9) is a built-in required document kind `member_notice`, and acceptance is audited as doc 48 section 12 requires; IP address is not collected by default (`consent.record_ip`, default false, stored as a salted hash if enabled), because Bazaar instances are private by default and Resibo itself said IP is not useful for single-tenant installs.

Enforcement: Resibo blocks a session without the current acceptance from everything except viewing and accepting. Bazaar does the same for a human's interactive write capabilities through an `IConsentGate` check that the authorizer calls (OPEN: that hook is a change in the Remote authorization seam, not yet specified). Floors: the gate never blocks the consent screen itself, sign-out, device revoke, a deletion request, a data export, the kill switch, or any owner security action, so no one can be locked out of leaving or protecting themselves. It never gates agents or system work.

### 10.3 Settings registry rows

Every row is audited. "Floor" means a value below which a change is refused.

| Key | Type | Default | Scope | Who | Notes |
|---|---|---|---|---|---|
| `account-guardian.enabled` | bool | true | install | owner | Core feature when the owner has a coworker; harmless for one person |
| `account-guardian.deletion.grace_days` | int | 30 | install | owner | Resibo's locked value, PROPOSED here. Packs set min and max. Setting 0 requires owner approval and counsel |
| `account-guardian.deletion.restrict_during_grace` | enum `none`,`read_only` | `none` | install | owner | Resibo stopped charges during grace; Bazaar has none by default |
| `account-guardian.deletion.require_step_up` | bool | true | install | owner | Cannot be turned off under `company-global` (tighten only) |
| `account-guardian.export.artifact_ttl_days` | int | 14 | install | owner | After that the artifact is deleted and the case becomes `expired` |
| `account-guardian.export.plaintext_for_subject` | bool | true | install | owner | See overlap (2) in section 9; OPEN |
| `account-guardian.recovery.time_lock_hours` | int | 24 | install | owner | Floor 1; applies to member recovery cases only |
| `account-guardian.recovery.require_voucher` | bool | true when more than one human exists | install | owner | |
| `account-guardian.consent.record_ip` | bool | false | install | owner | |
| `account-guardian.consent.block_on_reconsent` | bool | true | install | owner | Applies only to interactive human writes |
| `account-guardian.sessions.warn_single_method` | bool | true | install | owner | |
| `privacy.overlays` | list | existing (doc 32) | install | owner | Enables packs; changes need counsel (existing row) |

Each user-level preference (notifications, section 12) lives with the Notifications module.

### 10.4 Wire surface (sketch)

All routes are authenticated and scoped to the caller; none returns secrets.

| Route | Purpose |
|---|---|
| `GET /api/guardian/v1/sessions`, `DELETE .../sessions/{deviceId}`, `POST .../sessions/revoke-others` | Section 5 |
| `GET /api/guardian/v1/recovery/methods`, `POST .../recovery/codes/regenerate`, `GET/POST .../recovery/cases` | Section 6 (cases: owner capability) |
| `POST /api/guardian/v1/dsr/{kind}`, `GET .../dsr`, `GET .../dsr/{id}`, `POST .../dsr/{id}/cancel` | Section 7 |
| `GET /api/guardian/v1/access-report` | Section 7.4 |
| `GET /api/guardian/v1/consent`, `POST .../consent/{kind}/grant`, `POST .../consent/{kind}/withdraw` | Section 10.2 |
| `POST /api/guardian/v1/consent/documents` | Publish (owner) |

Errors use the doc 46 shape (`code`, `message`, `retryable`, `correlationId`). Failures are data, never an exception across the wire, as in Resibo.

## 11. The client pane (`Bazaar.Client.AccountGuardian`)

The pane is registered through the pane registry (doc 41) and opened by a mnemonic from the palette, no Ctrl chords. Proposed mnemonics: `guard` (opens the pane), `gdev` (devices), `gdata` (my data), `gconsent`. They are PROPOSED and subject to the registry. Sections: Devices, Sign-in and recovery, My data (requests, access report), Consent, Notifications, My activity, and for the owner a Members view (open cases, overdue requests, recovery cases).

Rules: the server computes everything and the client renders; keyboard reachable, screen-reader labels on every state and stage word, text alternative for any chart (none are planned), i18n strings for all words. Destructive actions (revoke, deletion) show a confirmation that states the effect in plain words, from the legal pack for deletion. Android shows the pane in the compact layout and deletion confirmation needs the platform biometric prompt (doc 29). The terminal head (Calcinato) exposes the same sections as commands. Offline: the pane shows the last snapshot with its age and disables every action, because a revoke that did not reach the server must never look done.

## 12. Optional module: Notifications (PROPOSED)

### 12.1 Gap check

Bazaar has content-free wake hints and three wake kinds plus a quota kind (`WakeKind` in `Bazaar.Client.Push.Abstractions`; doc 29, doc 02), approval cards posted into GCs (doc 15), the agent-side Interrupt System (doc 24, per-agent stack, not for humans), and email for agents and external mail (doc 22). None of these is a per-person inbox, a channel registry or per-person preferences. `grep` of the design set and `src` finds no such module. So `Bazaar.Server.Notifications` is proposed. It is optional (soft dependency of Guardian and of any module that wants to tell a person something); Guardian works without it and shows "unavailable" in that section.

### 12.2 Reuse, not duplication

- **Push** stays doc 02's wake hint and `Bazaar.Client.Push` providers (FCM, UnifiedPush). Notifications adds a `wake` channel adapter that asks the existing wake path to send a hint of the right `WakeKind`; it adds no push code. OPEN: the server-side wake sender (doc 02 wake package) is not in `src` yet.
- **Approval cards** remain GC cards (doc 15). A notification for an approval holds a reference to the card's GC and message, never a copy of the payload.
- **Email** goes through doc 22's outbound sender; Notifications adds an `email` channel adapter and no mail code. OPEN: name the sender interface when doc 22's module lands.
- **Interrupts** stay agent-only. A human is not given an interrupt stack.

### 12.3 Design (small)

Modules: `Bazaar.Server.Notifications` and `.Abstractions` (id `notifications`); channel adapters as provider modules `...Notifications.Wake`, `...Notifications.Email`, and optional SMS providers `...Notifications.Sms.Twilio`, `...Sms.Semaphore`, `...Sms.PhilSms` (the three Resibo shipped; off by default because SMS costs money, with Resibo's guided setup step including sender-name registration for the two Philippine providers). Doc 22 and doc 29 stay the owners of their channels; SMS providers are the only new external services, and each is a separate module with its own manifest and terms check.

```csharp
public sealed record Notification(Guid Id, Principal Recipient, string Category, string TitleKey,
    IReadOnlyDictionary<string,string> Params, SourceRef? Reference, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public interface INotificationChannel
{
    string ChannelId { get; }                     // "inbox", "wake", "email", "sms:twilio"
    ChannelClass Class { get; }                   // InApp, ContentFree, ExternalText
    Task<DeliveryStatus> SendAsync(Notification n, ChannelAddress to, CancellationToken ct);
}

public interface INotificationPreferences
{
    Task<NotificationPrefs> GetAsync(Principal p, CancellationToken ct);
    Task SetAsync(Principal actor, NotificationPrefs prefs, CancellationToken ct);
}
```

Taken from Resibo: the in-app inbox notification is always created, whatever the outbound outcome; a notification carries a reference, not a copy of the event; outbound channels are opt-in; failed sends retry three times with backoff over a day, then surface to the operator (in Bazaar through Kodo, section 16); consumers decide when to notify, the module decides how.

Changed for Bazaar and why: external channels are content-free by default (the wake and SMS bodies say only "something needs your attention"; `notifications.external_include_text`, default false) because Bazaar's push is content-free by decision (C41) and private by default; titles and params are keys and ids, stored in a table whose text columns are per-person encrypted so crypto-shred reaches the inbox too; preferences are per category from the start (Resibo deferred it) because Bazaar already has distinct wake kinds (mention, DM, owner decision, quota). Floors (not settings): a `security` category (new device, recovery started, deletion progress, DM owner-read marker where on) always reaches the in-app inbox and at least one chosen channel, and cannot be muted. Quiet hours per person are a preference (default off); the `security` category bypasses them.

```sql
CREATE TABLE notification (            -- namespace notifications
    id uuid PRIMARY KEY, recipient_id uuid NOT NULL, category text NOT NULL,
    title_key text NOT NULL, params_enc bytea NOT NULL,    -- per-person encrypted
    ref_kind text, ref_id uuid, created_at timestamptz NOT NULL DEFAULT now(), read_at timestamptz
);
CREATE TABLE notification_pref (recipient_id uuid, category text, channel text, enabled boolean,
    quiet_start time, quiet_end time, PRIMARY KEY (recipient_id, category, channel));
CREATE TABLE notification_delivery (id bigserial PRIMARY KEY, notification_id uuid, channel text,
    attempt int, status text, at timestamptz DEFAULT now());
```

Work packages for it are in section 18. It declares an `IErasureParticipant` named `notifications`.

## 13. Optional module: Billing (PROPOSED, off by default, short)

For an operator who sells access to their Bazaar instance. Bazaar the project sells no access (doc 47: AGPL plus a "contact us" commercial licence; pricing and the commercial form are OPEN, M10), so billing is operator-side and a separate module, never part of Guardian.

- Modules: `Bazaar.Server.Billing`, `Bazaar.Server.Billing.Abstractions` (id `billing`, default disabled). PSPs are provider modules behind `IPaymentProvider` (create charge, verify webhook, check status, cancel), as in Resibo: `Bazaar.Server.Billing.PayMongo` and `...Xendit` carry over (both Philippine aggregators, tokenizing, so the module stays out of card-data scope); other PSPs are future provider modules. The module has no third-party dependency; each provider has its own.
- A `Subscription` has a status (`active`, `past_due`, `cancelled`, `pending_deletion_hold`); the last is what `IDeletionHold` (kind `billing`) reads, as in Resibo's `BILLING_HOLD`. A deletion request stops new charges at once and the hold clears when the final invoice, proration and refund are resolved. Deletion is never paywalled and never charged a fee.
- Two independent proration policies carry over: upgrade (`immediate_charge`, `next_cycle`, `generous_deferred`) and downgrade (`immediate_refund`, `credit_next_cycle`, `no_refund`).
- Webhooks must be signature-verified before use (fail closed). OPEN: the doc 02 HTTP surface is the wire adapter and has no seam for a module to register a webhook route.
- **Boundary with a rejected decision.** Resource Economy (money budgets) is REJECTED, and doc 48 section 11.2 says cost never gates an agent. Billing is operator revenue for human access. It must never stop, throttle or price agent work. OPEN: whether any Bazaar module will ever declare tier gates. Without one, the module only records subscriptions and holds.
- Licensing of the Bazaar software itself is not here (Resibo's Keymaster analogue is doc 47's problem). Secrets (PSP keys) live in the Identity Service (doc 14), never in settings.
- Settings (all default off): `billing.enabled=false`, `billing.provider` unset, `billing.proration.upgrade=immediate_charge`, `billing.proration.downgrade=immediate_refund`.

OPEN: who is the billed subject (`user:` or a company); how a free instance with no PSP behaves (the module is not loaded). Everything in this section is optional and may be dropped by the owner without affecting the rest of the doc.

## 14. Audit events (all use `Principal`)

Guardian writes with the canonical `kind:id` actor. Existing kinds are reused where the event is the same. New kinds are `GuardianAuditKinds`, dotted lowercase, payload fields are ids, codes and labels only (never document text, never a free-text reason), validated by `AuditPayloadGuard`:

| Kind | Actor | Payload fields |
|---|---|---|
| `device.revoked` (existing) | `user:<id>` or `user:<owner>` | device id, reason label |
| `guardian.sessions.revoke_others` | `user:<id>` | kept device id, count |
| `guardian.recovery.codes_regenerated` | `user:<id>` | count |
| `guardian.recovery.case_opened` / `.checklist_recorded` / `.resolved` / `.cancelled` | `user:<owner>` or `user:<subject>` | case id, stage, lost method labels |
| `guardian.dsr.requested` / `.stage_changed` / `.cancelled` / `.completed` | `user:<subject>`, `system:guardian` for sweeps | case id, kind, from and to stage, hold kinds |
| `guardian.dsr.export_delivered` | `system:guardian` | case id, artifact id, manifest hash |
| `export.completed`, `erasure.completed` (existing) | per source | existing fields |
| `guardian.consent.granted` / `.withdrawn` | `user:<id>` | kind, version, document hash |
| `guardian.consent.published` | `user:<owner>` | kind, version, requires reconsent flag, hash |
| `guardian.legal_pack.changed` | `user:<owner>` | pack ids |
| `guardian.hold.registered_unclear` | `system:guardian` | hold kind, reason code |

Resibo's audit vocabulary is a closed list (`supervisor/CLAUDE.md`); Bazaar's audit takes arbitrary kinds through `AuditEntry`, so that gap does not recur, but each new kind still needs a test (section 17).

## 15. Security and privacy

- **Authority.** Every route resolves the caller to a `user` principal and the subject to the same person, except owner-assisted flows (recovery cases, owner publishing documents, owner viewing the member queue). The owner can see that a request exists, its stage and due date, and which holds apply. The owner cannot read a coworker's export or access report; those go to the subject only. This is stricter than the owner's DM reading right, and is chosen so a DSR cannot be a side door.
- **Taint.** Guardian takes no model-readable input. Agents cannot invoke it. DSR free text is not accepted; reasons are codes, so no prompt-injection path exists through a case record.
- **No secrets in output.** No endpoint returns key material, hashes of credentials, raw user agents, IPs or document text.
- **Fail closed** on hold checks, hold providers, and any step-up or signature check. A degraded dependency never lets a deletion skip a hold.
- **Rate limits.** DSR creation, recovery-code regeneration and consent publishing use the doc 15 quota table with audited breaches.
- **Erasure sufficiency.** Mechanically specified, legally OPEN (counsel). The confirmation text and proof wording are pack content.
- **Retention.** Cases, events and consent ledger are kept per the doc 48 section 9 retention table; their category row is part of the data inventory so the inventory test sees them.
- **Principle of least surprise for the subject.** Before confirming deletion the person sees plainly what stays (conversation structure, pseudonymous author id), what becomes unreadable, what is outside reach (plaintext exports, Vault history until decided, backups until the window ends).
- **Privacy law differs from Resibo's.** Resibo's text is RA 10173. Bazaar's overlays are multi-jurisdiction. Section 8 keeps jurisdictional rules in packs so that no deadline, fee rule or registration duty is hardcoded.

## 16. Kodo health contract for the module

`Bazaar.Server.AccountGuardian` implements `IModuleHealth` (doc 46 section 10) and its manifest has a `health` block. Hard dependencies declared: `audit`, `remote`, `erasure`, `jobs`. Soft: `notifications`, `billing`, `data-portability`.

- **Liveness:** no I/O; the sweep handler and clock are present.
- **Readiness:** `Fail` if `remote`, `erasure` or `jobs` is not ready; `Warn` if no legal pack is enabled, if a pack has `CounselApproved = false` under `company-global`, or if a soft dependency is missing.
- **Self-test:** `Quick` validates the stage transition tables and consent index logic in memory. `Full` runs in a rolled-back transaction: opens a deletion case for a synthetic person, advances it through `grace_period` with a fake clock, checks the hold path with a fake hold provider, and asserts no residue.
- **Probes** (must be in the signed catalogue; proposed ids): `guardian.dsr.overdue` (cases past `due_at`, non-critical), `guardian.deletion.stuck` (a case in `processing` longer than the setting, non-critical, critical if the key destroyer failed), `guardian.sweep.lag` (age of the last sweep, non-critical), `guardian.consent.pending` (members blocked on re-consent past a grace, non-critical).
- Notifications and Billing declare their own contracts. `notifications` adds probe `notifications.delivery.failures` (final failures in a window). `billing` adds `billing.webhook.verify_failures`.

## 17. Testing (xUnit v3; Avalonia.Headless for the pane)

- **State machines.** Every valid and invalid transition for each case kind asserted, including that `cancel` fails once `processing` starts rather than silently succeeding (Resibo test, kept).
- **Hold interaction.** Deletion with a fake `billing` hold enters `on_hold` and resumes when clear; a hold that throws keeps it held; `legal_hold` returns `HeldQueued` from the real coordinator and the case stays queued and nothing is destroyed.
- **Append-only GC.** With the real `ErasureCoordinator` and a test database: after `processing`, GC rows, sequence numbers, timestamps and author ids are unchanged, encrypted personal fields are unreadable, a tombstone exists, other members' messages are intact.
- **Last owner.** Self-delete and last-device revoke of the last owner are refused.
- **Sessions.** Ownership check (cannot revoke another person's device), revoke failure surfaces as an error, revoke closes sockets and revokes the approval key (fake `ITokenService`), "revoke others" keeps the named device.
- **Export completeness.** A regression test builds a person with data in every registered exporter and asserts the per-person export contains each class, so a new table without an exporter fails CI (Resibo's completeness check). The test fails when a new data class has no exporter or is not listed as exempt.
- **Consent.** Version pinning; a new version does not satisfy old acceptances; `requires_reconsent` blocks interactive writes and never blocks the floors list; withdrawal on an optional item works and on a required item is refused; one current version per kind.
- **Packs.** Stricter-of-two merge; a pack with `CounselApproved = false` raises the readiness warning; no jurisdictional constant exists outside packs (architecture test searching Guardian assemblies for deadline literals).
- **Architecture.** Guardian references no other implementation project; the SQL lives in its namespace folder; seeded-violation tests (BAZAAR-COMPONENTS section 6).
- **Audit.** Each kind is written with a `user:` or `system:` actor and rejected payloads fail `AuditPayloadGuard`.
- **Kodo contract tests** from `Bazaar.Kodo.Testing` (`ModuleHealthContractTests<T>`), including liveness without I/O and residue-free self-test.
- **Pane.** Headless tests for each section, keyboard reach, state words present, offline disabling of actions, confirmation text shown for deletion.
- **Notifications and Billing.** In-app row always written even when every outbound channel fails; three-attempt retry then failure surfaced; muted channel gets nothing but `security` cannot be muted; webhook with a bad signature is rejected; deletion-hold transitions for `pending_deletion_hold`.
- Not testable in CI: real push and SMS delivery, real PSP calls, legal sufficiency.

## 18. Work packages

Each is its own project or file with tests. Order is by dependency.

| WP | Content | Depends on |
|---|---|---|
| G0 | `ListDevicesForHumanAsync` on `Bazaar.Server.Remote.Abstractions`; `IPersonErasureRunner` on `Bazaar.Erasure.Abstractions`; per-subject audit read port request to doc 48 owner | none |
| G1 | `Bazaar.Server.AccountGuardian.Abstractions` (contracts in this doc, `GuardianAuditKinds`) plus component manifest | G0 |
| G2 | Case state machines, `guardian_dsr_case`/`event` migrations, in-memory and Postgres stores | G1 |
| G3 | Sessions service over Remote abstractions | G0, G1 |
| G4 | Deletion processing: hold registry, `legal_hold` and `ownership` holds, runner adapter, sweep job | G2, G0 |
| G5 | Export and access cases; per-person export job adapter over doc 44; access report over the data inventory | G2 (doc 44 exporters; stub until they exist) |
| G6 | Correction case and `IDataCorrectionParticipant` | G2 |
| G7 | Consent service, migrations, enforcement gate seam | G1 |
| G8 | Recovery cases, `ISignInMethodProvider` and Remote-side providers | G0, G1 |
| G9 | Legal pack contract and the three draft pack modules (data only, counsel flag false) | G1 |
| G10 | Wire contracts and `/api/guardian/v1/` routes | G3 to G8 |
| G11 | `Bazaar.Client.AccountGuardian` pane (headless tests) | G10 |
| G12 | Kodo health, probes, contract tests | G2, G7 |
| N1 | `Notifications.Abstractions`, inbox table, preferences, always-in-app guarantee, retry | none (G-independent) |
| N2 | Channel adapters: `Wake`, `Email` (reuse doc 29 and doc 22), then SMS provider modules | N1, doc 02 wake sender, doc 22 sender |
| N3 | Guardian notification section and pane tie-in | N1, G11 |
| B1 | `Billing.Abstractions`, `IPaymentProvider`, subscription store, `billing` hold, off by default | G4 |
| B2 | PayMongo and Xendit provider modules, webhook verification | B1, doc 02 webhook seam |

## 19. Open questions for the owner

1. **Plaintext of a subject's own personal fields in their export** (section 9 overlap 2): allow by default (proposed) or follow doc 44's default of off?
2. **Ownership of `privacy_request`.** Keep it with Remote and link from Guardian (proposed), or move ownership to Guardian in a later wave?
3. **Deletion grace default** of 30 days, and whether a person may waive it for immediate deletion (counsel).
4. **Recovery of a coworker** via an owner-opened case with no unauthenticated intake (proposed). Confirm this keeps the sign-in page's three-route fence.
5. **Consent enforcement hook** in the authorizer, and the floors list in section 10.2.
6. **Legal packs:** the first pack list (doc 48 section 12 is still OPEN), and whether a private personal instance may run on draft packs with a banner.
7. **Notifications module:** approve as a module (section 12), including SMS providers (cost, off by default).
8. **Billing module:** approve as an optional seam at all, and whether any tier gates are in scope.
9. **Counsel items (OPEN):** sufficiency of key destruction as erasure in each jurisdiction (C01 (c)); retention of consent and case events after erasure; third-party names in agent text; Vault and git history erasure; the 35-day backup window placeholder.
10. **Step-up service.** Until doc 48 section 8 biometrics exist, the interim step-up for deletion and code regeneration is a signed-session reauth. Confirm.

## 20. What is taken from Resibo, and what is changed

| Resibo source | Taken | Changed for Bazaar, and why |
|---|---|---|
| Deep-dive 06 section 4 | Thin user-facing view over the owner of sessions; ownership check; parsed summary; revoke errors are visible | Session is a device with a refresh chain (doc 32); revocation via `ITokenService`; last-owner floor |
| Deep-dive 06 section 5 and its checklist | Recovery is staff-mediated, not automated; verification checklist with no single sufficient check; vouching by a second human | Staff is the owner; no unauthenticated intake; time lock and cancel by the member's other devices; separate from doc 48 owner recovery |
| Deep-dive 06 (second SSO provider linking) | Linking a second sign-in method | Generalised to `ISignInMethodProvider`; no SSO module proposed since Bazaar has none |
| Deep-dive 06 section 6 | Export as just another exporter; grace period; cancellable until processing; sweep as a registered job (Bazaar's one-active-deletion partial index is PROPOSED, not from Resibo); 30-day default | Doc 44 pipeline; generalised hold hook (`on_hold` replaces `BILLING_HOLD`); crypto-shred and tombstone instead of row deletion; legal hold queues the request; proof recorded; correction kind added |
| Deep-dive 06 section 7 | Version-tied consent; owner-set `requires_reconsent`; one current version; text is operator content; enforcement gate | Append-only with withdrawal; text hash pin; no IP by default; floors on the gate |
| `docs/PRINCIPLES.md` section 1.3 | One small adapter per external dependency; capability gaps named at their site, never guessed | Ports in Abstractions; gaps in section 5.1 and G0 |
| Deep-dive 09 | In-app always created; reference not copy; channel providers; opt-in; bounded retry; SMS providers off by default | Content-free external channels; per-category prefs; per-person encrypted columns; push and email reuse; security floor |
| Deep-dive 22 | Off by default; PSP interface with two providers; proration policies; deletion hold status; webhook verification | Operator-side module; never gates agents (Resource Economy REJECTED); secrets in the Identity Service |
| RA 10173 material in deep-dive 06 section 6.1 | Research input | Moved into the Philippines legal pack as an unreviewed draft, not code |

Not adopted: Resibo's SQLite store (Bazaar is Postgres), gRPC and protobuf surface (wire is JSON, DECIDED), a Gateway and webapp route guard (no web client), and its Keymaster self-hosted licensing split (doc 47 covers licensing).
