# Onboarding: Server Setup and Client First Run

Status: DRAFT for owner review (2026-11-13; section 1.16 graphical installer added 2026-11-22, PROPOSED). The direction is DECIDED by the owner (brief of 2026-11-13). Every mechanism below is PLANNED (designed, not built) unless a line says otherwise; built so far: the apply engine, the `install.settings`, `company.profile`, `owner.create`, `constitution.seed` and `firstrun.seed` steps, the provisioning API (sections 1.3, 1.9, 1.15) and the config-path start option (section 1.1). Owner questions: 6
Depends on: 02-wire-protocol-and-sync.md (HTTP surface, status), 03-server-host-and-providers.md (config layers, settings registry, floors), 04-postgres-schema-and-migrations.md (`migrate`, partitions), 05-model-gateway.md (sources, routing), 14-identity-service-and-passport.md (`ISecretStore`, KEK, merge credential), 15-security-taint-and-approvals.md (`audit_log`, scam-mail setting), 22-email-subsystem.md (mail back ends, bait-protocol wizard), 24-company-systems-core.md (Constitution), 27-client-architecture.md (client shell, prefs, keyboard model), 29-android-client.md (radial menu), 32-remote-access-auth-and-privacy.md (recovery, privacy profile; no pairing), 33-backup-dr-and-export.md (backup targets, restore), 37-distribution-matrix-and-packaging.md (per-company packages). Named in the brief but not yet in `docs/design/`: 41 (client mnemonic navigation), 44 (data portability and importers), 46 (Kodo health API), 48 (audit view).
Used by: 30-linux-client-and-packaging.md, 34-testing-strategy.md

## Purpose

A new Bazaar install must reach a correct, safe, running company without a manual checklist, and the owner must be able to do it from a terminal with no client at all. This doc covers two halves that are designed together but built separately:

1. **Server setup** (Part 1): how a Bazaar.Server install is created. It is headless first. Every route ends in the same result and the same audit trail. A server has no tour.
2. **Client first run** (Part 2): what a person sees the first time a Bazaar client opens against a company. It connects to its company server automatically (each client is built already bound to the server that built it, so there is no pairing), shows a short intro, and offers a keyboard-first tour that can be skipped and re-run.

## Decided inputs

| Input | Source |
|---|---|
| Server setup is headless-first: interactive wizard, declarative headless config file, CLI flags and environment variables, provisioning API, reusable company templates, config validation with dry-run, and Kodo verifies a finished setup. Every path yields the same result and the same audit trail. | Owner brief, 2026-11-13 |
| Server setup must be fully headless. Tours are for client apps only. | Owner brief, 2026-11-13 |
| Client first run: automatic connection to the server that built the client (no pairing), an intro, and an interactive keyboard-first tour (mnemonics, panes, layouts). Skippable and re-runnable. | Owner brief, 2026-11-13; owner, 2026-11-16 (no device pairing) |
| No sample-data sandbox company. | Owner brief, 2026-11-13 |
| The import wizard still exists, through data portability. | Owner brief, 2026-11-13; doc 44 |
| One company per install. Per-company client packages; signing keys stay on the server. | Owner brief; doc 37 |
| Install is a Bazaar-only action: no external installs for users. PostgreSQL and pgvector are bundled and run as a hidden supervised child process. | Owner brief, 2026-11-13 |
| Update policy default: automatic for security, manual for the rest. | Owner gap decision, 2026-11-13; doc 30 |
| Keyboard-first: nothing focused by default, leader `Space`, mnemonics, no Ctrl chords. | Doc 27; owner brief |
| Instances are private by default. Compliance settings apply when there are several human members. | Owner brief; doc 32 |
| Secrets are injected by the server. Config holds references only. | Doc 14; doc 33 |
| Configurability rule: open values are operator settings with safe defaults, changes audited. | DECISIONS-LOG round 13; doc 03 |
| Beta 0.x versioning; the release channel is chosen by the operator. | CLAUDE.md; docs/VERSIONING.md |

## Principles

- **One plan, many doors.** The wizard, the config file, flags, environment variables, the API and templates all produce the same setup plan (section 1.2). The plan is the only input that changes the install.
- **Headless first.** Anything the wizard can do, a file, a flag and the API can do too. The wizard is a front end, not the source of truth.
- **Validate before change.** Every path can dry-run. Nothing is written until the exact plan hash has been checked.
- **Floors are not settings.** Setup may tighten a floor, never loosen it (doc 03 floors, doc 14 custody, doc 15 taint rules).
- **Secrets never sit in files, arguments or logs.** References only.
- **Protective defaults.** Where a legal or privacy choice is open, the shipped default is the most protective documented option, with a "needs counsel" note.
- **Accessible from day one.** Every step works by keyboard alone, with screen-reader labels and translated strings.

---

# Part 1: Server setup

## 1.1 Entry points [PLANNED; the config-path start option is BUILT]

| Entry point | Who uses it | Notes |
|---|---|---|
| Interactive wizard, `bazaar-admin setup` | Owner at the server console, or over SSH | Plain text prompts, one question at a time, keyboard only, readable by a screen reader. The plain path stays as the accessible one; the TUI (doc 58) and graphical installer (section 1.15) are opt-in beside it (doc 03 Q2 default is a small non-TUI `bazaar-admin`). The questions, validation, plan-hash confirmation and apply live in a UI-free `SetupWizardEngine` behind the `ISetupPrompter` port, so a second front end such as the planned server TUI (`Bazaar.Client.Tui`, Terminal.Gui) drives the same steps by implementing that port; `bazaar-admin` supplies only the console prompter. |
| Installer TUI and graphical installer | Owner at the box (TUI, doc 58) or at a desktop (GUI) | Two more front ends to the same wizard engine and `ISetupPrompter`; the GUI is designed in section 1.16. The fastest install stays the config file. |
| Wizard in a connected client | Owner, signed in to the client | Same steps, shown in the client's admin panel (doc 27 settings surface). Used for later changes more than first install. |
| Declarative config file | Operators, scripted installs, reproducible rebuilds | `bazaar-setup.json`, applied with `bazaar-admin setup --config <path>` (section 1.5). |
| Config-path start option, `Bazaar.Server.Host --setup-plan <file>` or `BAZAAR_SETUP_PLAN` | Container images, service managers, clean-install scripts | **BUILT** (`SetupPlanOnStartService` in `Bazaar.Server.Setup.Host`). The host loads the file with `SetupPlanLoader`, computes the plan hash and runs the same apply pipeline in-process before it serves: the same validation, floors, holds, instance lock and `setup.plan.applied` audit row, with entry path `file` and operator identity `host:setup-plan`. The operator started the process, so the file is local operator input and needs no bootstrap code; the single-use code and the HTTP routes are unchanged (a validate-then-apply over HTTP therefore still needs a code per admitted call, and this door is how a script does both in one start). It runs after the startup sequence (lock, schema gate) and before the bootstrap-code issuer. A plan hash already stored as applied (exit 0) is not applied again on a restart; a stored failure is retried; a held apply is not stored. With `--setup-only` (or `BAZAAR_SETUP_ONLY=1`) the process exits after the apply with the plan's exit code (0 applied, 1 invalid or unreadable file, 2 held, 3 failed and rolled back) and announces no bootstrap code. Without it a failed apply is logged and the server keeps serving, so the owner can finish through the provisioning API. Not yet: the `--set` and `BAZAAR__SETUP__` overlay is not merged at start (the CLI does that), and no dry-run flag exists here (use `bazaar-admin setup validate`). |
| CLI flags | Quick single changes, scripts | `bazaar-admin setup --set company.name=...`. Flags carry values and references only. |
| Environment variables | Containers, service managers | `BAZAAR__SETUP__...`, the same double-underscore convention as doc 03. Values and references only. |
| Provisioning API | Automation, and the clients | Authenticated HTTP, section 1.9. |
| Company templates | Owners who run several companies on the same kind of setup | Section 1.8. A template is an input to the plan, not a copy of a live company. |
| Import | Owners moving an existing company in | A dump is chosen as the company source. Importers are doc 44 modules. |

There is no separate server-side tour and no first-run screen in the server process. The server process has no console; the human surfaces are the setup prompts, the TUI client module (doc 58), the admin panel (doc 51) and the read-only status page (doc 02, doc 23).

## 1.2 The setup plan: one result, one trail [PLANNED]

Every path compiles its input into a **setup plan**: one normalized JSON document with every decision filled in, defaults included.

- **Shape:** `schemaVersion`, the company block, the module set, the provider choices, the settings values (registry keys from doc 03 and setup-only keys), the source (`blank`, `template:<id>@<semver>` or `import:<dump id>`) and the secret references. It contains no secret values.
- **Hash:** the plan hash is SHA-256 over canonical JSON (sorted keys, no insignificant whitespace). Apply takes a plan hash. If the state or the plan has changed since the dry run, the hash differs and apply refuses.
- **Apply:** runs under the instance lock (doc 03), in the step order of section 1.3. Each step is idempotent, so a crash mid-apply resumes from the last committed step. A failed step rolls back its own transaction and stops the apply with a clear report (exit code 3, section 1.7).
- **Audit:** an apply writes one `setup.plan.applied` row to `audit_log` (doc 15) with the plan hash, the entry path (`wizard`, `cli`, `file`, `api`), the operator identity, the template id and version when one was used, and the Kodo result. Each changed setting writes its own `setting.changed` row (doc 03). Secret references are logged by name and resolution status, never by value.
- **Equivalence test (section Testing):** the same plan entered through each of the four doors produces the same plan hash and the same audit rows, differing only in the entry-path field.

The audit view that shows these rows is doc 48. The hash chain, signed checkpoints and external anchoring of `audit_log` are doc 15's.

## 1.3 Order of setup steps [PLANNED]

