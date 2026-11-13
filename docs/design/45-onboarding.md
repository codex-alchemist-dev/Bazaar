# Onboarding: Server Setup and Client First Run

Status: DRAFT for owner review (2026-11-13). The direction is DECIDED by the owner (brief of 2026-11-13). Every mechanism below is PLANNED (designed, not built) unless a line says otherwise. Owner questions: 6
Depends on: 02-wire-protocol-and-sync.md (HTTP surface, status), 03-server-host-and-providers.md (config layers, settings registry, floors), 04-postgres-schema-and-migrations.md (`migrate`, partitions), 05-model-gateway.md (sources, routing), 14-identity-service-and-passport.md (`ISecretStore`, KEK, merge credential), 15-security-taint-and-approvals.md (`audit_log`, scam-mail setting), 22-email-subsystem.md (mail back ends, bait-protocol wizard), 24-company-systems-core.md (Constitution), 27-client-architecture.md (client shell, prefs, keyboard model), 29-android-client.md (radial menu), 32-remote-access-auth-and-privacy.md (pairing, recovery, privacy profile), 33-backup-dr-and-export.md (backup targets, restore), 37-distribution-matrix-and-packaging.md (per-company packages). Named in the brief but not yet in `docs/design/`: 41 (client mnemonic navigation), 44 (data portability and importers), 46 (Kodo health API), 48 (audit view).
Used by: 30-linux-client-and-packaging.md, 34-testing-strategy.md

## Purpose

A new Bazaar install must reach a correct, safe, running company without a manual checklist, and the owner must be able to do it from a terminal with no client at all. This doc covers two halves that are designed together but built separately:

1. **Server setup** (Part 1): how a Bazaar.Server install is created. It is headless first. Every route ends in the same result and the same audit trail. A server has no tour.
2. **Client first run** (Part 2): what a person sees the first time a Bazaar client opens against a company. It pairs automatically, shows a short intro, and offers a keyboard-first tour that can be skipped and re-run.

## Decided inputs

| Input | Source |
|---|---|
| Server setup is headless-first: interactive wizard, declarative headless config file, CLI flags and environment variables, provisioning API, reusable company templates, config validation with dry-run, and Kodo verifies a finished setup. Every path yields the same result and the same audit trail. | Owner brief, 2026-11-13 |
| Server setup must be fully headless. Tours are for client apps only. | Owner brief, 2026-11-13 |
| Client first run: automatic pairing, an intro, and an interactive keyboard-first tour (mnemonics, panes, layouts). Skippable and re-runnable. | Owner brief, 2026-11-13 |
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

## 1.1 Entry points [PLANNED]

| Entry point | Who uses it | Notes |
|---|---|---|
| Interactive wizard, `bazaar-admin setup` | Owner at the server console, or over SSH | Plain text prompts, one question at a time, keyboard only, readable by a screen reader. Not a TUI (doc 03 Q2 default is a small non-TUI `bazaar-admin`). |
| Wizard in a paired client | Owner, after pairing | Same steps, shown in the client's admin panel (doc 27 settings surface). Used for later changes more than first install. |
| Declarative config file | Operators, scripted installs, reproducible rebuilds | `bazaar-setup.json`, applied with `bazaar-admin setup --config <path>` (section 1.5). |
| CLI flags | Quick single changes, scripts | `bazaar-admin setup --set company.name=...`. Flags carry values and references only. |
| Environment variables | Containers, service managers | `BAZAAR__SETUP__...`, the same double-underscore convention as doc 03. Values and references only. |
| Provisioning API | Automation, and the clients | Authenticated HTTP, section 1.9. |
| Company templates | Owners who run several companies on the same kind of setup | Section 1.8. A template is an input to the plan, not a copy of a live company. |
| Import | Owners moving an existing company in | A dump is chosen as the company source. Importers are doc 44 modules. |

