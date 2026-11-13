# Gap decisions: security, identity, recovery and privacy

Status: DECIDED (owner gap cards, 2026-11-13) for every decision in this doc. The mechanisms under each decision are design intent. Nothing here is built yet: PLANNED.
Depends on: `03-server-host-and-providers.md` (providers, settings registry), `04-postgres-schema-and-migrations.md` (per-person keys, append-only tables), `14-identity-service-and-passport.md` (Identity Service, `ISecretStore`, Passport), `15-security-taint-and-approvals.md` (taint, tiers, approvals, audit, kill switch), `32-remote-access-auth-and-privacy.md` (roles, pairing, sign-in, privacy profiles), `33-backup-dr-and-export.md` (backups, erasure window, export), `09-work-life-simulation.md` (quotas, not money).
Used by: every doc that adds a module, a human or agent data access, a credential, a backup or a personal field.

## Purpose

This doc records the owner's decisions for twelve gaps that the earlier design set did not cover fully: module trust, developer sandboxes, the verified tag, roles, the audit log, disaster recovery, owner recovery, biometrics, retention, secrets, AI agent safety and privacy law. Each section gives the decision (DECIDED), the design consequences that follow, cross-links, and the OPEN details that the owner has not settled.

Principles that apply to every section:

- Overengineered options were chosen on purpose. Where two options are close, the more robust one is the design. A future problem must cause no damage.
- Settings may tighten a floor, never loosen it. Floors are listed per section.
- Legal and privacy exposure settings default to the most protective option and carry a "needs counsel" note. This is not legal advice.
- Each new persistent table, event or setting is registered in its owning doc (see `03` for settings and `15` for audit and quotas).

## Conflicts and overlaps with the existing set

These are flagged, not silently resolved. The owner's 2026-11-13 decision wins, and the older doc needs a change in the same pass.

| # | Where | Conflict | Treatment in this doc |
|---|---|---|---|
| K1 | `32` section 6 (Roles and permissions, PROPOSED) | Fixed principals and a fixed capability table (owner, coworker, agent, service). The owner decided custom roles. | Custom roles win (section 4). Doc 32 section 6 becomes the built-in role templates. |
| K2 | `15` Settings and "Audit log and kill switch" | Audit logs tool calls, approvals, leases, egress and config changes. The owner now requires every data read and write by humans and agents, plus admin and security events. | Scope expanded (section 5). Volume and privacy rules added. Doc 15 needs its audit paragraph rewritten to point here. |
| K3 | `33` Q1, Q2 and Q4 (KEK portability, KEK holders, migration credentials) | Open questions on sealed key copies and holders. | Answered in part by the encrypted recovery bundle (section 6) and the backup-owner quorum (section 7). Doc 33 questions stay open for the KEK custody detail only. |
| K4 | `15` Tier 3 and `32` section 6 | Tier 3 lists passkeys and recovery codes as human-performed. The owner now requires biometric verification for sign-in, recovery and high-risk actions. | Compatible: the owner performs the biometric step on a device. Doc 15 Tier 3 text should cite section 8. |
| K5 | `15` quotas and cost | The owner asks for "token and cost budgets". Resource Economy (money budgets) is REJECTED. Owner chose token and quota budgets only (2026-11-13). | Token budgets are quotas (section 11). Cost is display and analysis only. Resolved as 11.2. |
| K6 | `32` section 9 and `33` (retention) | Retention per category with GC entries kept by default. The owner's retention decision adds legal hold and signed erasure proof. | Compatible. Section 9 adds the hold and the proof. |
| K7 | Verified tag vs `15` "verified ServiceTerms" | Doc 15 uses "verified" for platform terms (Deny on unverified platforms). The owner's verified tag is a marketplace module status. | Two different meanings. This doc uses "Verified tag" only for modules. Doc 15 should rename its term to "terms-checked platform" to avoid confusion. |

## 1. Module trust: signed modules and declared permissions

**Status: DECIDED.** Modules are signed. Every module declares the permissions it needs, and the owner approves those permissions at install.

Design consequences:

- A module package carries a manifest (`bazaar-module.json`, PLANNED) with: module id, semantic version, publisher id, licence (SPDX expression), target API version, dependencies, and a permission list. Every permission has a class (data read, data write, GC post, network host, filesystem path, secret name, UI pane, background job, process spawn, device capability) and a scope.
- The package is signed by the publisher key. The server verifies the signature on download, on install and on every start. A module with a bad or missing signature is not installed. Unsigned modules are not installable, with or without a warning. (This is the consequence of "signed modules"; it is not a setting.)
- Publisher keys are registered in the server trust store with a publisher id, a key id and validity dates. First-party modules are signed with keys that stay on the server, per the build flow in `37`.
- Install shows the requested permissions in plain language and the licence. The owner approves the set once per module version. The approval is an audited event (`module.permissions.approved`), signed by the owner's device key (`15`).
- Any update that widens permissions needs a new approval. Narrowing needs none. Permission diffs are shown in the update card.
- At runtime the module host enforces the approved set. A call outside the set is refused and audited. A module cannot read a secret it did not name, and cannot reach a host it did not name.
- Modules run in the process model of their tier (see section 2 for developer sandboxes, and `03` for the host). Production modules run in the server host, with the permission gate in front of every service they receive.

Cross-links: `03` (module host, providers), `15` (`ToolDispatcher`, capability classes), `37` (signed releases, packaging), `14` (secret names for modules), section 3 (verified tag).

OPEN: none for the decision. Implementation choice of signature algorithm and certificate chain format is for the spike in `35`.

## 2. Developer sandboxes (full tier)

**Status: DECIDED.** The developer sandbox is the full tier: process isolation, operating system confinement, a fake-data test company, WebAssembly or a virtual machine by module type, and every sandbox action audited.

Design consequences:

- Each sandbox run starts a separate process for the module under test. The process is confined with the platform's strongest mechanism through a provider interface (`03`): on Linux, namespaces, seccomp, cgroups and Landlock; on Windows, a job object with restricted tokens and an AppContainer where available. OS providers are implemented separately, and the sandbox refuses to start on an OS whose provider is not tested.
- The execution engine is chosen per module type. Pure logic and data transforms run in WebAssembly with no host imports except the granted permissions. Modules that need native code, a runtime or an engine plugin run in a virtual machine from the agent VM image family (`16`) or a dedicated sandbox VM.
- The fake-data test company is a generated company. It contains synthetic people, agents, GCs, DMs, repositories, Vault pages, mail and secrets with canary values. It never contains real data. The sandbox company is created from a template by the same provisioning path as real installs (the server setup path in `27` and `36`). Its network egress is blocked except to named test endpoints.
- Every action in a sandbox run (system calls where the platform allows, host API calls, file access, network attempts, permission checks) is written to a sandbox audit stream. The stream is attached to the module's verified-tag run (section 3).
- A sandbox run cannot write to a production company. The sandbox database is a separate schema with a separate role. Promotion to production goes through the normal install path.
- Module authors get the same runner from the CLI and from the Bazaar clients. Runs are replayable (section 11).

Built status: PLANNED. The spike for the sandbox per OS is listed in `35`.

OPEN: the exact VM image for sandbox runs (shared with `16` golden images). Not a blocker for the decision.

## 3. Verified tag

**Status: DECIDED.** The marketplace shows a Verified tag on a module version after automated checks, a human review and a monitored sandbox run. A later version that fails any check has its tag revoked automatically. Unverified modules can be installed with a warning.

Design consequences:

- The automated checks are: licence (SPDX detection, compatibility with AGPL-3.0 under the AGPL module exception (owner decision, 2026-11-13), and the dependency licences), malware (static scan of every file, plus a signature-based scanner behind an interface), vulnerabilities (SBOM against an advisory feed, see `37`), and signature (valid publisher key, unrevoked, within validity).
- The human review is recorded per version: reviewer identity (a human with the reviewer role, section 4), date, scope reviewed, and the findings list. An agent's review never counts for the tag. The owner's standing is the same as other reviewers unless the owner delegates otherwise.
- The monitored sandbox run (section 2) is the dynamic check. Undeclared access (a call, host, file or secret outside the approved permissions) fails the run.
- The tag is per module version. Results are stored with the run's audit stream. The marketplace shows the checks as a list with the date of each.
- Auto-revocation: when a later version of a module fails a check, the tag is removed from that module line from that version onward. Installed copies show a revoked notice. Installed copies keep working unless the owner's update policy (carried by `37`) or the permission approval says otherwise. The revocation is audited and the publisher is notified.
- Unverified modules can be installed. The install card shows a warning, the licence, the permission list, and the reason the tag is missing. The warning cannot be disabled by a setting. The owner can set a company rule to refuse unverified modules (a tightening, allowed by the setting floor rule).
- The verified tag never bypasses the permission approval. It is information for the owner's decision.