| # | Step | What it does | Owning doc |
|---|---|---|---|
| 0 | Preflight | Checks OS and edition, CPU, memory, disk, free ports, time sync, the data root and its ACL, and the existence of a service account that is not Administrator. Reports Windows edition as an operator fact (doc 03 Conflict 1). | 03 |
| 1 | Install identity | Install id (generated), company name and slug, default locale, default timezone, release channel (LTSC, Stable, Beta, Alpha or Latest-Commit; Beta while the version is 0.x). | CLAUDE.md, VERSIONING |
| 2 | Update policy | Confirms the shipped default: automatic for security, manual for everything else. Per-module and per-channel policies are edited later. | Gap decisions; doc 30 |
| 3 | Key, encrypted volume and bundled PostgreSQL | [PROPOSED, doc 63 section 10] Chooses the key-encryption-key provider (moved forward from step 5), creates the encrypted data volume and mounts it as the data root; setup cannot finish without it. Then creates the data root contents, starts the bundled PostgreSQL 18 or 19 with pgvector as a hidden supervised child on loopback, and sets the data directory ACL. Choice of major version: section 1.4. | 03, 04, owner brief |
| 4 | Schema | Runs the explicit `migrate` command once on the empty database under the instance lock. Records fixed values (partition count) in `schema_setting`. | 04 (C44) |
| 5 | Custody | Sets the key-encryption-key provider, bootstraps the Identity Service, and sets the audit anchor target. Mounts stay disabled until the anchor is set. | 14, 15 |
| 6 | Owner | Creates the owner identity, enrols at least one passkey or hardware key, and creates the encrypted recovery bundle. There is no device pairing (section 2.1). | 32, 14 |
| 7 | Privacy and retention | Privacy profile, retention periods, legal-hold defaults, erasure window. Each option that touches law is marked "needs counsel". | 32, 15, 33 |
| 8 | Backups | Backup target (operator's choice), schedule, and restore-drill cadence. Finishing without a target needs a recorded acknowledgement. | 33 |
| 9 | Model sources | FreeLLMAPI guided setup (priority source), the owner's provider API key by reference, optional custom proxies and local runtimes, the default free-lane preset. Subscription logins are refused on every route. | 05, 05b, 14 |
| 10 | Git and GitHub | Merge credential (owner GitHub App or linked owner credential; no default), GitHub App installation for the company's organisation. | 14, 21 |
| 11 | Email (optional) | Off by default. If enabled: back end, operator domain, DNS checker (SPF, DKIM, DMARC; PTR when self-hosted). Domains are never hardcoded. Optional plan fields under `email` (PROPOSED, 2026-11-18): `domain`, `backendKind`, `dkimSelector`, `dmarcReportAddress`, `selfHosted`. The check is advice and never blocks the apply. | 22 |
| 12 | Scam-mail protocol (optional) | Shown only if step 11 is on. Off by default. Section 1.11. | 15, 22 |
| 13 | Company source | Blank company, a template at a version (section 1.8), or an import (doc 44). The Constitution is drafted here and must be accepted by the owner before any Soul activates. | 24, 44 |
| 13b | First-run seed [PROPOSED] | Creates the first group chat and the default agent, and records the owner's Constitution acceptance. Section 1.15. | 01, 06, 24 |
| 14 | Modules | The first module set (Bazaar.GC is always present; Constellation, Conduit, KODO, RyuseiFuda and others by choice). Each module's licence is shown and checked against the licence policy. | 37, brief |
| 15 | Status and network | Status listener bind (loopback by default; any other bind needs Tier 2 approval). No inbound internet connection by default (doc 03). | 03, 02 |
| 15b | Remote access [PROPOSED] | Optional, off by default. The operator picks how clients reach the server from outside, in doc 39's order: `cloudflared` with a registered domain first, then relay or overlay modules, then manual port-forward only if the operator asks. Plan fields under `network.remote` (PROPOSED): `module` (a Conduit module id) and its own settings. Opening a tunnel or a port is an owner-approved action (Tier 2), and a third-party TLS terminator shows the plaintext warning. After apply, the reachability check waits up to 90 s for a new tunnel (`TunnelReadiness`, spike C2) and then reports reachable or the last error; the result is advice and never blocks the apply. | 39, 32, 15 |
| 16 | Plan review and apply | Shows the change list and the plan hash. Apply runs under the lock. | 1.2 |
| 17 | Kodo verification | Read-only checks and a green, amber or red report with a 0 to 100 score. Section 1.12. | 46 (not in tree) |
| 18 | Client packages | Builds the per-company packages (.msix, .apk, .exe) on the server. Signing keys stay on the server. | 37 |

**Apply step status (2026-11-19).** The step table above is the order. Which steps `setup apply` and `POST /setup/plans/{hash}/apply` run today:

| Step | Status | Notes |
|---|---|---|
| 1 Install identity (release channel), 2 Update policy (security half), 8 Backups, 10 Git and GitHub (the merge credential choice), 11 Email, 12 Scam-mail protocol, 13 Company source, 15 Status and network (bind and port), plus the default routing preset of 9, plus the plan's `settings` map | **BUILT** as `install.settings` (`InstallSettingsStep`, order 1500, in `Bazaar.Server.Setup.Postgres`) | Writes the install-scope rows of the `setting` table: the `settings` map, `network.statusBind` and `network.statusPort` (`host.status.bind`, `host.status.port`), `install.releaseChannel` (`update.channel`, snake_case wire form), `install.updatePolicy.security` (`deps.update.autoSecurityPatches`, automatic is true), `scamMailProtocol.enabled` (`security.scam_mail.protocol_enabled`, written only when on, which is held for owner approval), the backups section (`backup.target`, empty when none; `backup.drill_interval_days`; `backup.no_target_acknowledged`, true only for a plan with no target and an acknowledgement; doc 33; keys contributed by `Bazaar.Server.BackupPolicy`) and the email section (`mail.enabled`; `mail.backend`, `none` while off; doc 22; keys contributed by `Bazaar.Server.MailPolicy`). Every value goes through the registry first. A backup target that is not `file:` is held for owner approval (data leaves the install). Email on needs one of the four back ends, and a back end while off is an error, in the validator and in the step. One transaction, same per-key locks as the settings store. A key already holding the planned value is left alone (idempotent). Rollback restores the previous value and writer, or removes the row, and leaves a value someone changed after the run. The applier writes the `setting.changed` rows. These keys are recorded choices: the backup scheduler and the mail runtime read them when they are built, so writing them starts no backup and opens no mailbox. Added 2026-11-20: `identity.merge_credential` (`owner_app` or `linked_user`; the plan's `owner_linked` is stored as `linked_user`) and `identity.merge_credential.app_id_ref` (a `secret://bazaar/` reference, written only for `owner_app`, never a credential), `gateway.default_preset` (a slug; shipped default `free-lane`) and `company.source` (`blank`, `template:<id>@<semver>` or `import:<id>`; a reference only, written when the plan states one). The merge credential keys are denied to templates on purpose. A later change of the merge credential has no registry approval rule (doc 14 asks for owner approval; the registry cannot see the stored value, so the merge flow's signed owner approval is the gate and the rule stays OPEN). |
| 13b First-run seed | **BUILT** as `constitution.seed` (`ConstitutionSeedStep`, order 134, `Bazaar.Server.Setup.Constitution` with `.Abstractions`) then `firstrun.seed` (`FirstRunSeedStep`, order 135, `Bazaar.Server.Setup.FirstRun`), with Postgres stores in `Bazaar.Server.Setup.FirstRun.Postgres`, registered by the setup component | Both cover `firstRun`. `constitution.seed` writes the default Constitution (Markdown with `type: constitution` front matter and the six sections of doc 24, no company name, domain or person) to `company/Constitution.md` in the Vault git repository, commits it, and indexes it so `vault_docs` holds the `constitution` row that acceptance reads. It runs when the plan seeds the default agent or accepts the Constitution. An existing file is never overwritten (the owner may have edited it), a Vault that is not yet a repository is initialised, and a second apply makes no commit. Rollback leaves the Vault alone (history is append-only). The Vault repository, vault id and writer identity come from the host settings `Memory:Vault:RepoPath`, `VaultId`, `WriterName` and `WriterEmail` (the memory module's keys, so one install configures one Vault; environment form `Memory__Vault__RepoPath` for the CLI). With any of them unset the step fails with a message naming them, so nothing is written somewhere unplanned. The step drafts only: acceptance stays the owner's act (`acceptConstitution`). `firstrun.seed` needs the owner row (`human`, role `owner`), so it fails with a plain message when none exists. Makes the `general` GC (owner as member) and the default agent (`agent` state `created`, then `active` only after the owner's acceptance), both found again by marker rows in `firstrun_seed` (idempotent, under an advisory lock). The agent's model policy holds exactly the whitelist: the route `claude-first-agent` plus every available Claude model of an enabled gateway provider, narrowed by `defaultAgentSources`; no Claude source means no policy row (deny by default). Acceptance records the head commit of the newest `constitution` Vault document in `constitution_acceptance`; with no such document nothing is accepted and the agent waits. Audit row `setup.firstrun.seeded` (ids and a flag). Not yet: the selection profile of doc 92 is validated but not stored (doc 92 is not in the tree), the purpose is stored in `firstrun_seed.detail` with no profile view yet, and the end-to-end run over a real Vault index (pgvector) is not yet tested, only the git and step halves. The Admin CLI registers both steps, so a CLI apply matches the host (the Admin gate also covers `firstRun`). |
| 9 Model sources | **BUILT** as `models.sources` (`GatewaySourcesStep`, order 900, in `Bazaar.Server.Setup.Postgres`) | Writes one `gw_provider` row per plan source. The plan carries only `{id, keyRef}`, so kind, adapter, base URL and credential kind come from the source catalogue (`ModelSourceCatalogue.Default`, in `Bazaar.Server.Setup.Abstractions`, doc 05 "Default catalogue"; ids `anthropic-direct` (aliases `anthropic`, `anthropic-owner`), `gemini`, `openai`, `openrouter`, `huggingface`, `freellmapi` (base `http://127.0.0.1:3001/v1`, doc 05b), `ollama`, `lm-studio`). A source doc 05 leaves "to confirm", "operator-set" or "account-specific" is not in the catalogue, so a plan naming it is invalid (validation error `models.sources[n].id`) and the step refuses it: setup never invents a base URL, and the operator adds such a source in the admin panel. A host may pass its own `IModelSourceCatalogue` to the step. A source that takes a key must carry a `secret://bazaar/providers/` reference; a local runtime takes none. The row keeps `terms_ack_at`, headers, proxy and `enabled` as the operator left them. One transaction under one advisory lock, idempotent; rollback removes a row this run created (unless a model row now references it) and restores the five columns it changed, only while the row is still as this run wrote it. The default preset is a registry setting written by `install.settings`; a host registers both steps, and `models` is applied only with both. No `gw_route` is created: preset members need models the gateway has not listed yet. |
| 1 Install identity (company name, slug, locale, time zone), 6 Owner (display name, second-factor requirement, recovery bundle reference, printed-codes flag) | **BUILT (2026-11-22)** as `company.profile` (`CompanyProfileStep`, order 40) and `owner.create` (`OwnerStep`, order 60), in `Bazaar.Server.Company` with `Bazaar.Server.Company.Abstractions`; registered by the setup component and by the Admin CLI apply gate through `AddCompanySetup` | `company.profile` covers `install.identity` and writes the install's one `company_profile` row (company migration 0001, `id = 1`). `owner.create` covers `owner` and `owner.recovery`: it creates the owner `human` row (role `owner`, status `active`, the row `firstrun.seed` and the setup gate look for) and one `owner_profile` row (second-factor requirement, recovery bundle reference, printed-codes flag; Kodo's `owner.secondFactor` check reads the plan's requirement). Both run before `constitution.seed` (134) and `firstrun.seed` (135), so a fresh plan finds an owner. Idempotent (a second run finds the rows and writes no audit row); a company with other values, an owner with another display name or different recorded choices is refused (exit 3 with the apply rolled back), never overwritten. Rollback removes only rows this run created, and leaves an owner a device or credential row points at. Audit rows `setup.company.profile_created` (plan hash, entry path, slug) and `setup.owner.created` (plan hash, entry path, owner id and flags), and the matching `*_rolled_back` rows; neither holds the company name, the owner's name or the bundle reference. A failed audit write removes the step's own rows before the failure goes on. **No secret is created or stored:** `owner.recovery.bundleRef` is a `secret://bazaar/` reference kept as given (the table refuses anything else); the bundle, codes and keys belong to the Identity Service, and the passkey is enrolled through the sign-in page. Ports: `ICompanyProfileStore`, `IOwnerStore`, read-only `ICompanyProfileReader` (all in `Bazaar.Server.Company.Abstractions`); Postgres stores in `Bazaar.Server.Company`. Agents are not seeded here: the first agent is `firstrun.seed`'s. |
| 0 Preflight, 3 Data root and PostgreSQL, 4 Schema, 5 Custody, 6 Owner (backup owners and the passkey ceremony), 7 Privacy and retention, 14 Modules, 16 to 18 | PLANNED, no step registered | Each needs a store or service that is not in the tree yet. The install id has a table and a provider (`IInstallIdProvider`, `PostgresInstallIdProvider`, setup migration 0003 `setup_install_id`, one row, idempotent; **BUILT 2026-11-22**, not yet shown in the plan or written by a step; the company name, slug, locale and time zone are stored by `company.profile`); the "other" half of the update policy has no setting key, so `install.updatePolicy.other` must be `manual` (validation refuses `automatic`; the step refuses it too); the privacy profile only accepts `private`, so it has nothing to write; the audit anchor target is not `security.audit.export_target` (an enum); the owner's passkey ceremony and the backup owners (`owner.recovery.backupOwners`, section `owner.backupOwners`) have no store, and the recovery bundle itself is made by the Identity Service, not by setup; the module set has no enabled-modules store. `migrate` stays its own explicit command (doc 04), and an apply needs the migrated `audit_log`. Until they land, an apply refuses a plan that asks for those parts (see "Unapplied sections" below). |

**Unapplied sections (DECIDED 2026-11-19, rationale: a setup that cannot apply what the operator asked for must say so before it changes anything).** `SetupPlanSections` (in `Bazaar.Server.Setup.Abstractions`) is the one registry of plan sections: `install.identity` (step 1), `install.releaseChannel` (1), `install.updatePolicy` (2), `database` (3, 4), `custody` (5), `custody.identityKek` (5), `owner` (6, display name and second-factor requirement), `owner.recovery` (6, bundle reference and printed codes), `owner.backupOwners` (6, the backup-owner list), `privacy` (7), `backups` (8), `models` (9), `git` (10, the merge credential), `email` (11), `scamMailProtocol` (12), `company` (13), `modules` (14), `network` (15) and `settings` (the registry values map). Every plan leaf belongs to exactly one section (a test enforces it). Each `ISetupStep` declares the sections it applies in `Covers` (default none), so registering a step shrinks the list with no other edit.
- A section is *requested* when any of its leaves differs from the shipped default plan (and, once a state reader exists, from the current state). A section left at its default or empty is not requested and needs no step. Required fields (company name and slug, KEK, owner name, company source) have no default, so they always request their sections.
- Before the lock or any step, the applier compares the requested sections with the registered steps' `Covers`. Any requested section nobody covers holds the apply: exit 2, issue path `unapplied_sections`, message naming the section ids and doc step numbers (never values), `appliedSteps` empty, nothing changed, so a partial apply cannot happen. Exit 0 therefore means every requested section was applied. Exit codes keep their meanings.
- The list appears in the apply result (`UnappliedSections`), the CLI text and `--json` (`unappliedSections`, outcome `unapplied_sections`), the HTTP bodies of `POST /setup/plans` and `.../apply` (`unappliedSections`), the HTTP call audit row (outcome `held:unapplied_sections`) and a `setup.plan.applied` row with outcome `held_unapplied_sections` (section ids only; this is the one held apply that is recorded, so the refusal is on the record). A dry run (`setup --dry-run`, `POST /setup/plans`) lists the same sections, marked as what an apply would be held for.
- Today `company.profile` (covering `install.identity`), `owner.create` (covering `owner` and `owner.recovery`), `install.settings` (covering `install.releaseChannel`, `install.updatePolicy`, `backups`, `email`, `git`, `scamMailProtocol`, `company`, `network` and `settings`), `constitution.seed` and `firstrun.seed` (covering `firstRun`) and `models.sources` (covering `models`) are registered. Every valid plan also requests `custody`, `database`, `privacy`, `modules` and more (and `owner.backupOwners` when it lists backup owners), so a real apply is held until those steps land. That is the honest state.

## 1.3a Company profile and owner steps [BUILT; added 2026-11-22]

Steps 1 (install identity) and 6 (owner) are applied by two small steps in `Bazaar.Server.Company`, so the first-run seed (section 1.15) finds an owner row. The `firstRun` plan section and the default agent stay exactly as section 1.15 has them; this section adds nothing to them.

- **`company.profile`** (order 40) covers `install.identity`: name, slug, locale and time zone. One company per install: the table holds one row, and a different company is refused, never edited. The release channel and update policy are other sections (`install.settings`). The registry section named `company` is the company *source* (step 13) and is unchanged.
- **`owner.create`** (order 60) covers `owner` (`owner.displayName`, `owner.requireSecondFactor`) and `owner.recovery` (`owner.recovery.bundleRef`, `owner.recovery.printedCodes`). The backup owners are their own section, `owner.backupOwners`, which no step covers yet, so a plan that lists them is held rather than silently dropped.
- **`owner.recovery.bundleRef`** (new, optional, left out of the canonical form when absent, so existing plan hashes do not change) is a `secret://bazaar/` reference to the encrypted recovery bundle, validated by shape only (nothing is resolved). Setup records the reference and never creates, reads or stores the bundle, a code or a key (step 6 of the table above; doc 14 owns the bundle). A literal value, an `env:` or `file:` reference is refused by the validator without echoing it.
- **Configurable, not decided.** The second-factor requirement stays the plan's (default true; finishing without a second factor is allowed with a warning, Open question 4). Whether setup should also create the bundle through the Identity Service once that exists is OPEN; until then the operator supplies the reference.

## 1.4 What the wizard asks, and what it defaults to [PLANNED]

"Asks" means the wizard shows the question. Every other path takes the same key in the plan. "Required" means no default exists, so the plan is invalid until a value is given.

| Decision | Asks | Shipped default | Notes |
|---|---|---|---|
| Company name and slug | Yes | Required name; slug derived from the name, editable | Slug is an identifier and cannot change later. |
| Default locale and timezone | Yes | Locale `en`; timezone from the host | i18n is on from day one. Each agent keeps its own timezone (doc 09). |
| Release channel | Yes | Beta while 0.x | Channels per `docs/VERSIONING.md`. |
| Update policy | Confirm | Automatic for security; manual for the rest | Owner gap decision, 2026-11-13. |
| PostgreSQL major version | Yes | The newest bundled version the release marks as default; PG 18 until PG 19 is marked default | PG 19 GA is expected around 2026-11-19. Both versions stay supported (dual-version rule). |
| GC partition count | Yes | 16 | Fixed after the first GC data (doc 04). |
| Key-encryption-key provider | Yes | `dpapi` on Windows; `file` (a protected key file) on Linux and other hosts (doc 14, DECIDED) | `tpm` (Linux opt-in, sealed through `systemd-creds` where systemd is present) and `openbao` optional (doc 14). |
| Audit anchor target | Yes | Required, no default | Mounts stay disabled until set (doc 14). |
| Owner passkey or hardware key | Yes | Required to finish [PROPOSED] | A second factor is needed for recovery and high-risk actions (gap decision: biometrics and MFA). |
| Recovery bundle and printed codes | Yes | Bundle created; printed codes per `recovery.printed_codes_enabled` (doc 32) | Backup-owner quorum is an optional list (doc 32). |
| Privacy profile | Yes | Private instance | Compliance settings apply automatically when a second human member exists. Needs counsel for the wording of the options. |
| Retention and erasure window | Yes | The most protective documented option | Doc 04 lists 35 days as a placeholder. Needs counsel. |
| Backup target | Yes | Required; finishing without one needs a recorded acknowledgement (DECIDED, owner 2026-11-16: Open questions item 5) | Operator's choice, documented in doc 33. |
| Model sources | Yes | FreeLLMAPI guided setup; default free-lane preset | Owner's API key optional. Routing defaults are doc 05's. |
| Merge credential | Yes | None; must be chosen (doc 14, round 12) | Owner GitHub App or linked owner credential. |
| Status listener bind | Yes | Loopback | Non-loopback needs Tier 2 approval (doc 03). |
| Email | Yes | Off | If on, a back end is required (`email.backend`: `bazaar_client`, `imap_smtp`, `cloudflare` or `builtin_server`; empty while off) and an operator domain is set in the mail panel afterwards, because the domain is a `mail_domain` row, not a plan field (doc 22). |
| Scam-mail protocol | Yes, if email is on | Off | Section 1.11. |
| Company source | Yes | Blank company | Template or import optional. |
| Module set | Yes | Bazaar.GC plus the core modules | Each module's licence is shown before install. |

## 1.5 Declarative config file [PLANNED; sketch only]
> WORKING NAME, pending owner naming decision. The key names and file shape in this sketch are proposals.


The file is one JSON document named `bazaar-setup.json`. The answer to the format question is JSON, through `System.Text.Json`. It matches the wire format (CLAUDE.md), it needs no second parser, and it lets the server publish one JSON Schema per `schemaVersion`. YAML and TOML are not supported. JSON has no comments, so the schema carries the descriptions.

Rules: unknown keys are rejected (the file fails closed); every key is either a settings-registry key (doc 03) or a documented setup-only key; secret-typed keys accept only references (section 1.6).

```json
{
  "schemaVersion": 1,
  "install": {
    "companyName": "Example Studio",
    "companySlug": "example-studio",
    "locale": "en",
    "timezone": "Europe/London",
    "releaseChannel": "beta",
    "updatePolicy": { "security": "automatic", "other": "manual" }
  },
  "database": {
    "postgresMajor": 18,
    "dataRoot": "%ProgramData%\\Bazaar",
    "gcPartitionCount": 16
  },
  "custody": {
    "kekProvider": "dpapi",
    "kekRef": "dpapi:C:\\ProgramData\\Bazaar\\keys\\kek-dpapi.bin",
    "identityKek": "dpapi",
    "auditAnchorTarget": "https://anchor.example.invalid/bazaar-audit",
    "mergeCredential": { "kind": "owner_app", "appIdRef": "secret://bazaar/owner/app-id" }
  },
  "owner": {
    "displayName": "Owner",
    "requireSecondFactor": true,
    "recovery": { "printedCodes": false, "backupOwners": [] }
  },
  "privacy": { "profile": "private" },
  "backups": {
    "target": "file:/mnt/backup/bazaar",
    "drillIntervalDays": 30,
    "acknowledgeNoTarget": false
  },
  "models": {
    "sources": [
      { "id": "freellmapi", "keyRef": "secret://bazaar/providers/freellmapi/key" },
      { "id": "anthropic-owner", "keyRef": "secret://bazaar/providers/anthropic/key" }
    ],
    "defaultPreset": "free-lane"
  },
  "email": { "enabled": false, "backend": "" },
  "scamMailProtocol": { "enabled": false },
  "company": { "source": "template:starter@1.0.0" },
  "modules": ["Bazaar.GC", "Bazaar.Constellation", "Bazaar.KODO"],
  "network": { "statusBind": "loopback", "statusPort": 8443 },
  "settings": { "host.shutdown_timeout_seconds": 20 }
}
```

The file is a plan input. It is never executed and never contains a secret value. It may be committed to git (private repository) because it holds references only. Doc 33 backs it up as configuration.

**Custody fields (2026-11-20).** Two fields name a KEK, and they are different things:

| Plan field | Values | Required | What it is |
|---|---|---|---|
| `custody.kekProvider` | `env`, `file`, `dpapi`, `systemd-creds` | Yes | How the bootstrap secret named by `custody.kekRef` is held (unchanged). |
| `custody.identityKek` | `file`, `dpapi`, `openbao`, `tpm` | No; absent means the server default (doc 14: `dpapi` on Windows, `file` elsewhere) | The identity KEK provider that wraps the Identity Service data keys. Applied to the host configuration file `<dataRoot>/config/kek.json` as `Bazaar:Identity:KekProvider` (**PROPOSED**, coordinator decision 2026-11-20: the KEK opens before the database, so it is host configuration, with no database copy; the registry key `identity.kek_provider` is not used). **BUILT** in setup: plan model, validation, wizard question in the "Identity key custody" step. The apply step is `Bazaar.Server.Setup.IdentityKek` (PROPOSED). |

`custody.identityKek` is left out of the canonical JSON when absent, so the hash of a plan that does not set it does not change. `tpm` and `openbao` are valid plan values now even though their providers land separately. Verification of the chosen custody is the `identity.kekCustody` check (section 1.12).

## 1.6 Secrets handling [PLANNED]

- **No secret value in the config file, in flags, in environment variables, in templates or in the plan.** Any value under a secret-typed key must be a reference. A literal value fails validation, and the error names the key only, never the value.
- **Two reference families.** Bootstrap values (the KEK and the database credential) use `env:NAME`, `file:<path>`, `dpapi:<path>` or `systemd-creds:<name>`, as doc 03 defines. Every other secret uses `secret://bazaar/...`, resolved by `ISecretStore` (doc 14) under a purpose the policy allows (doc 05 maps provider keys to this form). This resolves the earlier mixed wording in doc 05b (section Conflicts).
- **Entering a secret.** The wizard prompts without echo. `bazaar-admin secret set <reference>` reads the value from a prompt or from standard input. It never accepts the value as an argument, so it does not reach shell history or the process list. The value goes straight into `ISecretStore`. **Built 2026-11-22:** `AddBazaarSetupPostgres` registers `ISetupSecretWriter` as `SecretStoreSetupSecretWriter` when an `ISecretStore` exists (references map to installation-scope names), else a fail-closed writer; the Admin CLI still uses its own placeholder until it composes the Host store.
- **Dry-run output** lists reference names and a status (resolves, missing, not allowed). It never prints a value. Reads needed for the probe are purpose-tagged and audited.
- **Logs and the plan hash** contain reference names only.
- **Backups** contain references, never the values; the values live in the Identity Service's encrypted store (doc 33).
- **Canary test:** a known fake secret placed in every input path must not appear in any output, log, plan, audit row or error (see Testing).

## 1.7 Validation and dry-run [PLANNED]

- `bazaar-admin setup validate --config <file>` checks the schema version, every type, every floor (doc 03: service account is not Administrator; status is loopback unless approved; PostgreSQL stays a readiness role), reference resolution, provider probes, the release channel's compatibility with the installed build, disk and memory minimums, and free ports.
- `bazaar-admin setup --dry-run` runs validation and then computes the change list against the current state. On an empty install every step is "create". It writes nothing and prints the plan hash.
- `bazaar-admin setup apply --plan-hash <hash>` applies only if the hash still matches. The wizard shows the same hash on its review screen and applies only that plan.
- **Exit codes:** 0 valid or applied; 1 invalid input; 2 blocked by a floor or by a change that needs approval (the change is held, not refused); 3 apply failed and rolled back.
- **Output:** plain text by default, written so a screen reader reads it in order. `--json` gives the same content for scripts. Error text names the key and the rule, never a value.

## 1.8 Company templates [BUILT: loader, resolver, folder source, git catalog; PLANNED: exporter and wizard picker]

**What a template is.** A versioned folder in a git repository (the operator's own, or one Bazaar ships), containing:

- `template.json`: `schemaVersion`, a semver `version`, required modules, settings presets from the registry (doc 03), role definitions including custom roles, GC types to register (doc 01), Kanban columns (Backlog, Ready, In progress, Review, Done, with automatic Blocked marking), calendar and quota presets (doc 24, doc 09), and the Soul presets to offer (doc 07 export format).
- Markdown seeds: a Constitution draft, role charters, and the Vault folder layout (doc 18). They become Vault files when applied.

**What a template never contains.** Secret values or references to real secrets; GC messages; Soul instances, memories or agent history; personal data; the owner's identity; provider or GitHub accounts. Soul presets are behavioural only (doc 07). A preset does not activate an agent: the Constitution must be accepted first (doc 24).

**Use.** The plan's `company.source` is `template:<id>@<semver>`. The template's version and hash are recorded in the `setup.plan.applied` audit row. Changing a template later does not change an existing company; a newer version is applied as a planned settings change (section 1.13).

**Making a template.** An owner exports the structure and settings of an existing company with doc 44's exporter. Data is excluded by default and needs an explicit opt-in per category.

**Import.** Choosing an import as the company source hands the dump to a doc 44 importer. Importers are modules and handle docx, md, txt, Milanote, Miro, Trello, Obsidian and similar sources.

One company per install. A template creates the install's one company; it does not create a second one.

**Build status (2026-11-19).**

| Part | Status | Notes |
|---|---|---|
| Template model, strict loader, hash, resolver and overlay | **BUILT** | `Bazaar.Server.Setup/Templates`. Lookups are exact on id and version. |
| Folder source | **BUILT** | `FileSystemTemplateSource`. Links and paths outside the folder are refused. |
| Git catalog and fetch | **BUILT** | `GitTemplateCatalog`, `GitCliFetcher` behind `IGitFetcher`, `GitTemplateCatalogParser`. No forge API and no git server: any plain git URL the git CLI can read. See below. |
| Wiring the git catalog into the host (config file path, DI) | PLANNED | The catalog is a library type; no host setting names a catalog file yet. |
| Private-repository credentials | PLANNED | Entries carry no credentials. A private repository works only through the server's own ssh agent or git credential setup. A secret reference field is a later change. |
| Exporter (doc 44) and wizard template picker | PLANNED | Needs doc 44 and the wizard's company-source step. |

**Git catalog.** The operator lists template versions in a catalog file (`schemaVersion` 1, `templates[]`). Each entry pins `id`, an exact `version`, a plain `url`, a full `commit` id (40 or 64 lower-case hex; branch and tag names are refused), an optional `path` to the template folder, and the `templateHash` (section 1.8 hash). The file is strict JSON (no comments, no duplicate or unknown keys, 256 KB, 256 entries). Rules:

- **URL:** https and ssh by default; `file` only when the operator lists it. No credentials, query or fragment; an ssh user name without a password is allowed. Errors never echo the URL.
- **Fetch:** `git init --template=`, a shallow fetch of the one commit (a full fetch if the remote refuses a bare commit id), HEAD must equal the pinned commit, checkout with symlinks off, then the `.git` folder is removed. The process runs without a shell, with no prompt, no system or user git config, no hooks, no submodules, no LFS smudge, `GIT_ALLOW_PROTOCOL` set to the allowed schemes, and a time limit (default two minutes). Git's own output is dropped.
- **Pin:** the resolver refuses a template whose computed hash differs from the entry's `templateHash`, on every resolve, including a cached checkout. A fetch failure is a `company.source` error with fixed text.
- **Cache:** one folder per URL and commit under an operator-set cache root; a repeat resolve reuses it.

## 1.9 Provisioning API [BUILT behind its gates; no apply step registered yet]

| Route | Purpose |
|---|---|
| `POST /setup/plans` | Validates a plan (full or partial) and returns diagnostics and the plan hash. Writes nothing. |
| `POST /setup/plans/{hash}/apply` | Applies a plan whose hash matches. A repeat call with the same hash returns the stored result. |
| `GET /setup/status` | Step-by-step progress, the last Kodo result and any held approvals. |

- **Where it lives.** On the authenticated HTTP surface (doc 02), on its own rate bucket. JSON only; no client UI is served.
- **Access.** Before the first owner exists, only from loopback, and only with the one-time bootstrap code printed at the first start. This is the same pattern doc 32 uses for pairing codes. After the owner exists, only the owner role may call it, and each call is audited.
- **Host wiring.** The `setup` Bazaar Component (`Bazaar.Server.Setup.Host`, depends on `audit` and `settings`) registers the apply engine, this API and the durable Postgres stores. `Program.cs` maps the three routes only when the component is enabled (`Bazaar:Components:setup:Enabled`, default true), so a host without it answers 404 on `/setup/*` (the host's GET-only method guard lets `POST /setup/plans` and `POST /setup/plans/{hash}/apply` through and still answers 405 to every other write on `/setup/*`; `SetupHostEndpointsTests` covers the real host graph); a missing dependency answers 503. The bootstrap code is created at start while no owner exists (a `human` row with role `owner`), printed once to standard output and never logged; only its SHA-256 digest is stored (`setup_bootstrap_code`, setup migration 0002), it works once, and it is retired at the next start after an owner exists. The caller check uses the host's connection authenticator, the transport rule is `wire.allow_plaintext_overlay`, and the rate bucket is `setup_api` (30 a minute, working default).
- **Not yet.** No apply step is registered: an apply of a plan that changes anything ends in exit 3 with one audit row until the database and custody steps land. `GET /setup/status` lists registered steps as pending and holds no approvals.
- **Not a sign-in route.** These routes are outside the three sign-in routes of doc 32. Doc 02 must list them (conflict, section Conflicts).

## 1.10 Dependencies bundled as modules [DECIDED install rule; PLANNED mechanics]

- PostgreSQL 18 and 19 and pgvector are modules tagged "Deps". They are two versions of one module. Both are always supported, with day-0 support for betas.
- The install starts the bundled PostgreSQL as a hidden, supervised child process. It listens on loopback only, uses a data directory under the data root, and its files are readable only by the service account.
- No external installer runs for the user. An operator who already runs a PostgreSQL server may point the install at it. That is an operator choice, documented, not a user install step. It cannot be the default.
- Update policy for the bundled database is the module policy (section 1.3, step 2).

## 1.11 Nigerian Prince Protocol setup step [PLANNED; optional]

The brief names this step "Nigerian Prince Protocol". Doc 22 calls the same optional scam-baiting protocol by that name and proposes a neutral name for review (section Open questions). This doc uses the brief's name until the owner decides.

- **Position.** Step 12 of section 1.3, shown only if email is on.
- **Default.** Off. Scam mail is quarantined and never rendered with links (docs 15 and 22). This holds whether or not the step is taken.
- **Risk wizard.** States the risks in plain words, as doc 22 lists them: harm to real victims, harassment complaints, provoking criminals, malware, domain suspension, and legal exposure that varies by jurisdiction (needs counsel).
- **To enable.** The owner gives a dated acknowledgement, which is written to `audit_log`. The bait identity is a fictional persona with its own mailbox. It never uses an agent's address, a real person's name, Passport access, real payment data or real personal data. Every reply is owner-approved with a written reason.
- **Automatic stop.** Any sign of a real third party, any threat, any request for credentials or money stops the protocol and disables it.
- **Setting.** `security.scam_mail.protocol_enabled` (doc 15), false by default, changed only through the risk wizard, which is an owner-tier change.

## 1.12 Kodo verifies the finished setup [PLANNED; doc 46]

Kodo is the Health API module (module name Bazaar.KODO). It runs as the final setup step and can be run again at any time with `bazaar-admin kodo verify`.

- **Read-only.** Kodo's verification never changes state. A check that needs a write is a test, and it runs only in a sandbox copy.
- **Checks:** schema version and migration state; the instance lock; PostgreSQL and pgvector health; each provider role's usability (doc 03); every secret reference resolves, with no value shown; the audit chain head is writable and the anchor target is reachable (docs 14, 15); the backup target is reachable and a restore drill is scheduled (doc 33); the status bind is inside its floor; clock sync; disk headroom; each model source is reachable (doc 05); at least one owner second factor exists; the server's identity and key hash match the identity baked into the company build (section 2.1).
- **Identity KEK custody (doc 14, DECIDED 2026-11-19).** `identity.kekCustody` reads `custody.identityKek` and `identity.tpm.allowVirtual`. A plan that picks `tpm` and allows a virtual TPM is amber (a vTPM is weaker custody, and the acknowledgement names it); any other choice is green. The check reads the plan only. A VM host without the opt-in is refused by the TPM provider at start-up, which also records the custody class in the audit log (`identity.kek.custody`).
- **Result.** Green, amber or red, plus the 0 to 100 score described in doc 46. Red blocks "setup complete" but does not stop the running server. Amber completes only with a recorded acknowledgement that names each amber item.
- **Probe status (2026-11-19).** `host.disk` and `host.clock` run Kodo core's `DiskFreeProbe` and `ClockDriftProbe` through `IHostReadings` (**BUILT**, `KodoDiskHeadroomCheck`, `KodoClockSyncCheck`); setup ships a runtime `DriveInfoHostReadings` (disk and memory real; the clock has no reference source, so it stays not-evaluated, never green). Kodo core has no probes yet for the other twelve checks, so setup defines the seam `ISetupInfrastructureProbe` (**BUILT**, per check id) and `ProbeBackedCheck` (healthy green, degraded amber, failed red, unavailable not-evaluated); `SetupVerificationChecks.Create(probes, hostReadings, volumes)` builds the list. The Host adapter that supplies the real probes (schema, PostgreSQL, secrets, audit, backup, models, identity) is **PLANNED**; until then those checks stay not-evaluated placeholders.
- **Shown in** the wizard, the Monitor pane of every client (doc 46), and the read-only status page (summary only, doc 23).

## 1.13 Changing setup later [PLANNED]

- Every change after setup uses the same path: a change plan, a dry run, an apply, and an audit row per changed setting.
- Re-running setup on a finished install shows a diff against the current state. It never resets values.
- Settings that need approval are held until the approval is given (doc 03). A held change is visible in the client approvals view.
- A value that is fixed after the first data (`gc.partition_count`, doc 04) is refused with an explanation. Its only path is a planned data move.
- The restore wizard (doc 33) and server migration use the same plan format, so a restored or moved install is set up by the same rules.

## 1.14 No server tour [DECIDED]

The server has no tour, no first-run screen and no onboarding page. Setup is complete when the plan is applied and Kodo reports, whichever door was used. Tours exist only in the client apps (Part 2).

## 1.15 First-run seed [PROPOSED; the `constitution.seed` and `firstrun.seed` steps are BUILT]

A blank company has nobody to talk to. This step gives a fresh install one place to start, so "install, sign in, create a company, do real work" has something to do. It is a module of its own (`Bazaar.Server.Setup.FirstRun` with `.Abstractions`) that registers one apply step, `firstrun.seed`, after the owner (step 6) and company source (step 13) steps. Everything here is PROPOSED until the owner decides.

- **Plan section `firstRun`.** Keys: `seedGeneralGc` (bool, default true), `generalGcTitle` (default `General`), `seedDefaultAgent` (bool, default true), `defaultAgentName` (default `Claude`, owner 2026-11-22; the wizard lets the operator change it), `defaultAgentPurpose` (free text, may be empty), `defaultAgentSources` (model source ids; empty means every Claude source the gateway has), `defaultAgentProfile` (selection profile of doc 92, default `balanced`; the route is created over those sources and the profile bound to it), `acceptConstitution` (bool, default false). Unknown keys are rejected like every other section.
- **General group chat.** One GC of the seeded type `general` (doc 01), with the owner as a member and the default agent added when it exists. Created once; the step finds it again by a fixed marker on later applies, so a second apply changes nothing.
- **Default agent.** One agent with its own identity, in the `general` GC, named `Claude` by default. It takes its model dynamically from any Claude source the gateway has (the Claude Code CLI of doc 91, the owner's Anthropic API key, FreeLLMAPI, or another Claude source), by availability at run time (owner, 2026-11-22). Its whitelist holds only provider-qualified entries from those sources, narrowed by `defaultAgentSources` when the operator lists some; deny by default, doc 05. The wizard asks for the name, purpose and sources (the first-agent step); the create-company screen asks for name and purpose. No Soul content ships with it. It is created `provisioned` and **does not activate until the Constitution is accepted** (doc 24: no activation without an accepted Constitution commit). If the gateway has no Claude source, the agent is still created with an empty whitelist and the step reports that it cannot answer yet; it never routes through a source the gateway does not list.
- **Constitution acceptance.** `acceptConstitution` is the owner's own act, made on the create-company screen (a checkbox the owner ticks) or in the plan file the owner wrote. When true, the step records the acceptance for the current Constitution commit and the agent activates. When false, the agent waits and the dashboard says so. An acceptance is never inferred, never set by a default, and never made on the owner's behalf by an agent.
- **Audit.** One `setup.firstrun.seeded` row with the GC id, the agent id and whether acceptance was recorded. No secret appears.
- **Rollback.** Rolling back removes a seeded agent and its membership and leaves the GC row (the GC log is append-only, doc 01); a rolled-back GC is archived, not deleted.
- **Ports, not stores.** The step talks to three ports (GC creation, agent provisioning, Constitution acceptance) so it can run with fakes before the stores that back them land. The Postgres adapters are separate slices.
- **Gateway adapters (built, PROPOSED).** `Bazaar.Server.Setup.FirstRun.Gateway` implements `IDefaultAgentRoutes`: the whitelist is every available Claude-family model of every enabled gateway source, provider-qualified, plus one priority route `default-agent-<profile>` over them (the owner's own key first, then the rest). `defaultAgentSources` narrows it by source id; an id the gateway does not list is ignored. No Claude source gives an empty whitelist and writes no route. `DefaultAgentPolicyGrant` writes the agent's `agent_model_policy` row (entries = the whitelist, default route = the route entry, `changed_by` = `first-run-seed`) and never overwrites a row an operator edited. Binding the doc 92 selection profile to the route is not built yet; the route name carries the profile only.
- **Testing.** Idempotent apply; seed with and without acceptance; provider missing; the agent is not active before acceptance; rollback; and a canary secret check. The end-to-end smoke harness (gap 2) runs it through `POST /setup/plans/{hash}/apply`.

## 1.16 Doors and the graphical server installer [PROPOSED unless tagged; owner intent DECIDED]

Owner decision (relayed 2026-11-22), recorded in doc 58: the server installer comes in a full-screen terminal version (doc 58) and a **graphical version** (GUI), both running the same wizard. The fastest install is a headless install from a config file. This section designs the graphical door. Everything below is PROPOSED unless it carries a DECIDED tag; the owner's "no web client" rule and the Avalonia client decision (CLAUDE.md) are the DECIDED inputs it stays inside.

### 1.16.1 The doors and what each is for

| Door | Command or head | For | Status |
|---|---|---|---|
| Headless config file | `bazaar-admin setup --config <path>` (section 1.5) | Scripted and repeatable installs, containers, rebuilds. **The fastest install, and it needs no wizard** (owner, 2026-11-22). | DECIDED |
| CLI flags and environment | `setup --set key=value`, `BAZAAR__SETUP__...` | Small changes, overlays on a file. | PLANNED (section 1.1) |
| Plain console wizard | `bazaar-admin setup` (`ConsolePrompter`) | Any terminal, SSH, no TTY, screen readers, the accessible path. Stays. | PLANNED |
| Installer TUI | `Bazaar.Client.Tui.Installer` (doc 58) | A box with no desktop; people who want a walkthrough over SSH. | DECIDED scope (doc 58) |
| Graphical installer | A native Avalonia head (section 1.16.3) | A person at a desktop who wants a guided, visual install, mainly the Windows owner's PC (the first supported host). | Intent DECIDED, design PROPOSED |
| Provisioning API, templates | Section 1.9, 1.8 | Automation, and the in-client wizard for later changes. | PLANNED |

All doors are front ends to one thing, the setup plan (section 1.2). The GUI and the TUI offer the same full walkthrough, one question per step in the section 1.3 order, with no quick path inside them; the quick path is the config file. The GUI may offer "Open a config file" on the welcome screen only to load a file into the review (section 1.16.5), never to skip validation. Doc 45 section 1.1's "Not a TUI" wording is superseded by doc 58: the plain wizard stays as the accessible path, the TUI and GUI are opt-in beside it.

### 1.16.2 One engine, one prompter contract

The GUI is another `ISetupPrompter` over the same `SetupWizardEngine`, `SetupWizardSteps` and `SetupWizardBackend`. It owns how a question looks and nothing else: no step list, no validation rule, no plan path, no default, no hash. A rule that exists in a GUI view model and not in the engine is a bug (an architecture test and the conformance suite in section 1.16.9 enforce it).

The current port (`ShowMessage`, `ShowProgress`, `AskText`, `ChooseOne`, `ChooseMany`, `Confirm`, `AskSecret`, with `WizardQuestion` as data) already carries the question model: `Id`, `Prompt`, `Kind`, `Default`, `PlanPath`, `Options`, `Hint`, `Optional`, `Validate`, `Intro`. A console and a TUI are served by it as it is. A GUI needs more, and the additions must be **additive and optional** so the plain console and the TUI do not change:

| Need | What the contract exposes | Today | Proposed addition |
|---|---|---|---|
| Question model | Stable `Id`, `Kind`, prompt, intro, hint, default, options, `Optional`, `PlanPath` | Present | Add display metadata on `WizardQuestion` (all optional, all strings go through Localisation): `Help` (longer text), `Group` (the screen it belongs to), `OptionLabels` and `OptionDescriptions` keyed by option, `Advanced` (shown under "More options"), `Sensitivity` (plain, personal, secret). The ids and options stay the data; labels are display only. |
| Validation | One rule set, owned by the engine | `Validate` runs in the engine after an answer; a prompter re-prompts only widget-level errors | Expose `WizardQuestion.Validate` as a pure function the GUI may call while the person types (live feedback). The engine still re-validates every answer on submit, so a GUI check is a convenience, never the authority. Cross-field and plan-level validation (floors, port in use, provider probes) stay in the backend dry run and come back as `SetupValidationIssue` rows the GUI maps to a field by `PlanPath`. |
| Progress | `ShowProgress(SetupProgress)` with phase, current, total, step id | Present for questions, review, applying, finished | Add per-apply-step events (step name, started, done, skipped, failed, duration) and a log line stream, through an optional `ISetupProgressSink` the engine feeds from the apply result. A console ignores it. |
| Secret entry by reference | `AskSecret` returns a `SetupSecretInput` that is disposed; the engine stores it and writes only the `secret://` reference to the plan | Present | A GUI secret field never writes to a view-model property, binding, undo stack, clipboard history, accessibility tree or log: it uses a masked buffer type that maps straight to `SetupSecretInput`, clears on Dispose and on navigating away, and offers no reveal toggle in v1 (reveal is an open choice, default off). Where a secret already exists, the question takes **"use an existing reference"** (pick `secret://bazaar/...`, `env:` or `file:` per section 1.6) as an alternative to typing a value. The screen shows the reference name and its resolve status, never a value. |
| Back and forward | Forward-only: the engine asks each question once and a null return means "cancel" | Not supported | A prompter may return a `SetupNavigation` signal (`Back`, `Next`, `Cancel`) instead of an answer, through an optional `ISetupNavigator` the engine checks for (`prompter as ISetupNavigator`). The engine keeps its answer ledger (the `path=value` set it already builds), so Back rewinds to the previous step, pre-fills the earlier answers as defaults and replays forward; a changed answer invalidates later answers that depended on it (for example email off removes steps 11 and 12) and the engine says which. Cancel keeps today's meaning: stop, nothing written. Console and TUI that do not implement it keep the current behaviour. |
| Resume | Not applicable (one sitting) | Not supported | Two levels. (a) **Questions:** the GUI may persist the answer ledger (values and secret **references** only, never a secret value) to a draft file in the installer's own data folder, encrypted for the current OS user, so a closed window resumes at the last step. A draft is not a plan and changes nothing; it is deleted on apply or cancel. (b) **Apply:** already covered by the engine: each apply step is idempotent and resumes from the last committed step (section 1.2), so reopening the installer after a crash offers "Resume the apply" using the stored apply result (`IApplyResultStore`). Secrets entered but not yet stored are asked again. |
| Async | Synchronous `Ask*` methods | Fine for a console | The GUI cannot block its UI thread. Preferred change: an `IAsyncSetupPrompter` (the same members returning `ValueTask<T>`) that the engine prefers when present, with the synchronous port adapted for the console. Alternative: run the engine on a worker thread and marshal each ask to the UI thread. Recommended default: the async port, because it also lets a prompter show a spinner during probes. |
| Probes | Kodo and provider probes run in the backend | Dry run only | A question may declare a `Probe` (for example "test this model source", "check the port") the GUI offers as a Test button. The probe runs through the backend, is read-only, and returns issues in the same type. |

These additions go through the setup abstractions project and carry tests in the engine's own suite. Their shape is PROPOSED and one design pass should decide the exact names; the rule that they are additive and optional is the part to keep.

### 1.16.3 How the GUI is hosted

**DECIDED inputs.** Native, never a web client: browser-based clients are excluded, though a native client may embed web components (CLAUDE.md). One C# codebase for all clients; Avalonia is the client UI framework, with OSI-only packages. First supported and tested host: Windows. The server itself is headless, so the installer is a **client-side program that runs on the box (or the owner's PC) and talks to the setup engine in-process**, the same shape as the installer TUI (doc 58, Install mode: engine in-process, loopback and one-time bootstrap code, doc 45 section 1.9). It never adds a window or a listener to the server process.

**Options for the head:**

| Option | For | Against |
|---|---|---|
| A. Reuse `Bazaar.Client.Core` (the `UI.Core` library) and add an installer head | Theme loader, key router, localisation, accessibility settings, controls and look already exist; one theme file drives it (CLAUDE.md); fewest new moving parts. | `Client.Core` assumes a connection to a running server and brings the chat, Monaco, terminal and pane machinery the installer never uses, so the installer becomes large and drags more dependencies onto a fresh machine. It also makes the first-run installer depend on client code that changes often. |
| B. A tiny separate installer head (`Bazaar.Installer.Gui`, name provisional) on Avalonia alone, sharing only the theme loader and a small controls library | Small download, few dependencies, starts on a bare Windows box, stable surface. | Duplicates some control and theme plumbing unless the shared pieces are extracted; one more head to build and sign. |
| C. A shared slim kit: extract theme, localisation and base controls from `Client.Core` into a small library (`Bazaar.UI.Kit`, provisional) that both `Client.Core` and the installer head reference | Best long-term reuse, no duplication, small installer. | A refactor of `Client.Core` before the installer can ship; schedule risk. |

**Recommendation: B now, moving to C when the kit is extracted.** Start with a small separate head that reads the same theme file format and reuses `Bazaar.Client.Core` code only through the theme and localisation pieces, so the installer stays small and starts on a fresh box; extract the shared kit when a second consumer needs it. Reason: the installer runs before any server or client exists, so its footprint and dependency count matter more than reuse of pane code. **Open:** the owner may prefer A for lower effort; the head's name and the kit boundary are provisional under the naming rule in DECISIONS-LOG. Configurable: none of this is an operator setting; it is a build choice.

**Same family as the TUI.** The view models (step list, question state, validation messages, progress, review, apply log) are UI-free and shared by the GUI and the TUI heads through one `Bazaar.Client.Installer.Abstractions`-style library (provisional name), so the two differ only in rendering. This is how the "wizard logic stays in one place" rule extends to presentation state: the engine holds the logic, the shared view-model library holds the screen state, each head holds drawing.

**Packaging and launch (PROPOSED).** On Windows the graphical installer is the front of the Windows server package (doc 30 plans an MSI built with WiX): the MSI (or a small bootstrapper) places the files and registers the service, then starts the graphical installer for the setup plan. On Linux the tarball's install script starts the TUI by default and the GUI only when a display exists. The installer needs elevation only for the steps that need it (service registration, data root ACL), is asked at that step, and the GUI itself runs as the normal user. Auto-launch is a choice in the MSI. Signing follows the OSI-only, signing-mode rules (no signing infrastructure exists yet).

**Which installer opens.** `bazaar-admin setup` stays plain. A launcher picks the door: GUI when a display is available and `--gui` or the packaged shortcut is used; TUI when a TTY and no display; plain when `--plain`, `NO_COLOR`, a screen-reader flag, or no TTY. Override flags always win. Default order is configurable by the operator in the package, not a server setting.

### 1.16.4 Screens

Each screen is one or more wizard steps from section 1.3, in that order, with the engine's defaults shown. A left rail lists the screens with state (done, current, needs attention, not started); Back and Next at the bottom; every field has its hint and help text from the question model; every state is words, not only colour.

| # | Screen | Steps | Content |
|---|---|---|---|
| 1 | Welcome and requirements | 0 | What will happen, the licence summary (AGPL-3.0, with module licences shown in step 14), and a Kodo preflight probe list: OS and edition (an operator fact), CPU, memory, disk headroom, free ports, time sync, the data root and its ACL, a non-Administrator service account. Each probe shows pass, warn or fail with the fix in plain words. A fail on a floor blocks Next; a warn needs an acknowledgement. Probes are read-only (Kodo, section 1.12). |
| 2 | Install location and data root | 3 | The data root (default per OS, `%ProgramData%` on Windows), disk space needed against free, ACL check, release channel (Beta while 0.x), update policy confirm (automatic for security, manual for the rest). The volume and encryption choice of step 3 is shown here as its own block, because setup cannot finish without it ([PROPOSED, doc 63 section 10]). |
| 3 | Database | 3, 4 | The bundled PostgreSQL (the default, hidden child on loopback), the major version default, the GC partition count (16, fixed after first data, with that warning). **An external database never appears here** (DECIDED, owner 2026-11-16; hidden advanced or headless setting only). |
| 4 | Identity and key custody | 5, 6 | The key-encryption-key provider (default `dpapi` on Windows, systemd-creds on Linux; `openbao` optional), the audit anchor target (required), the owner's name, passkey or hardware-key enrolment, and the encrypted recovery bundle. A passkey ceremony is the one place a GUI needs the OS: see section 1.16.8. Second factor is optional with a warning (DECIDED, owner 2026-11-16). |
| 5 | Privacy, retention and backups | 7, 8 | Privacy profile (private instance by default), retention and erasure window (most protective option, "needs counsel" notes), backup target and schedule. Finishing with no backup target needs a recorded acknowledgement (DECIDED, owner 2026-11-16), shown as a checkbox that names what is being accepted. |
| 6 | Gateway sources | 9 | The FreeLLMAPI guided setup (priority source, with its own probe), the owner's Anthropic API key by reference (entered as a secret or picked as a reference), optional custom proxies and local runtimes, and the default free-lane preset. A subscription login is refused with the reason, on every route. A note says that some free tiers train on prompts (CLAUDE.md). |
| 7 | Company and owner | 1, 13, 10 | Company name and slug (slug cannot change later), locale and timezone, the company source (blank, template at a version, or import), the merge credential (no default, must be chosen), the GitHub App installation. |
| 8 | First agents and modules | 14 | The first module set with each module's licence shown and checked. First agents come from the template or the module set chosen here; the installer does not invent a roster and writes only what the plan holds. Email (11) and the scam-mail protocol (12) are optional sub-steps off by default; the latter shows only when email is on. |
| 9 | Network | 15 | Status listener bind (loopback; any other bind needs Tier 2 approval, so the field explains the hold rather than failing silently). |
| 10 | Review plan and dry run | 16 | The change list (create, change, hold), the unapplied sections (section 1.3), every validation issue with a link back to its screen, the secret references with status (resolves, missing, not allowed) and the plan hash. The person confirms by typing or pasting the start of the hash, the same rule as the wizard, so a click cannot apply a plan they did not read. A **Save as config file** button writes the canonical plan (references only) so the install can be repeated headlessly. |
| 11 | Apply | 16 | A step list with live status and a log pane, below. |
| 12 | Done and first sign-in | 17, 18 | The Kodo report (green, amber or red with the score; red blocks "complete", amber needs an acknowledgement that names each item), where the client packages were built, and a button that starts the first client sign-in on this machine through the loopback and one-time bootstrap code of section 1.9. There is no server tour (section 1.14). |

Screens 4 to 9 can be reordered by the person only within the engine's dependency order; the step order of section 1.3 is the engine's, not the GUI's.

### 1.16.5 Same plan, same hash

The GUI collects answers as `path=value` pairs through the engine, exactly like the console does, and never builds plan JSON itself. The backend normalizes, validates and hashes (section 1.2): sorted keys, no insignificant whitespace, SHA-256, secrets as references only. So the same answers produce the same canonical plan JSON and plan hash on the GUI, TUI, console, file and API doors, and the audit row differs only in the entry path. Section 1.2 names four entry paths (`wizard`, `cli`, `file`, `api`); the TUI and GUI both use `wizard` because they run the same wizard, and a separate **front-end** field (`console`, `tui`, `gui`) is recorded beside it, as data, for the audit view (doc 48). **Open:** whether that field is worth a schema change; the default is to record it. Loading a config file into the GUI feeds its values as the starting answers, then the person still walks the review.

### 1.16.6 Failure, rollback and what the GUI shows

The engine and applier own failure handling (section 1.2: each step is idempotent, a failed step rolls back its own transaction and stops the apply with exit code 3). The GUI only shows it:

- **Per-step state:** pending, running, done, skipped (already at the planned value), held (needs approval), failed, rolled back. Each has a word, an icon and a colour.
- **On failure:** the apply stops, the failed step is expanded with the step's message (key and rule, never a value), the list shows what was committed and what was rolled back, and the screen offers **Retry** (resume from the last committed step, same plan hash), **Back to review** (the plan may be edited, which makes a new hash and a new dry run) and **Copy report** (a plain-text report with no secret values, for a bug report). No "force" button exists.
- **Held changes** (a change that needs approval) are shown as held, with where to approve them; they are not errors (exit code 2).
- **Closing the window mid-apply** asks first and, if confirmed, leaves the apply to finish or stop at a step boundary; the next launch offers the resume described in section 1.16.2.
- **Unapplied sections** (section 1.3) are listed on the review screen before any change, so the person sees up front what this build cannot yet apply.
- **Crash of the installer itself** cannot corrupt an install: it holds no state the engine does not also hold, and the instance lock (doc 03) is the engine's.

### 1.16.7 Accessibility and theming

- **Theme:** the installer reads the same one theme file format that drives Avalonia and Monaco (CLAUDE.md), with a built-in light and dark default and the OS high-contrast setting honoured. No hard-coded colours.
- **Keyboard:** every screen is fully usable by keyboard (Tab order that follows reading order, Enter for the default action, Escape for Back with confirmation). The client's mnemonic and command-palette default (doc 41) is **not** applied in the installer: it is a short fixed flow, and the installer runs before the client keymap exists. Plain, visible shortcuts only, no Ctrl chords (the owner's rule), remappable is not required.
- **Screen readers:** each control has an automation name and help text from the question model, errors are announced, progress changes are announced at a calm rate (not every line), focus moves to the first error on a failed Next. A real screen-reader pass in the owner's environment is a release gate (headless tests cannot cover it, doc 17). The plain console wizard remains the documented accessible path (doc 58 section 4).
- **Text and layout:** scales with the OS text size, no information in colour alone, no fixed pixel layouts, strings through Localisation, a minimum window that fits 1024 by 768.
- **Motion:** no animation that carries meaning; a reduced-motion setting is honoured.

### 1.16.8 No display, remote and passkeys

- **No display (headless-safe).** The graphical head checks for a usable display and graphics backend at start. If there is none (SSH, a server box, a failed backend, a container), it prints one plain line saying why and **falls back to the installer TUI when a TTY exists, otherwise to the plain wizard**, or to the headless file door when `--config` was given. It never exits with a crash and never leaves a half-drawn window. The fallback keeps the person's already given answers where it can (the draft of section 1.16.2).
- **Remote:** the installer runs on the machine being installed. There is no remote GUI installer, no web page and no listener (no web client, ever). Installing a Linux box from a Windows PC is done over SSH with the TUI or plain wizard, or by a config file.
- **Passkey ceremony:** the owner enrolment needs a platform authenticator. The GUI hosts it through the OS (WebAuthn on Windows) behind a provider interface, the same boundary as every OS-specific piece (CLAUDE.md), and the installer's narrow interactive sign-in page exception (a passkey page) is the only allowed browser surface if the OS API is not usable. Without any usable authenticator, the step follows the DECIDED rule: second factor optional with a warning, with recovery and high-risk actions blocked until one is added.

### 1.16.9 Testing

- **Conformance suite (shared with doc 58):** one suite drives the plain, TUI and GUI prompters through the same scripted answers and asserts equal plan JSON, equal plan hash and equal audit rows apart from the front-end field. Real PostgreSQL for the apply (doc 34).
- **Avalonia.Headless** (required for UI changes): a headless test per screen (renders, shows defaults, shows a validation message and maps a backend issue to the right field), forward and back with answer preservation and dependent-answer invalidation, resume from a draft and from a stored apply result, the review hash confirmation (a click alone does not apply), the apply screen against a fake backend that fails at step N (retry, back to review, copy report), and a keyboard-only walk of the whole flow with no pointer input.
- **Secret canary:** a fake secret entered in the GUI must not appear in any plan, draft file, log, view-model property, automation tree or error. The draft never contains a secret value.
- **Fallback:** with no display available the launcher chooses the TUI or the plain wizard and exits zero on a successful install.
- **Architecture test:** the installer head references neither `Bazaar.Server` nor chat client code, holds no validation rule that is not in the engine, and builds no plan JSON.
- **Accessibility pass:** the manual screen-reader pass of the Testing section, extended to the GUI.

### 1.16.10 Open questions for the owner (defaults recommended)

1. **Head choice.** A (reuse `Client.Core`), B (tiny separate head) or C (extract a shared kit). Default: B now, C later (section 1.16.3).
2. **Async prompter.** An async port the engine prefers, or the engine on a worker thread. Default: the async port.
3. **Draft persistence.** Allow a resumable answer draft (references only, encrypted for the OS user), or restart on close. Default: allow.
4. **Front-end audit field.** Record `console`, `tui` or `gui` beside the entry path. Default: record.
5. **Secret reveal.** Allow a reveal toggle on secret fields. Default: no.
6. **Windows package.** Is the graphical installer the front of the MSI (default), a separate download, or both. Default: the front of the MSI, with the TUI and plain wizard reachable from the same package.
7. **Names.** `Bazaar.Installer.Gui` and the shared installer view-model library are provisional under the DECISIONS-LOG naming rule.

---

# Part 2: Client first run

## 2.1 Automatic connection, no pairing [DECIDED, owner 2026-11-16]

- **Per-company packages.** The package built for a company (doc 37) is bound to the server that built it. It carries the company identity, the server address and a pin of the server's key hash (doc 32, trust-on-first-use pin).
- **What is automatic.** On first launch the client connects to the server it was built for. The person types no host, no code and no key. There is no pairing request, no pairing code and no device approval step, because the client is not a separately enrolled device.
- **Who signs in.** The person signs in as a user (doc 32). The second factor stays a separate step (doc 14, step 6). Neither one is a pairing step.
- **Keys.** The client holds only what it needs to sign in as a user. It never holds a GitHub credential (doc 27, doc 14).
- **Other devices.** Doc 32 section 3 describes a pairing flow for other devices and accounts. The owner's 2026-11-16 answer removes device pairing from the client path. Whether doc 32 section 3 is removed or kept for another purpose is open (Open question 1).

## 2.2 Intro [PLANNED]

- Up to three short screens: what the company is, the start dashboard (what changed while away, doc 27), and where the main areas are.
- Skippable at any point. Nothing needs reading before the app works.
- No timers, no auto-advance, no content that appears only through animation. Reduced motion is honoured.
- Screen-reader labels and translated strings on every screen.

## 2.3 Interactive keyboard-first tour [PLANNED]

The tour runs in the client's own company with the person's own data. It does not use a sample company (owner decision). Where a step needs data that does not exist yet (for example unread messages), the tour says so and shows the empty state.

| Step | Teaches | Real action in the step |
|---|---|---|
| 1 | Focus model: nothing is focused by default; leader `Space` | Press `Space`, then read the leader menu |
| 2 | The command palette, opened by its mnemonic; fuzzy search; no Ctrl chords | Open the palette and find a command |
| 3 | Mnemonic navigation (doc 41) | Jump with examples from the brief: `ngc1`, `topgc`, `unread`, `settings` |
| 4 | Panes and splits | Split the view and move a pane |
| 5 | Layouts and posture: folded, unfolded, half-open at 600 dp (docs 27, 28, 29) | Switch layout variant; on a device with a fold, the change is shown live |
| 6 | Remapping a key or a mnemonic (doc 41) | Remap one command and undo it |
| 7 | Import, through data portability (doc 44) | Read where importers are; optional |
| 8 | Finish; re-run from the palette | None |

- **Skip and re-run.** Skippable at every step. Re-run through a palette command with its own mnemonic (set in doc 41; the working name is `tour`). Re-running never resets settings.
- **Where progress lives.** Progress is a human preference, saved with `prefs.set` (doc 27), so it follows the account to every device. A local cache may hold it as a convenience. The app must work with that cache empty.
- **Terminal step.** Only if the person opens Calcinato. It is Vim-style with no Ctrl chords, and local-NPU suggestions are off by default (brief, doc 41).
- **Android variant.** The radial menu's centre is dictation (doc 29). The Android tour teaches the radial, its sectors and dictation, and the mnemonics through the on-screen keyboard. It does not teach keyboard focus.
- **Accessibility.** Every step works by keyboard alone. Each step has a screen-reader label. Strings are translated. Focus moves only when the person moves it.

## 2.4 No sample-data sandbox [DECIDED]

The owner has not chosen a sample-data sandbox company. Empty states explain what will appear and where, and link to the tour and the import step. Development sandboxes for module authors (full tier: process isolation, a fake-data test company) are a separate feature for the marketplace (brief, gap decisions) and are not part of onboarding.

## 2.5 Import at first run [DECIDED via doc 44]

The import wizard is offered after the tour and from the palette. It is the same importer set as the server's import source (section 1.8). Nothing is imported without an explicit choice.

## 2.7 Who is signed in [PROPOSED]

After sign-in a client reads the signed-in person (human id, display name, role, device id) from `GET /auth/whoami` (doc 32 section 5) with its access token, instead of decoding the token itself. A 401 means sign in again. Signing out calls `POST /auth/signout` (doc 32 section 5), which revokes this device only; the client then clears its stored credentials.

## 2.6 Sign-in and create-company screens [PLANNED; the handoff route's body is PROPOSED]

Two client modules, each its own project set (`Bazaar.Client.SignIn` and `Bazaar.Client.CompanySetup`, each with `.Abstractions` and `.Avalonia`). They add no code to Client.Core. A head (Windows first) shows them with `--sign-in`, `--create-company` or `--first-run`.

- **No host to type, no company to choose.** The client already talks to the server it was built for (section 2.1) and an install has one company (decided inputs), so there is no server picker and no company list. The first screen only checks that the server is up (`GET /health/ready`) and offers Retry when it is not.
- **Sign-in screen.** The person signs in on the server's own sign-in page, opened in their browser (`/signin?audience=native`, doc 32 section 4). The page hands back a single-use code, as `bazaar://signin?code=...` or as text to paste. The client redeems it at `POST /auth/handoff` with `{ "code", "device_class", "client_build" }` and expects the same token body as `POST /auth/refresh` (`access_token`, `token_type`, `expires_in`, `refresh_token`). **The `/auth/handoff` request and reply shape is PROPOSED** until the route is built (doc 32 names the route only). The client never draws a password or passkey prompt and never sees a credential other than the tokens. A saved session is reused, refreshed when its access token has expired, and cleared when the server refuses the refresh. Every failure shows one generic message.
- **Session storage.** A port (`ISessionStore`). The default keeps the session in memory only, so a restart signs out. A protected store per platform is the module `Bazaar.Client.SignIn.Storage`: one file sealed through the head's `ISecretProtector` (DPAPI on Windows; on Linux an AES-GCM protector whose key sits in libsecret or, as the reported fallback, a 0600 key file, see doc 30; Keystore comes with the Android head), written atomically, and a file that cannot be opened reads as signed out.
- **Create-company screen.** The setup wizard in a connected client (section 1.1), over the provisioning API (section 1.9). It collects company name, identifier (derived from the name, editable, fixed after creation), owner name, locale, time zone, key-encryption-key provider and reference, and the recorded no-backup-target acknowledgement, and the owner's own tick that accepts the Constitution (`firstRun.acceptConstitution`, section 1.15; never on by default). It writes a sparse setup plan with `company.source` = `blank`; the server fills every other decision with its shipped default and hashes the normalized plan. The screen checks first (`POST /setup/plans`, writes nothing), shows the plan hash and the server's findings, and applies only that hash (`POST /setup/plans/{hash}/apply`). Before the owner exists the call carries the one-time bootstrap code from the server console (header `X-Bazaar-Bootstrap-Code`); after, the owner's bearer token. A held or failed apply returns to the form with the reason; a replayed apply says the company already exists.
- **Not on these screens.** Template and import sources, backup targets, model sources, email, the owner's passkey enrolment and the intro and tour. Each is its own step or module.
- **Testing.** Avalonia.Headless tests drive each screen by its automation ids (`signin-*`, `company-*`). A test feeds the plan the client writes through the server's own `SetupPlanLoader`, so the two cannot drift apart. The smoke harness drives the same ids.

## 2.8 Native sign-in redeem [shape built; flow PLANNED]

The client's sign-in ends at `bazaar://signin?code=...` (doc 32 section 4). The client redeems the code with `POST /auth/handoff` `{ "code", "device_class", "client_build" }` and receives `{ "access_token", "token_type", "expires_in", "refresh_token" }`, the same body as `/auth/refresh`; the refresh secret goes in the platform keystore and the access token stays in memory. Every failure is one generic 401 `invalid_grant`, so the client shows one "sign in again" message and starts the page flow again. The request and reply shape is now built in `Bazaar.Server.SignIn` (doc 32 section 4, status DECIDED-in-code); the client screens that drive it are still PLANNED, and the second factor stays a separate step (section 2.1).

---

## Conflicts and gaps

Each row names the place where the sources disagree, and the resolution this doc uses until the owner decides.

| # | Where | Conflict | Resolution used here |
|---|---|---|---|
| 1 | Doc 03 line 23 and line 146 (NUC checklist); doc 04 line 10 | Those docs assume a PostgreSQL 18 service on the host. The brief says bundled, run as a hidden supervised child process, with zero external installs. | Bundled child is the default (section 1.10). Bring-your-own is an operator option. Owner question 2. |
| 2 | Doc 32 lines 39 to 44 (native approval); brief ("automatic pairing", "no manual pairing") | Automatic pairing and native approval pulled in different directions. | Resolved by the owner, 2026-11-16: there is no device pairing for clients. Each client is built for one server and connects to it automatically (section 2.1). |
| 3 | Doc 04 line 67 (`db.migrate_on_fresh_install`) against doc 03 line 15 and doc 04 line 16 (C44: explicit `migrate` only) | A setting that migrates on a fresh install could read as an automatic migration at startup. | Setup runs `migrate` as an explicit step on an empty database (section 1.3, step 4). The setting is only a setup default. Startup still only checks the schema version. |
| 4 | Doc 05b line 17 (`env:<VAR>` or `file:<path>` for the FreeLLMAPI key) against doc 14 and doc 03 (every non-bootstrap secret goes through `ISecretStore`) | Two reference forms for one provider key. | Config holds `secret://` references for provider keys. `env:` and `file:` are bootstrap-only (section 1.6). Doc 05b to be updated. |
| 5 | Doc 02 HTTP surface (C15) and doc 32 line 52 ("exactly three" sign-in routes) | Provisioning routes are a new HTTP surface. | They sit outside the sign-in routes and under the authenticated HTTP surface (section 1.9). Docs 02 and 32 must list them. |
| 6 | Doc 22 line 180 (rename proposal) against the brief | The brief's name is "Nigerian Prince Protocol"; doc 22 proposes a neutral name. | This doc keeps the brief's name until the owner decides. Owner question 6. |
| 7 | README (docs 01 to 38 only) | Docs 41, 44, 46 and 48 are named by the brief but did not exist in `docs/design/` when this row was written. They are in the same PR now. | Links are kept and marked "not yet in docs/design/". Their content must not be assumed here. |
| 8 | CLAUDE.md (Apache-2.0 for Bazaar.Sdk and Bazaar.Contracts) against the brief (AGPL-3.0 across all repos, plus a module exception and a CLA for core code) | Not an onboarding question, but the module licence check at install (step 14) depends on it. | Not resolved here. Recorded for the owner to confirm, and for doc 37 and the NOTICE file. |
| 9 | Doc 03 Q2 (admin CLI in v1, default: small non-TUI `bazaar-admin`) against the brief's "interactive wizard" | The wizard's home is not fixed: console prompts or a client. | Both: console prompts for the first install (no client exists yet), and the same wizard in a connected client afterwards (section 1.1). |

## Open questions for the owner

Each question has a default, the one this doc uses.

1. **Device pairing. DECIDED (owner, 2026-11-16): none for clients.** Each client is built for its server and connects to it automatically (section 2.1). Open for the owner: whether doc 32 section 3 (offer and approval flow) is removed, or kept only for other devices and accounts. Default until decided: keep it, unused by clients.
2. **PostgreSQL. DECIDED (owner, 2026-11-16):** bundled by default. An external database is a hidden advanced or headless setting, and it never appears in the install wizard (doc 42 section 18 Q2).
3. **Provisioning access.** Loopback plus a one-time bootstrap code until the owner exists (default), or a signed installer token? Reason: no network surface before the owner exists.
4. **Second factor to finish setup. DECIDED (owner, 2026-11-16): optional with a warning.** Setup can finish without a second factor, with a warning. Recovery and high-risk actions stay blocked until one is added (docs 14, 32).
5. **Backup target. DECIDED (owner, 2026-11-16): finishing without a target is allowed only with a recorded acknowledgement.** The acknowledgement is written to the audit log, and Kodo keeps flagging the missing target (doc 33, doc 46). The operator's choice of target is kept.
6. **Scam-mail protocol name. CLOSED (owner, 2026-11-16):** keep "Nigerian Prince Protocol". The doc 22 rename proposal is dropped.

## Testing [PLANNED]

- **Equivalence:** the same plan entered through the wizard, the config file, flags and the API produces the same plan hash and the same audit rows, differing only in the entry path. Real PostgreSQL (doc 34).
- **Dry-run writes nothing:** a database and filesystem snapshot before and after dry-run are identical.
- **Idempotent apply:** applying a plan twice gives the stored result the second time; a crash after step 4 resumes at step 5.
- **Canary secret:** a fake secret placed in each input path must not appear in any output, log, plan, audit row or error. A literal value under a secret key fails validation with the key name only.
- **Floors:** each doc 03 floor (Administrator account, non-loopback bind, Postgres removed from readiness) is rejected with a clear message and exit code 2 or a held approval.
- **Validation golden files:** the sketch in section 1.5 and one invalid variant per rule.
- **Kodo verification:** golden report with fake providers and real PostgreSQL; a red result blocks completion but not the running server.
- **Nigerian Prince step:** off by default; enabling requires the acknowledgement row; declining leaves scam mail quarantined.
- **Client:** Avalonia.Headless tests for the intro, each tour step, skip at each step, re-run, the progress preference, the empty-state text, and the Android radial variant. A keyboard-only walk covers the whole tour with no pointer input.
- **Accessibility:** a screen-reader pass on the console prompts and the tour, in the owner's environment, before release (headless tests cannot cover it, doc 17).

## Links

- Brief and decisions: `docs/design/DECISIONS-LOG.md` (round 13 configurability rule), `CLAUDE.md`.
- Server: [03](03-server-host-and-providers.md), [04](04-postgres-schema-and-migrations.md), [14](14-identity-service-and-passport.md), [15](15-security-taint-and-approvals.md), [22](22-email-subsystem.md), [33](33-backup-dr-and-export.md).
- Clients: [27](27-client-architecture.md), [28](28-windows-client.md), [29](29-android-client.md), [32](32-remote-access-auth-and-privacy.md).
- Packaging: [37](37-distribution-matrix-and-packaging.md).
- Update 2026-11-13: 41 (mnemonics), 44 (data portability), 46 (Kodo) and 48 (audit view) are now in the same PR.