There is no separate server-side tour, no onboarding web page and no first-run screen in the server. The server's only human surfaces are the setup prompts and the read-only status page (doc 02, doc 23).

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
| 3 | Data root and bundled PostgreSQL | Creates the data root, starts the bundled PostgreSQL 18 or 19 with pgvector as a hidden supervised child on loopback, and sets the data directory ACL. Choice of major version: section 1.4. | 03, 04, owner brief |
| 4 | Schema | Runs the explicit `migrate` command once on the empty database under the instance lock. Records fixed values (partition count) in `schema_setting`. | 04 (C44) |
| 5 | Custody | Sets the key-encryption-key provider, bootstraps the Identity Service, and sets the audit anchor target. Mounts stay disabled until the anchor is set. | 14, 15 |
| 6 | Owner | Creates the owner identity, enrols at least one passkey or hardware key, creates the encrypted recovery bundle, and offers the first device pairing (section 2.1). | 32, 14 |
| 7 | Privacy and retention | Privacy profile, retention periods, legal-hold defaults, erasure window. Each option that touches law is marked "needs counsel". | 32, 15, 33 |
| 8 | Backups | Backup target (operator's choice), schedule, and restore-drill cadence. Finishing without a target needs a recorded acknowledgement. | 33 |
| 9 | Model sources | FreeLLMAPI guided setup (priority source), the owner's provider API key by reference, optional custom proxies and local runtimes, the default free-lane preset. Subscription logins are refused on every route. | 05, 05b, 14 |
| 10 | Git and GitHub | Merge credential (owner GitHub App or linked owner credential; no default), GitHub App installation for the company's organisation. | 14, 21 |
| 11 | Email (optional) | Off by default. If enabled: back end, operator domain, DNS checker (SPF, DKIM, DMARC). Domains are never hardcoded. | 22 |
| 12 | Scam-mail protocol (optional) | Shown only if step 11 is on. Off by default. Section 1.11. | 15, 22 |
| 13 | Company source | Blank company, a template at a version (section 1.8), or an import (doc 44). The Constitution is drafted here and must be accepted by the owner before any Soul activates. | 24, 44 |
| 14 | Modules | The first module set (Bazaar.GC is always present; Constellation, Conduit, KODO, RyuseiFuda and others by choice). Each module's licence is shown and checked against the licence policy. | 37, brief |
| 15 | Status and network | Status listener bind (loopback by default; any other bind needs Tier 2 approval). No inbound internet connection by default (doc 03). | 03, 02 |
| 16 | Plan review and apply | Shows the change list and the plan hash. Apply runs under the lock. | 1.2 |
| 17 | Kodo verification | Read-only checks and a green, amber or red report with a 0 to 100 score. Section 1.12. | 46 (not in tree) |
| 18 | Client packages | Builds the per-company packages (.msix, .apk, .exe) on the server. Signing keys stay on the server. | 37 |

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
| Key-encryption-key provider | Yes | `dpapi` on Windows; systemd-creds on Linux (doc 03) | `openbao` optional (doc 14). |
| Audit anchor target | Yes | Required, no default | Mounts stay disabled until set (doc 14). |
| Owner passkey or hardware key | Yes | Required to finish [PROPOSED] | A second factor is needed for recovery and high-risk actions (gap decision: biometrics and MFA). |
| Recovery bundle and printed codes | Yes | Bundle created; printed codes per `recovery.printed_codes_enabled` (doc 32) | Backup-owner quorum is an optional list (doc 32). |
| Privacy profile | Yes | Private instance | Compliance settings apply automatically when a second human member exists. Needs counsel for the wording of the options. |
| Retention and erasure window | Yes | The most protective documented option | Doc 04 lists 35 days as a placeholder. Needs counsel. |
| Backup target | Yes | Required; finishing without one needs a recorded acknowledgement [PROPOSED] | Operator's choice, documented in doc 33. |
| Model sources | Yes | FreeLLMAPI guided setup; default free-lane preset | Owner's API key optional. Routing defaults are doc 05's. |
| Merge credential | Yes | None; must be chosen (doc 14, round 12) | Owner GitHub App or linked owner credential. |
| Status listener bind | Yes | Loopback | Non-loopback needs Tier 2 approval (doc 03). |
| Email | Yes | Off | If on, a back end and domain are required (doc 22). |
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
    "kekRef": "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
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
  "email": { "enabled": false },
  "scamMailProtocol": { "enabled": false },
  "company": { "source": "template:starter@1.0.0" },
  "modules": ["Bazaar.GC", "Bazaar.Constellation", "Bazaar.KODO"],
  "network": { "statusBind": "loopback", "statusPort": 8443 },
  "settings": { "host.shutdown_timeout_seconds": 20 }
}
```

The file is a plan input. It is never executed and never contains a secret value. It may be committed to git (private repository) because it holds references only. Doc 33 backs it up as configuration.

## 1.6 Secrets handling [PLANNED]

- **No secret value in the config file, in flags, in environment variables, in templates or in the plan.** Any value under a secret-typed key must be a reference. A literal value fails validation, and the error names the key only, never the value.
- **Two reference families.** Bootstrap values (the KEK and the database credential) use `env:NAME`, `file:<path>`, `dpapi:<path>` or `systemd-creds:<name>`, as doc 03 defines. Every other secret uses `secret://bazaar/...`, resolved by `ISecretStore` (doc 14) under a purpose the policy allows (doc 05 maps provider keys to this form). This resolves the earlier mixed wording in doc 05b (section Conflicts).
- **Entering a secret.** The wizard prompts without echo. `bazaar-admin secret set <reference>` reads the value from a prompt or from standard input. It never accepts the value as an argument, so it does not reach shell history or the process list. The value goes straight into `ISecretStore`.
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