Built status: PLANNED.

OPEN: the reviewer pool for a public marketplace and the reviewer SLA. These depend on the marketplace launch, which is outside this doc.

## 4. Roles (custom)

**Status: DECIDED.** Roles are custom. The owner defines roles as named sets of capability grants over scopes. Built-in templates ship as starting points.

Design consequences:

- A role is a row in a `role` table with a name, description and a list of grants. A grant is (capability, scope). Capabilities are the same classes as the tool dispatcher uses (`15`), plus administrative capabilities (`admin.members`, `admin.devices`, `admin.privacy`, `admin.backups`, `admin.modules`, `admin.roles`, and so on).
- Scopes are: instance, company, GC, GC tag, module, repository, or an agent group. A scope grant covers its children.
- Built-in templates are: owner, coworker, reviewer, read-only observer, and agent. These mirror the table in `32` section 6 and are copies that the owner may edit. The owner role is protected: the owner always keeps the floors in section 4.1.
- Principal kinds are unchanged: human, agent, service. A role is granted to a principal. Agent principals may hold roles, but a role that carries human approval (`15` Tier 2 approval) cannot be granted to an agent. This is a floor.
- Every change to a role, a grant or a membership is audited with the actor, the diff and the time. Changes take effect at the next request, with no caching longer than the token lifetime.
- Role changes that widen a grant on a human principal require the owner, the signed approval (`32` section 5a) and a biometric check (section 8).

### 4.1 Floors (not settings)

- At least one human owner exists at all times. The last owner cannot be removed except by the recovery flow (section 7).
- No role can read audit data that is hidden from the owner, except the owner.
- No role can grant itself or another principal an approval capability.
- Agents never hold `admin.*` capabilities.

Built status: PLANNED. Doc `32` section 6 is updated to point here.

OPEN: none for the decision. The default set of built-in templates is a list the owner can change in the setup wizard.

## 5. Audit log

**Status: DECIDED.** The audit log is tamper-evident. It uses a hash chain, signed checkpoints, external anchoring with Merkle inclusion proofs, and a multi-sink design. It records admin and security events, and every data read and write by humans and agents.

Design consequences:

### 5.1 What is recorded

- Admin and security events: sign-in, pairing, device change, role change, grant change, approval request and decision, lease issue and revoke, mount attach and detach, canary hit, kill-switch event, module install, permission approval, setting change, backup, restore, erasure request and erasure proof, legal hold, export.
- Data events: every read and write of a data object by a human or agent. The object is recorded by id and type, never by content. A read record holds: principal, session, tool or route, object id and type, purpose tag when given, result count, taint tags (`15`), and the time.
- Data reads are batched into compact rows. A bulk read of a large range records one row with a range descriptor and count, plus a row per object for objects that are personal. The rule is that every object a human or agent touched can be reconstructed.
- Changes to content (GC messages, Vault pages, Kanban) are recorded with the version id, not the content. Content versions already exist in the append-only tables (`01`, `18`).

### 5.2 Chain and checkpoints

- Each audit row carries `seq`, `prev_hash` and `row_hash`. The row hash covers the row fields after field encryption (section 5.5), so erasure of an encrypted field does not break the chain.
- Every N rows or T minutes (both settings), the audit service writes a checkpoint. A checkpoint holds the chain head, the row count, a Merkle root over the rows since the last checkpoint, and a signature by the audit signing key. The signing key is held by the Identity Service (`14`) and is never readable by agents.
- Checkpoints are signed daily as a minimum, in addition to the size and time rule.
- A verifier (a CLI and a Kodo check, PLANNED) recomputes the chain from any starting checkpoint and reports the first mismatch.

### 5.3 External anchoring and Merkle proofs

- Each checkpoint hash is anchored to at least one external target. Targets are pluggable: an RFC 3161 time-stamp authority, an operator-controlled offsite Git remote, or a public transparency log. The operator chooses one or more. The shipped default is one RFC 3161 target and one offsite Git remote, both configurable. Anchors are outbound only (`33`).
- A Merkle inclusion proof can be produced for any row against an anchored checkpoint. The proof is exported as a file the owner can verify offline with the verifier.
- Anchor failures are audited and raise an alert. Chain writing continues; the gap is anchored at the next success.