## 1.8 Company templates [PLANNED]

**What a template is.** A versioned folder in a git repository (the operator's own, or one Bazaar ships), containing:

- `template.json`: `schemaVersion`, a semver `version`, required modules, settings presets from the registry (doc 03), role definitions including custom roles, GC types to register (doc 01), Kanban columns (Backlog, Ready, In progress, Review, Done, with automatic Blocked marking), calendar and quota presets (doc 24, doc 09), and the Soul presets to offer (doc 07 export format).
- Markdown seeds: a Constitution draft, role charters, and the Vault folder layout (doc 18). They become Vault files when applied.

**What a template never contains.** Secret values or references to real secrets; GC messages; Soul instances, memories or agent history; personal data; the owner's identity; provider or GitHub accounts. Soul presets are behavioural only (doc 07). A preset does not activate an agent: the Constitution must be accepted first (doc 24).

**Use.** The plan's `company.source` is `template:<id>@<semver>`. The template's version and hash are recorded in the `setup.plan.applied` audit row. Changing a template later does not change an existing company; a newer version is applied as a planned settings change (section 1.13).

**Making a template.** An owner exports the structure and settings of an existing company with doc 44's exporter. Data is excluded by default and needs an explicit opt-in per category.

**Import.** Choosing an import as the company source hands the dump to a doc 44 importer. Importers are modules and handle docx, md, txt, Milanote, Miro, Trello, Obsidian and similar sources.

One company per install. A template creates the install's one company; it does not create a second one.

## 1.9 Provisioning API [PLANNED]

| Route | Purpose |
|---|---|
| `POST /setup/plans` | Validates a plan (full or partial) and returns diagnostics and the plan hash. Writes nothing. |
| `POST /setup/plans/{hash}/apply` | Applies a plan whose hash matches. A repeat call with the same hash returns the stored result. |
| `GET /setup/status` | Step-by-step progress, the last Kodo result and any held approvals. |

- **Where it lives.** On the authenticated HTTP surface (doc 02), on its own rate bucket. JSON only; no client UI is served.
- **Access.** Before the first owner exists, only from loopback, and only with the one-time bootstrap code printed at the first start. This is the same pattern doc 32 uses for pairing codes. After the owner exists, only the owner role may call it, and each call is audited.
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
- **Checks:** schema version and migration state; the instance lock; PostgreSQL and pgvector health; each provider role's usability (doc 03); every secret reference resolves, with no value shown; the audit chain head is writable and the anchor target is reachable (docs 14, 15); the backup target is reachable and a restore drill is scheduled (doc 33); the status bind is inside its floor; clock sync; disk headroom; each model source is reachable (doc 05); at least one owner second factor exists; a pairing offer can be created.
- **Result.** Green, amber or red, plus the 0 to 100 score described in doc 46. Red blocks "setup complete" but does not stop the running server. Amber completes only with a recorded acknowledgement that names each amber item.
- **Shown in** the wizard, the Monitor pane of every client (doc 46), and the read-only status page (summary only, doc 23).

## 1.13 Changing setup later [PLANNED]

- Every change after setup uses the same path: a change plan, a dry run, an apply, and an audit row per changed setting.
- Re-running setup on a finished install shows a diff against the current state. It never resets values.
- Settings that need approval are held until the approval is given (doc 03). A held change is visible in the client approvals view.
- A value that is fixed after the first data (`gc.partition_count`, doc 04) is refused with an explanation. Its only path is a planned data move.
- The restore wizard (doc 33) and server migration use the same plan format, so a restored or moved install is set up by the same rules.

## 1.14 No server tour [DECIDED]

The server has no tour, no first-run screen and no onboarding page. Setup is complete when the plan is applied and Kodo reports, whichever door was used. Tours exist only in the client apps (Part 2).

---

# Part 2: Client first run

## 2.1 Automatic pairing [DECIDED automatic; PLANNED mechanics]

- **Per-company packages.** The package built for a company (doc 37) carries the company identity, the server address and a pin of the server's key hash (doc 32, trust-on-first-use pin).
- **What is automatic.** On first launch the client finds the server from the package, sends a pairing request, and receives its device credential. The person types no host, no code and no key.
- **What still needs an owner.** The device's approval is native (doc 32, section 3). The first device is approved at the server console during setup (step 6). Later devices are approved on an already-paired owner device. Approval is never automatic, because pairing mints credentials.
- **Keys.** The client holds only its device credential. It never holds a GitHub credential (doc 27, doc 14).
- **Conflict.** The brief's wording for packages ("no manual pairing") is read here as "no manual pairing steps for the person". Section Conflicts and Open questions record this.

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

---

## Conflicts and gaps

Each row names the place where the sources disagree, and the resolution this doc uses until the owner decides.

| # | Where | Conflict | Resolution used here |
|---|---|---|---|
| 1 | Doc 03 line 23 and line 146 (NUC checklist); doc 04 line 10 | Those docs assume a PostgreSQL 18 service on the host. The brief says bundled, run as a hidden supervised child process, with zero external installs. | Bundled child is the default (section 1.10). Bring-your-own is an operator option. Owner question 2. |
| 2 | Doc 32 lines 39 to 44 (native approval); brief ("automatic pairing", "no manual pairing") | Automatic pairing and native approval pull in different directions. | Discovery and key pinning are automatic. Approval stays native (section 2.1). Owner question 1. |
| 3 | Doc 04 line 67 (`db.migrate_on_fresh_install`) against doc 03 line 15 and doc 04 line 16 (C44: explicit `migrate` only) | A setting that migrates on a fresh install could read as an automatic migration at startup. | Setup runs `migrate` as an explicit step on an empty database (section 1.3, step 4). The setting is only a setup default. Startup still only checks the schema version. |
| 4 | Doc 05b line 17 (`env:<VAR>` or `file:<path>` for the FreeLLMAPI key) against doc 14 and doc 03 (every non-bootstrap secret goes through `ISecretStore`) | Two reference forms for one provider key. | Config holds `secret://` references for provider keys. `env:` and `file:` are bootstrap-only (section 1.6). Doc 05b to be updated. |
| 5 | Doc 02 HTTP surface (C15) and doc 32 line 52 ("exactly three" sign-in routes) | Provisioning routes are a new HTTP surface. | They sit outside the sign-in routes and under the authenticated HTTP surface (section 1.9). Docs 02 and 32 must list them. |
| 6 | Doc 22 line 180 (rename proposal) against the brief | The brief's name is "Nigerian Prince Protocol"; doc 22 proposes a neutral name. | This doc keeps the brief's name until the owner decides. Owner question 6. |
| 7 | README (docs 01 to 38 only) | Docs 41, 44, 46 and 48 are named by the brief but did not exist in `docs/design/` when this row was written. They are in the same PR now. | Links are kept and marked "not yet in docs/design/". Their content must not be assumed here. |
| 8 | CLAUDE.md (Apache-2.0 for Bazaar.Sdk and Bazaar.Contracts) against the brief (AGPL-3.0 across all repos, plus a module exception and a CLA for core code) | Not an onboarding question, but the module licence check at install (step 14) depends on it. | Not resolved here. Recorded for the owner to confirm, and for doc 37 and the NOTICE file. |
| 9 | Doc 03 Q2 (admin CLI in v1, default: small non-TUI `bazaar-admin`) against the brief's "interactive wizard" | The wizard's home is not fixed: console prompts or a client. | Both: console prompts for the first install (no client exists yet), and the same wizard in a paired client afterwards (section 1.1). |

## Open questions for the owner

Each question has a default, the one this doc uses.

1. **Pairing approval.** Should the first device be approved during setup at the server console (default), or should automatic approval be allowed for devices that present a valid offer on the local network? Reason for the default: pairing mints credentials, so approval stays a human act.
2. **PostgreSQL.** Bundled child process as the only default, with bring-your-own as an operator option (default), or bring-your-own as a first-class path? Reason: the brief's zero-install rule.
3. **Provisioning access.** Loopback plus a one-time bootstrap code until the owner exists (default), or a signed installer token? Reason: no network surface before the owner exists.
4. **Second factor to finish setup.** Required (default), or optional with a warning? Reason: recovery and high-risk actions depend on it.
5. **Backup target.** Finishing without a target only with a recorded acknowledgement (default), or refused? Reason: the most protective default for data loss; the operator's choice of target is kept.
6. **Scam-mail protocol name.** Keep "Nigerian Prince Protocol" (brief, default for this doc), or adopt a neutral name such as "Bait protocol" (doc 22 proposal)? Reason: the name refers to a nationality; the choice affects public naming, so counsel may need to see it.

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