OPEN: the default external target set and the public transparency log choice. The owner has chosen anchoring, not the vendor. Needs counsel for any target outside the operator's control.

### 5.4 Sinks, replica and views

- Multi-sink. The primary sink is an append-only Postgres table (`audit_log`, `15`) under a role with no UPDATE or DELETE. Secondary sinks are optional: a signed daily file export (`15`), a syslog or OpenTelemetry stream, and an optional encrypted offsite replica. The replica is encrypted with a key held by the owner (`33` recovery bundle key class).
- The replica is write-only from the server. The server cannot read back from the offsite copy except through the verifier.
- Role-scoped views. The audit viewer shows only the rows the viewer's role grants (section 4). Security reviewers see security events; the owner sees all; agents see nothing. A view never changes the chain.

### 5.5 Field encryption and per-subject keys

- Personal fields in audit rows (for example a name in a sign-in event, or a purpose note that names a person) are encrypted with a per-subject key. The key is in the per-person key table (`04`), so erasure destroys the key (section 9).
- Non-personal fields stay in clear, so the chain can be verified after erasure without the keys.

### 5.6 Anomaly alerts and auto-containment with undo

- Detections are deterministic rules first (for example a burst of reads on a personal object class, a read outside an agent's scope, a canary hit, an approval flood), and later optional model-assisted scoring. Each rule has a threshold, a window and a severity.
- Auto-containment actions are the kill-switch actions in `15` for one agent or one session: revoke leases, detach mount, block egress, pause sessions. Global containment (freeze writes) needs the owner's role.
- Every containment has an undo. The undo window is a setting with a floor. Undo restores the prior state and is itself audited. Undo of a containment that is still in force needs the owner's biometric check (section 8).
- Alerts go to the owner's devices and to security reviewers, through the notice channels in `15`.

Built status: PLANNED. The audit paragraph of `15` is updated to point here.

OPEN: the default threshold values, which are settings and start from safe drafts in the spike. Retention of raw read records is in section 9.

## 6. Disaster recovery

Update 2026-11-13: the whole-company export in doc 44 section 4.z uses this encrypted bundle format for transfer to a new install, and the restore wizard below restores it.

**Status: DECIDED.** Disaster recovery uses an encrypted recovery bundle, scheduled PostgreSQL backups, and a restore wizard. The same restore wizard is used for server migration.

Design consequences:

- The encrypted recovery bundle is a single sealed file. It holds: the configuration references (bootstrap values only, `03`), the sealed key-encryption-key export (`33` Q1 recommends a passphrase-sealed export for migration), the audit checkpoint head, the list of backup sets with checksums, the module list with versions and signatures, and the owner recovery material (section 7). The bundle is sealed with a key derived from a passphrase plus a hardware key or a sealed copy (section 7 quorum).
- PostgreSQL backups are scheduled: base backups plus WAL archive for point-in-time recovery, and a nightly `pg_dump -Fc` as a logical copy (`33`). The schedule, retention and offsite target are settings (`33` list).
- Restore drills run on a schedule (`backup.drill_interval_days`, `33`). A drill restores into a sandbox (section 2) and runs the checks in Kodo's self-test set.
- The restore wizard runs the same steps for disaster restore and for migration to a new host: verify bundle, restore keys, restore database to a point in time, restore Soul folders and Vault, rebuild indexes, verify audit chain from the last anchored checkpoint, start in read-only mode, then the owner confirms and writes are enabled. Every step is audited with the restore id.
- Migration uses the same wizard. The source host is set read-only, a final bundle is taken, and the target restores from it. The source is then retired or kept as a cold copy, by the owner's choice.
- The wizard supports a restore to a point before a known compromise, with the unrecoverable range recorded (`33` Q3, point-in-time recovery).

OPEN: KEK custody on Windows and the new-hardware path (`33` Conflict 1, DECISIONS-LOG follow-up). The bundle format is decided; the hardware-binding detail depends on that spike.

Cross-link: `33` (backup, DR and export; this doc decides the bundle and the wizard, `33` keeps the component table and the settings).

## 7. Owner recovery

**Status: DECIDED.** Owner recovery uses a recovery bundle, a backup-owner quorum, hardware keys, and delayed alerting for recovery.

Design consequences:

- The owner recovery bundle is the bundle in section 6, plus a recovery credential set. It is stored offline by the owner.
- Backup owners: the owner names a set of backup owners (n) and a threshold (k). Each backup owner holds a share and a hardware key. The shipped default is a threshold the owner sets during setup, with the wizard refusing k = 1 when n > 1 (a floor).
- Hardware keys are required for every quorum member. They are FIDO2 security keys, used with user verification. A passkey on a phone is not a quorum key.
- Starting recovery: a recovery is initiated by a quorum member. The initiation is audited and sends an alert to every existing owner device and every backup owner.
- Delayed alerting: recovery completes only after a time lock. During the lock, any existing owner device can cancel the recovery with biometric verification (section 8). The lock length is a setting with a floor (the shipped default is the longer, more protective option). Alerts repeat at intervals during the lock.
- Completion needs k shares or hardware key assertions, the time lock elapsed, and the owner's biometric check on the new device. Then the new owner device is enrolled, the old keys are revoked, and the event is audited with a signed proof.
- A recovery that is not cancelled is recorded in the audit chain before it completes, so the chain shows the recovery.

Built status: PLANNED.

OPEN: the default k and n values and the default time lock. These are settings; the shipped defaults are proposed in the spike. The owner has not chosen numbers. Needs counsel for the recovery of a company account in a multi-human company.

## 8. Biometrics and multi-factor

**Status: DECIDED.** Full multi-factor sign-in uses biometrics: 3D face with LiDAR, multi-lens or zoom face capture with liveness, voice, and fingerprint, alongside hardware keys. Biometric templates stay on the device and never reach the server. Biometric verification is required for sign-in, recovery and high-risk actions.

Design consequences:

- Templates are enrolled on the device, in the device's secure hardware, and never leave it. The server stores no template, no template hash and no face image.
- The device produces a signed assertion: device key, the factor used (face with depth, face with multi-lens or zoom, voice, fingerprint, hardware key), the liveness result, and the time. The server verifies the signature against the enrolled device key (`32` section 5a) and the freshness of the challenge.
- Liveness is provided by the device or the platform API. Where a platform offers no liveness, the factor is not used for high-risk actions. Face with depth from LiDAR is the strongest factor; multi-lens or zoom is accepted only with a liveness check the platform reports as passed.
- Passkeys (WebAuthn) remain the transport for sign-in (`32` section 4). A passkey assertion with user verification plus the device biometric assertion together form the sign-in. Passkeys stay owner-held and never enter the Passport (`14`).
- Required for: sign-in on a new device, every recovery step (section 7), every high-risk action (role change, grant widening, export of all data, retention or legal-hold change, module permission approval, containment undo, erasure of another person's data, and any Tier 2 approval in `15`).
- Android uses the platform biometric prompt per approval (`29`). Windows uses the device face or fingerprint through the platform. Each platform provider sits behind an interface (`03`).
- Biometric data is never in logs, the audit chain or backups.
- A device without a required factor cannot perform a high-risk action. It can still read.

Built status: PLANNED. Spike per platform (LiDAR face on the owner's device, multi-lens fallback, liveness reporting, fingerprint).

OPEN: the platform list for LiDAR face beyond the owner's devices and the exact liveness reporting per platform. These are device-capability questions for the spike, not policy choices. The biometric matching thresholds stay with the platform.

## 9. Retention

**Status: DECIDED.** Retention periods are set by the owner per data category. Deletion is soft first. Legal hold blocks removal. Crypto-erase destroys per-subject keys and produces a signed proof of erasure.

Design consequences:

- Retention is a per-category table: category, period, soft-delete window, hold behaviour, and erasure method. The categories are the same as the data inventory in `32` section 9. Periods are settings. The GC log is never purged (`01`); its personal fields are removed only by crypto-erase.
- Soft delete: a delete marks a row with a tombstone and hides it from normal views. Restore is possible within the soft-delete window. Soft delete is recorded in the audit chain.
- Legal hold: a hold names a scope (a person, a GC, a company, a category) and a reason. While a hold is active, purge and crypto-erase in its scope are blocked. An erasure request inside a hold is queued, not refused, and the subject is told the hold status where the law requires it (needs counsel). Holds are visible to the owner, audited, and need the owner's biometric check to create or release.
- Crypto-erase: destroy the per-subject key (`04`, `15` C01) and append a tombstone. The GC structure, timestamps and authorship stay (C01). The erasure proof is a signed document with: subject reference (a salted hash, not the name), the key ids destroyed, the scopes covered, the backup expiry date after which the data is unrecoverable from backups, the time and the signing key. The proof is stored in the audit chain.
- Backups keep old keys until the backup expires. The backup retention is the erasure window (`33`, C01). The proof states the window date.
- Raw audit read records (section 5) have their own retention period, a setting. Their personal fields are crypto-erasable like any other.
- Retention changes are high-risk actions (section 8).

OPEN: erasure inside Vault history and git history (`33` Q6, C38). The owner has not decided this, and the options are encrypted personal notes or history rewrite. Needs counsel. The shipped default for Vault pages is the most protective option (no personal data in a Vault page without per-subject encryption), still to be designed in `18`.

OPEN: whether key destruction counts as erasure under each jurisdiction (C01 (c)). Needs counsel.

## 10. Secrets

**Status: DECIDED.** Secrets live in a server vault. Agents get scoped, short-lived tokens. Each agent has its own identity. Secrets are rotated, and any token can be revoked at once. The server injects secrets. A Passport may be mounted per agent, scoped to that agent only, as the owner chose.

Design consequences:

- The vault is `ISecretStore` (`14`, C05). Only bootstrap values (the key-encryption-key reference and the database credential, `03`) live outside it.
- Agents never see provider keys or App keys. They get leases from `ICredentialBroker` (`15`): a GitHub App token narrowed to one repository, or an injected cookie at the proxy. Lease lifetime is short (minutes, a setting with a floor on the upper bound).
- Per-agent identities: each agent has its own identity record, its own GitHub App installation scope where the platform allows, its own mount and OS account (`15`), and its own leases. No agent shares a credential with another agent.
- Server injection: the server writes a secret into the destination (proxy, broker call, a single browser session, or a host-side git push, `14` C05). Secrets never enter a VM environment variable or a VM disk.
- Rotation: scheduled rotation for each secret class (a setting per class), and on demand. Rotation of a key re-wraps DEKs and does not re-encrypt rows (`14`). Rotation of a provider or App key is audited and checked by a test call before the old key is retired.
- Instant revoke: revoking a lease, an agent identity, a secret or a device takes effect at the next broker call. The kill switch (`15`) revokes all of an agent's leases in one step. The revocation is audited.
- Mounted Passport, per agent: the owner chose to allow a Passport mount scoped to one agent. The security note advised against it (`14`). The mount holds metadata and a session-cookie profile only (`14` C02), is read-only, is on its own share and OS account, carries canary credentials, and detaches on hard taint (`15`). The shipped default for new installs is off, and the operator enables it per agent. This is a shipped default, not a loosening of the owner's decision.
- The owner-class credentials (owner merge credential, `14`) are never leased to an agent.

OPEN: the default lease lifetime and the rotation schedule per class. These are settings. Token lifetime for GitHub App tokens is still unverified (`21` test T4).

## 11. AI agent safety

**Status: DECIDED.** Agents operate under permissions, token budgets, a kill switch, approval gates, dry runs in a sandbox copy, and replayable runs. Untrusted input taints a session, and high-impact actions then need owner approval (`15`).

Design consequences:

- Permissions: each agent has a permission set (capability classes, scopes and models, `15` and `14`), enforced by the `ToolDispatcher`. The prompt never carries authority.
- Token budgets are quotas. Each agent has a token quota per window (hour, day, week), set in the settings registry. Breach denies the call and is shown on the agent's chip. Quotas are not money budgets (K5).
- Cost is shown in the observatory and the usage views (`23`). Cost never stops work by itself. A spend ceiling that halts work is REJECTED under the Resource Economy decision. See OPEN 11.2.
- Kill switch: per agent and global (`15`). A global kill freezes egress and write tools; the status page stays up. Triggers are the host CLI, the automatic breach rules (section 5.6), and the owner's devices. The kill switch is tested in a weekly drill.
- Approval gates: the tiers in `15`. The owner's approval uses the signed approval flow with the biometric check for high-impact actions (section 8).
- Taint: external content taints hard, agent text softly (`15`). Once a session is hard-tainted, high-impact actions need owner approval per action, and the session is not trusted again. Tainted sessions are replaced by a fresh session seeded with an approved summary (`15`).
- Dry run in a sandbox copy: any change to a Vault page set, a Kanban plan or a code branch can be dry-run in a sandbox copy of the company (section 2). The dry run has blocked egress, fake credentials and an audit stream. The dry-run result is shown to the owner before the real run.
- Replayable runs: each turn stores its inputs, tool calls, tool results and model responses, with secret values replaced by references. A run can be replayed against recorded tool results with no external effect, to debug or to show the owner what happened. Replay is audited.
- Loop-guard trips go to the Psych Ward (`26`, C34).

OPEN 11.1: the default token quota values per agent class. These are settings with drafts in `09`/`05`.

DECIDED 11.2 (owner, 2026-11-13, card answer): token and quota budgets only. No money budgets and no money cap that stops work. Cost is shown as display and analysis only, and never gates an agent.

## 12. Privacy law

**Status: DECIDED.** The platform ships data-subject tools for export, deletion and access. It generates compliance reports in GDPR and SOC 2 style. Instances are private by default. Compliance settings are global when several human members are present.

Design consequences:

- Data-subject tools: an export job per person (JSON plus attachments, `33`), a deletion request that runs crypto-erase where the person's personal fields are concerned (section 9), and an access report that lists the categories held about the person, the purposes, the recipients (model providers and other processors as listed in `05`/`32`), and the retention. Each request is audited and has a due date.
- Compliance reports are generated from the data inventory (`32` section 9), the audit chain (section 5), the retention table (section 9), the access reviews, the incident log and the erasure proofs. The reports are generated, not certified. Each report states its scope and date range.
- The report templates cover GDPR records of processing and data-subject handling, and SOC 2 style control evidence (access, change, availability, confidentiality). Templates are data, so an operator can add a jurisdiction overlay (`32` profiles).
- Private by default: no data leaves the instance unless the operator turns on a route. Outbound telemetry (crash reports, `37` and the telemetry design) defaults to ask. Remote access is off until configured (`32`).
- Privacy profiles: `privacy.profile` (`personal`, `company-global`) and overlays (`32` section 9). When more than one human member is present, the company-global profile applies. It is global, meaning no individual member can override a compliance setting that the owner set for the company.
- Consent and notice for each new member are recorded in the audit chain (`32` section 9).
- Legal review: every template, default and overlay needs counsel before public release. This is not legal advice.

OPEN: the list of jurisdictions for the first overlays and the wording of the generated reports. Needs counsel. The owner has not decided the list.

## Work packages (PLANNED)

These are the order in which the decisions are built, not a schedule.

1. Audit chain, checkpoints, verifier and sinks (sections 5.2 to 5.4). Needed by everything that records.
2. Field encryption and per-subject keys, crypto-erase and signed proofs (sections 5.5 and 9).
3. Secrets and leases with instant revoke, per-agent identities (section 10).
4. Roles with floors (section 4).
5. Module manifest, signature check and permission approval (section 1).
6. Biometric assertion and device enrolment spikes (section 8), then sign-in and recovery use.
7. Recovery bundle, restore wizard and drills (section 6).
8. Owner recovery quorum and time lock (section 7).
9. Developer sandbox runner, then verified-tag pipeline (sections 2 and 3).
10. Anomaly rules and auto-containment with undo (section 5.6).
11. Data-subject tools and compliance reports (section 12).

## Testing (PLANNED)

- Chain: a tamper test changes one row and the verifier reports the first mismatch. A truncation test removes the last rows and the anchor catches it.
- Erasure: after crypto-erase, the subject's encrypted fields are unreadable and the chain still verifies.
- Permissions: a module that calls outside its approved set is refused and the attempt is audited.
- Roles: an agent principal cannot be granted a human approval capability. The last owner cannot be removed.
- Recovery: the time lock holds, a cancel by an owner device stops recovery, and k-1 shares fail.
- Biometric: the server rejects an assertion with a stale challenge, a bad signature, or a missing liveness flag.
- Restore: a drill restores a fake-data company from a bundle and verifies the chain from the last checkpoint.
- Sandbox: the fake-data company has no path to real endpoints; a canary token used in the sandbox is detected.

## Questions for the owner

Only the details the owner has not decided:

1. Confirm K5: no cost ceiling that stops work (token quotas only), or name the cost limit you want.
2. Name the external anchoring targets you want by default, or accept the shipped default in section 5.3 (counsel needed for any target outside your control).
3. Give the backup-owner quorum numbers (n and k) and the recovery time lock, or accept the shipped defaults.
4. Confirm that the shipped default for a new install's Passport mount is off, with the operator enabling it per agent.
5. Name the jurisdictions for the first privacy overlays (counsel needed).
6. Decide the Vault and git history erasure mechanism (`33` Q6, C38), or keep it open until counsel responds.
