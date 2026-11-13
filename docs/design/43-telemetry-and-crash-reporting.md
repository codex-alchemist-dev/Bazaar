# Client telemetry and crash reporting

Status: PLANNED (design intent, nothing built) · DRAFT for owner review (2026-11-13) · Owner decisions of 2026-11-13 are marked DECIDED where they apply; everything else is PROPOSED or OPEN.
Depends on: 02-wire-protocol-and-sync.md (envelope, authenticated connection), 03-server-host-and-providers.md (settings registry), 04-postgres-schema-and-migrations.md (conventions, per-person keys), 05-model-gateway.md (role model policy, quotas), 14-identity-service-and-passport.md (GitHub App keys, `ISecretStore`), 15-security-taint-and-approvals.md (taint, reader sessions, audit log, quotas), 21-github-integration.md (issue writes, rate limits, bot labelling), 23-observatory-and-usage.md (OpenTelemetry telemetry kept separate), 27-client-architecture.md (logs, settings cache), 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md (client heads), 31-local-coworker-npu.md (on-device inference, later option), 32-remote-access-auth-and-privacy.md (privacy profiles, erasure), 33-backup-dr-and-export.md (backup window), 37-distribution-matrix-and-packaging.md (signed builds, symbols), 41-client-gui-customization-and-dsl.md (commands, mnemonics, settings UI)
Used by: 40-core-feature-modules-audit.md, 34-testing-strategy.md

## 1. Purpose

When a Bazaar client fails, the people who keep it running need enough evidence to fix it, and the person using it needs to know exactly what leaves their machine. This doc designs the client side of that: a deterministic capture of a failure, a redaction step that runs on the device before anything is sent, a per-company consent mode, a report pipeline that stores reports on the company's own server, and GitHub issues filed from that server with fingerprint deduplication.

The short answer:

- **Capture is deterministic.** A failure produces a fixed set of fields (error code, stack with a flagged line, recent logs, screenshot if allowed, device information, module versions). No model decides what is captured.
- **Redaction is on the device and never model-dependent.** It is a set of pure, versioned rules with no network, gateway, NPU or model reference. Tests enforce this (section 6).
- **Consent is per company, with three modes: off, ask, auto. The default is ask.** The person sees what will be sent before it is sent in ask mode.
- **Reports go to the client's own company server, and nowhere else.** The client holds no GitHub credential. The company server files issues through a GitHub App whose key sits in the Identity Service (doc 14).
- **Diagnosis is optional and labelled.** A light server-side diagnosis uses the company's own model, read-only, off or ask by default, and is always shown as "unverified diagnosis". An on-device diagnosis is a later option, off by default.
- **Bazaar's own fixes land through PRs.** A crash never edits code. It becomes an issue and an inbox item, and any fix is an ordinary PR under doc 21 and doc 15.

Scope: the client heads (Windows, Android, Linux) and the Calcinato terminal, plus the shared `Bazaar.Client.Core` library. Server-side failures belong to Kodo (the health module named in the owner decisions of 2026-11-13); this doc only says where they meet the client pipeline (section 12).

## 2. Decided inputs (not re-opened here)

- Clients report to their own company server. Per company, the setting is off, ask or auto, with **ask as the default** (owner, 2026-11-13).
- Redaction is never model-dependent (owner, 2026-11-13).
- The server files GitHub issues and deduplicates them by fingerprint. No GitHub token lives on a client (owner, 2026-11-13; CLAUDE.md "GitHub identity = GitHub Apps"; doc 14).
- Light LLM diagnosis runs server-side with the company's model, read-only, off or ask by default, always labelled "unverified diagnosis" (owner, 2026-11-13).
- Optional on-device NPU diagnosis comes later and is off by default (owner, 2026-11-13; doc 31).
- Bazaar's own fixes go through PRs (owner, 2026-11-13; CLAUDE.md "Build and test before every push"; doc 21).
- Instances are private by default. With several human members, the privacy-law compliance settings (global) apply (doc 32 §9; CLAUDE.md "Privacy").
- Configurability rule: a value left open becomes an operator setting with a safe default, audited on change. Floors and security invariants are not settings, and settings may only tighten them (DECISIONS-LOG round 13).
- Keyboard-first, mnemonics only, no Ctrl chords for app commands, nothing focused by default (doc 27, doc 28 §4, doc 41).
- Accessibility (keyboard, screen reader, i18n) from day one (owner decisions, 2026-11-13).
- Append-only audit log with hash chain (doc 15). Its extension for 2026-11-13 is listed in section 14.
- Version display is `0.MM.PP+commit` (CLAUDE.md "Versioning"). Reports carry the same string.
- Free-tier data may be used for training by some providers; setup warns (CLAUDE.md). This applies to the diagnosis model choice (section 9).

## 3. What is built

Nothing in this doc is built. At this commit `src/` has no crash capture, redaction, report ingestion, fingerprinting or issue filing code. Every item below is PLANNED.

| Item | Status |
|---|---|
| Error code registry and contract test | PLANNED |
| Client capture (stack, logs, device info, module versions, screenshot) | PLANNED |
| On-device redaction rules and test corpus | PLANNED |
| Consent UX in each head | PLANNED |
| Report wire messages and server ingestion | PLANNED |
| Fingerprinting and grouping | PLANNED |
| GitHub issue filing (system App) | PLANNED, depends on doc 14 and the owner registering the App |
| Server-side diagnosis | PLANNED, depends on doc 05 role policy |
| On-device NPU diagnosis | PLANNED, later, off by default |

## 4. Deterministic client capture

### 4.1 What a failure produces

A capture is built only from fields in the report schema (section 5). Nothing else is read. There is no sampling, no model call and no free-text summary generated on the device. The same failure on the same build with the same inputs gives the same capture.

**Error code.** Each failure that a module reports carries a stable code of the form `BZ-<AREA>-<NNNN>` (for example `BZ-GC-0042`). Codes are registered in the module's manifest and in one registry in `Bazaar.Contracts`. A contract test fails the build if a code is used but not registered, if two modules claim one code, or if a registered code is reused with a different meaning. Unhandled exceptions with no code get `code = null`, and the fingerprint then uses the message rule in section 8.

**Stack trace with the flagged line.** The stack is captured from the exception as thrown (inner exceptions, up to 4 levels, each with its own stack). Each frame carries:

- module id and module build id (the deterministic build GUID from the assembly),
- declaring type and method name, with compiler-generated wrappers left as they are in the frame and normalised later (section 8),
- IL offset and the method token,
- source line and file name where the build includes line information, otherwise `null`.

The **flagged line** is the first frame whose module is a Bazaar module (`app: true`). If none is found, the throwing frame is flagged. The report stores the index of the flagged frame. The source text of the line is never captured on the device. The server shows the line from the source at the build's tag (section 12).

Release builds (doc 37) keep line tables in a symbol bundle that the company server holds, because the server built the package. The client may therefore send only IL offset and method token, and the server maps them to lines. This keeps the report small and avoids shipping symbols to clients.

**Logs.** The client keeps an in-memory ring buffer of the most recent entries at Information and above, with structured properties. The buffer is capped by `telemetry.crash.log_lines` (default 200). Only the allow-listed property keys are copied (section 5.3). Message templates are kept; interpolated values pass through the redactor. Logs from before the buffer window are not included.

**Screenshot.** Captured only when `telemetry.crash.screenshot` allows it (section 7.3), and only of the window that crashed. Each pane declares its sensitive regions through `IRedactionRegions` (text bodies of GC messages, Vault file contents, DM text, email bodies, password and token fields, Passport views). The capture paints those regions solid before encoding. If a pane does not declare its regions, or the window contains a pane kind with no declaration, the screenshot is dropped and the report says so (`screen.masking.panes_unknown` counts them). The screenshot is never fed to a model on the device or on the server, and it is never sent to GitHub.

**Device information.** OS family and version, CPU architecture, memory bucket (not the exact amount), display scale, device class and posture (doc 41's classes: desktop, tablet, foldable, phone), locale tag. The client never sends hostname, user name, serial numbers, MAC or IP addresses, or file paths outside the install directory. The company server sees the connection's address for the transport, and that address is dropped before storage.

**Module versions.** Every loaded module id with its version and build id, plus the client version string and channel. This is what lets the server tie a crash to one build.

### 4.2 Where capture runs

Capture runs in-process. It hooks the unhandled-exception events of the runtime (`AppDomain` and unobserved task exceptions on Windows and Linux; the uncaught-exception handler on Android). Handled exceptions are captured only when the code calls the reporting API with an error code. It does no I/O on the failing thread. It copies the capture into a bounded in-memory structure and writes one file to the local report queue on a background thread at the next safe point. If the process dies before the write, the report is lost; that is accepted, and the crash-on-last-run path (section 7.4) covers what it can.

## 5. Data model and wire format
> WORKING NAME, pending owner naming decision. The schema id `bazaar.crash/1` and the fingerprint prefix `fp1` are proposals, not owner-named.


### 5.1 Report (`bazaar.crash/1`)

The report is a typed record in `Bazaar.Contracts`, serialised with source-generated `System.Text.Json` behind `IWireCodec` (CLAUDE.md "Wire format"). Field names are fixed by schema version. An illustrative shape:

```json
{
  "schema": "bazaar.crash/1",
  "report_id": "0196f3a2-...-uuidv7 generated on the device",
  "created_at": "2026-11-13T09:14:02Z",
  "install_id": "random, generated at install, not hardware-derived",
  "client": { "kind": "windows", "version": "0.1.0+a1b2c3d", "channel": "Beta", "runtime": ".NET 11" },
  "error": { "code": "BZ-GC-0042", "type": "Bazaar.Client.Core.GcViewException", "message": "<redacted:1>", "message_fingerprint_input": "..." },
  "stack": [
    { "module": "Bazaar.Client.Core", "module_build_id": "b3...", "type": "Bazaar.Client.Core.Panes.GcView", "method": "RenderPage", "il_offset": 118, "line": null, "file": null, "app": true }
  ],
  "flagged_frame": 0,
  "modules": [ { "id": "Bazaar.Client.Core", "version": "0.1.0", "build_id": "b3..." } ],
  "logs": [ { "t": "2026-11-13T09:13:58Z", "level": "Warning", "cat": "Bazaar.Client.Core.Wire", "msg": "reconnect after <redacted:token>", "props": { "attempt": 3 } } ],
  "device": { "os_family": "windows", "os_version": "10.0.26100", "arch": "x64", "ram_bucket": "16-32 GiB", "display_scale": 1.5, "device_class": "desktop", "posture": "open", "locale": "en-GB" },
  "screen": { "attachment_id": null, "masking": { "regions": 0, "panes_unknown": 0 } },
  "redaction": { "rules_version": "1.0.0", "counts": { "pattern": 1, "structural": 0, "exact": 0 }, "residual_scan": "pass" },
  "fingerprint": "fp1:9c1e...",
  "consent": { "mode": "ask", "decision": "sent", "screenshot": "none" }
}
```

The `fingerprint` the device computes is a hint. The server recomputes it and records a mismatch when the two differ (section 8).

### 5.2 Size and transport

- Report JSON is capped at 256 KiB before transport and 64 KiB for the `logs` array after redaction. Longer input is truncated from the oldest end, and `redaction.counts.truncated` says so.
- Reports go over the authenticated WebSocket (doc 02) as `crash.submit`, one request per report, acknowledged by `crash.ack` with the server's `report_id` and the group key. Idempotent: the server keys on (`install_id`, `report_id`).
- A screenshot, if any, uploads separately as `crash.attachment.begin`, `crash.attachment.chunk` and `crash.attachment.end`. The end message carries a SHA-256 that the server checks. The report refers to the attachment by id and is accepted before the attachment finishes, but the attachment is only kept if its hash matches.
- Wake-ups and push carry no report content. A pending report never causes a push (doc 29 and doc 02 wake rules).

### 5.3 Log property allow-list

Only keys listed in the schema's allow-list are copied: for example `attempt`, `count`, `duration_ms`, `status`, `module`, `error_code`. Any other key is dropped and counted. The allow-list is code, in `Bazaar.Contracts`, and a test fails when a new log call site uses an unlisted key that is not marked as non-sensitive. Property values must be scalars; objects and arrays are dropped.

### 5.4 Server storage

Postgres tables follow doc 04 conventions (UUID primary keys, `timestamptz`, migrations through the explicit `migrate` command, per-person encryption where section 11 requires it).

```sql
crash_report(
  report_id uuid primary key,              -- from the device, UUIDv7
  install_id uuid not null,
  device_id uuid not null,                 -- device record (pseudonymous)
  person_id uuid null,                     -- from the device record; null for shared devices
  received_at timestamptz not null,
  client_version text not null, channel text not null,
  error_code text null, error_type text not null,
  fingerprint text not null,               -- server-computed
  fingerprint_device text null,            -- device hint, for the mismatch count
  group_id uuid not null references crash_group,
  flagged_module text, flagged_build_id text,
  symbols_state text not null,             -- resolved | missing | partial
  schema_version text not null,
  redaction_rules_version text not null,
  consent_mode text not null, consent_decision text not null,
  source_trust text not null default 'external',   -- doc 15 enum
  body_enc bytea not null,                 -- stack, logs, device, modules: encrypted per person (or per company key on shared devices)
  attachment_id uuid null,
  size_bytes int not null,
  deleted_at timestamptz null,             -- soft delete (section 11)
  legal_hold boolean not null default false
);

crash_group(
  group_id uuid primary key,
  fingerprint text unique not null,
  error_code text null, error_type text not null, flagged_module text,
  first_seen_version text not null, last_seen_version text not null,
  occurrences bigint not null default 0,
  status text not null,                    -- open | filed | resolved_in | ignored
  resolved_in_version text null,
  github_repo text null, github_issue_number int null,
  issue_last_comment_at timestamptz null,
  created_at timestamptz not null, updated_at timestamptz not null
);

crash_attachment(
  attachment_id uuid primary key,
  report_id uuid not null references crash_report,
  kind text not null,                      -- screenshot
  mime text not null, width int not null, height int not null,
  sha256 bytea not null,
  bytes_enc bytea not null,                -- same key rule as body_enc
  mask_regions int not null,
  expires_at timestamptz not null          -- screenshot retention (section 11)
);

crash_diagnosis(
  diagnosis_id uuid primary key,
  group_id uuid not null references crash_group,
  report_id uuid null references crash_report,
  kind text not null,                      -- server | device
  label text not null default 'unverified diagnosis',  -- constant, checked by the writer
  produced_at timestamptz not null,
  role_model text not null,                -- the company's whitelisted model label, not an id in docs
  input_hash bytea not null,               -- SHA-256 of the exact redacted input
  text_out text not null,
  posted_to_issue boolean not null default false
);
```

Crash reports are stored in their own tables. They are not `gc_message` rows and not `gw_usage` rows, and they are not OpenTelemetry signals (doc 23 Content policy).

## 6. On-device redaction

### 6.1 Principle

Redaction is a pure function from a capture to a report. It runs on the device before any byte leaves it, and before any diagnosis (on-device or server-side) sees the report. It does not call the model gateway, an NPU, a remote service or the clock beyond stamping the report. The output is deterministic: the same capture and the same rule version give the same bytes.

Redaction is a **floor**. A company setting may add patterns (tighten), never remove a built-in rule or switch redaction off. If a redaction step throws, the affected field becomes `<redacted:failed>` and the report is held rather than sent.

### 6.2 Layers

Applied in this order to every string in the report:

1. **Structural drop.** The schema says which fields are allowed, and each DTO property carries a redaction class (`Safe`, `Identifier`, `Free text`, `Never`). `Never` fields do not enter the report at all: GC message bodies, Vault contents, DM text, email bodies, Passport and credential values, cookie values, environment variables, command lines and arguments, clipboard contents, and any `IRedactionRegions` text. A source generator emits the class table; an analyzer error fires on a DTO property without a class.
2. **Pattern redaction** with `RegexOptions.NonBacktracking`. Built-in patterns (versioned in `redaction.rules`):
   - GitHub token families (`ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, `github_pat_`).
   - GitHub App private key blocks (PEM) and App-style JWTs (three base64url segments beginning `eyJ`).
   - Provider API key formats (for example the `sk-` and `sk-ant-` prefix families), AWS access key IDs (`AKIA`, `ASIA`).
   - `Authorization` and `Bearer` header values, URL user info, and query values for keys named `token`, `key`, `secret`, `sig`, `signature`, `password`, `code`.
   - Email addresses, IPv4 and IPv6 addresses (loopback kept as-is so local debugging still reads).
   - Home-directory paths: `C:\Users\<name>\`, `/home/<name>/`, `/Users/<name>/` become `<home>\` or `<home>/`. The machine name is replaced wherever it appears.
   - MAC addresses.
3. **Exact-value redaction.** Values the client knows are secrets are added at runtime to an in-memory set: the device session token, the pairing secret, the local ISecretStore handle values it has ever received. Each is replaced everywhere it appears, including inside longer strings, before pattern output is produced.
4. **Length and shape limits.** Strings over 4 KiB are truncated with a count. Arrays over their cap are truncated from the oldest end.
5. **Residual scan.** The whole serialised report is scanned again with the pattern set and the exact-value set. Any match turns the enclosing field into `<redacted:residual>`, sets `redaction.residual_scan = "fail"`, and the report is held for the owner, not sent automatically (ask mode shows it as "held: contains unexpected text"; auto mode sends nothing and records a local notice).

The company setting `telemetry.crash.redaction_extra_patterns` (section 7.3) adds patterns to layer 2. Patterns are compiled with the non-backtracking engine and a timeout, and a pattern that fails to compile disables the setting and raises a notice. It cannot remove a built-in.

### 6.3 Test corpus and testing

- **Synthetic secret corpus.** Each pattern has at least 5 positive and 5 negative cases. Positive cases are generated values with the right shape and are never valid credentials. Negative cases are look-alikes that must survive: UUIDs, commit SHAs (needed for the build id), version strings, hashes in log messages, and base64 image fragments that are not JWT-shaped.
- **Canary test.** A canary value is planted in every source a capture reads: exception message, inner exception message, log message and property, a flagged file path, a home-directory path, a pane's `Never` region, the exact-value set, and the screenshot's pixels inside a sensitive region. The test serialises the report and asserts the canary is absent from the bytes and that the masked screenshot region is a solid fill (Avalonia.Headless renders the pane for the screenshot test).
- **Determinism.** The same capture gives identical bytes over 1000 random input orderings.
- **Model independence.** An architecture test asserts that the redaction assembly references no gateway, provider, NPU, HTTP or model assembly. A second test runs redaction with the network and gateway disabled and asserts identical output.
- **Adversarial input.** A 1 MiB input made of repeated near-matches must redact in under 50 ms per layer, which the non-backtracking engine must meet. Fuzzing (xUnit v3 with a seeded generator) checks that no generated secret-shaped string survives the residual scan.
- **Server re-check.** Ingestion runs the same validator (section 12.1). A test feeds a report from a modified client with a planted canary: the server refuses it, files nothing, and records a `crash.redaction_refused` audit entry.

## 7. Consent: off, ask, auto

### 7.1 The three modes

The company has one mode, stored as `telemetry.crash.mode` in the settings registry (doc 03). Its scope is the install, because one company runs per install (owner decisions, client build flow).

| Mode | What the device does | What leaves the device | Who decides |
|---|---|---|---|
| **off** | Captures nothing beyond the normal local log (doc 27). No report is built, no dialog is shown. | Nothing. | Operator |
| **ask** (default) | Builds the report, keeps it in the local queue, and shows the consent dialog (7.4). | Only the report the person sends, byte for byte what the preview showed. | Person, per report |
| **auto** | Builds, redacts, and sends without a dialog. The person sees a notice and can review the report afterwards. | The redacted report, with a screenshot only if `telemetry.crash.screenshot` is `allowed`. | Operator, once |

A person cannot raise the company's mode. In ask mode a person can decline any report. Changing the mode is an operator action, audited, and needs owner approval for `auto` (section 7.3).

### 7.2 What the person sees

The client serialises the report once, and the dialog and the sender use those same bytes. The preview decodes those bytes for reading; it does not rebuild a second copy. This is what makes "what you see is what is sent" true, and a test asserts the sent hash equals the previewed hash.

### 7.3 Settings

Scope is `install`. Who may change each is `operator (owner)` unless a row says otherwise.

| Key | Type | Default | Change needs approval? |
|---|---|---|---|
| `telemetry.crash.mode` | enum `off`, `ask`, `auto` | `ask` | Yes, owner, for `auto` |
| `telemetry.crash.screenshot` | enum `off`, `ask`, `allowed` | `off` | Yes, owner, for `allowed`; needs counsel for company-global (section 13) |
| `telemetry.crash.redaction_extra_patterns` | list of pattern entries | empty | No (tightening only) |
| `telemetry.crash.log_lines` | int, range 0 to 500 | 200 | No |
| `telemetry.crash.retention_days` | int, range 1 to 365 | 90 | Yes for values above 90 |
| `telemetry.crash.screenshot_retention_days` | int, range 1 to 90, at most the value above | 30 | Yes for values above 30 |
| `telemetry.crash.local_queue_days` | int, range 1 to 30 | 14 | No |
| `telemetry.crash.issue_repo` | string, `owner/name`, or empty | empty (filing off) | Yes, owner (public repo, section 13) |
| `telemetry.crash.issues_per_day` | int, range 0 to 200 | 20 | No |
| `telemetry.crash.diagnosis` | enum `off`, `ask`, `auto` | `ask` | Yes, owner, for `auto` |
| `telemetry.crash.diagnosis_model` | enum: whitelisted models in the diagnosis role (doc 05) | the first whitelisted lightweight model, resolved at setup | No |
| `telemetry.crash.issue_diagnosis_post` | enum `off`, `on` | `off` | Yes, owner, for `on` |
| `telemetry.crash.npu_diagnosis` | bool | `false` | Yes, owner (later, section 10) |

**Validation at save.** These combinations are refused, with a clear message: `auto` with `screenshot = ask` (no person is there to decide); `issue_repo` set with `mode = off`; `screenshot_retention_days` above `retention_days`; `npu_diagnosis = true` on a head whose coworker module is disabled (doc 31).

**Floors (not settings).** Redaction is always on. Raw logs and unredacted text never leave a device. A screenshot never includes an undeclared pane. Screenshots never go to GitHub. Diagnosis output is always labelled. The client never holds a GitHub credential. Agents cannot read or change any of these settings. Settings may tighten, never loosen, these floors.

### 7.4 Dialogs and notices, per head

Common rules: keyboard-first with mnemonic commands registered in doc 41's command registry (example names: `crsend`, `crnot`, `crlist`, `crpreview`); no Ctrl chords; nothing focused by default; the Send action is not the default button, so Enter alone does nothing; all strings come from resource files; every control has an accessible name.

- **Ask dialog.** Title "Send a crash report?". Body: the error code if any, a one-sentence plain description, and a list of what is included (stack, N log lines, device information, module versions, and the screenshot if offered). Buttons: Preview, Send, Don't send. The screenshot is a checkbox, unticked when the setting is `ask` and ticked when it is `allowed` (the person can untick it). Preview opens a text view of the exact bytes, with each redaction marked (`<redacted:pattern>`), readable by a screen reader in document order.
- **Queue.** Reports the person did not send stay in the local queue for `telemetry.crash.local_queue_days`, then are deleted. The palette command `crlist` lists them; each can be previewed, sent or dropped.
- **Auto notice.** A non-modal line in the Monitor pane (Kodo's client pane, named in the owner decisions of 2026-11-13) reads "Crash report sent (reference ...)". It takes no focus and makes no sound. The notice links to the preview of what was sent.
- **Off.** No dialog and no notice. Settings show that crash reports are off for this company.
- **Windows (doc 28).** The dialog is an in-app window. An OS notification may say a crash happened on the last run, with no content.
- **Android (doc 29).** A crash on the previous run shows the ask dialog on next launch. Sending does not need a biometric prompt; biometrics gate approvals only (doc 14, doc 15).
- **Linux (doc 30).** Same as Windows, using a desktop notification with no content.
- **Calcinato terminal.** An ex-style command list of pending reports: `:crash list`, `:crash preview N`, `:crash send N`, `:crash drop N`. Vim-style, no Ctrl chords. The local NPU suggestion stays off by default.
- **Screen reader.** The screenshot's alt text reads "Screenshot of the window that crashed, masked, N regions hidden". It does not describe content, so the description cannot leak what is hidden.

### 7.5 Crash on the last run

The capture writes its file at the last safe point (section 4.2). When the process dies before the write, the next launch looks for a `crash.sentinel` file that the client writes at start and clears on clean exit. A sentinel with no matching report gives a minimal report: error code absent, reason `unclean exit`, module versions and device information only. This report goes through the same consent mode and redaction. It is the only report kind that is built without a stack.

## 8. Fingerprint and grouping

### 8.1 Goal

Many occurrences of one bug should land in one group and one GitHub issue. Line shifts and build changes should not split a group, and a real new failure should not merge into an old one.

### 8.2 Algorithm outline (`fp1`)

```
input = "fp1" + "\n"
      + (error_code or "-") + "\n"
      + normalise_type(error.type) + "\n"            // full name, generic arity removed
      + for frame in top_app_frames(5):              // app frames; if none, the top 5 frames
            frame.module_id + "|" + frame.type + "|" + normalise_method(frame.method) + "\n"
      + (if error_code is null: normalise_message(first line of error.message) + "\n")

fingerprint = "fp1:" + lowercase_hex(SHA-256(UTF-8(NFC(input)))[0..16])
```

- `top_app_frames` keeps frames with `app = true`, in order, and stops after five. Framework and third-party frames are skipped unless no app frame exists.
- `normalise_method`: strips async state-machine wrappers (`<Foo>d__12.MoveNext` becomes `Foo`), local functions (`<Foo>g__Bar|3_0` becomes `Bar`), and lambdas (`<Foo>b__5_0` becomes `Foo.lambda`). Parameter lists and line numbers are never used.
- `normalise_message` replaces decimal numbers, hex runs of six or more digits, GUIDs, quoted strings, and paths with fixed placeholders. The message contributes only when there is no error code, so a stable code is the preferred key.
- Module build ids are not in the key. Module versions are recorded per occurrence, so a regression can be seen per version without changing the group.

### 8.3 Server side

The server recomputes the fingerprint from the report it received. A device hint that differs from the server value is counted in `crash_group` diagnostics as a mismatch, which points at a normalisation bug. The server value is the one used.

### 8.4 Issue deduplication

- Each group has at most one GitHub issue in `telemetry.crash.issue_repo`. The issue body carries a hidden marker `<!-- bazaar-crash-fp: fp1:... -->`. The server looks up the group by its own table, not by GitHub search, so no search quota is spent.
- A new group files an issue if `issues_per_day` allows, otherwise it waits in the queue and the group shows "filing deferred".
- Later occurrences add a comment only at the thresholds 1, 10, 100 and 1000 occurrences, and at most one comment per group per day. Comments carry the counts and the versions seen; they do not repeat the stack.
- If the issue was closed with a fixed version and a new occurrence arrives from a version at or above the fixed one, the group status becomes `resolved_in` and the issue is reopened with a comment that names both versions. Occurrences from older versions only increase the count.
- If the issue is deleted by hand, the next occurrence creates a new one and the group records the new issue number.
- GitHub writes count against `security.quota.github_writes_per_minute` and the per-repo daily caps in doc 21.

### 8.5 Issue body (what GitHub sees)

Title: error code and type, or type and flagged frame. Body: fingerprint marker, error code, flagged frame (module, type, method, line when symbols resolve), first and last seen versions, occurrence counts, device classes and OS families seen (aggregated, no individual device data), a short redacted message, and a bot label that names the company server as the filer (doc 21 community rule). Not in the body: logs, screenshots, device identifiers, install ids, person ids, or raw stacks beyond the flagged frame and top five frames. Each issue states that the content is automated and was redacted on the device.

## 9. Diagnosis

### 9.1 Server-side diagnosis (light)

- Input: the redacted report, or a small set of redacted reports from one group, plus the flagged source line text at the build's tag if symbols resolve. Nothing else. The input is hashed (`input_hash`) and the hash is stored with the output.
- Model: the company's whitelisted model in a dedicated `diagnosis` role under doc 05's per-role model policy. No global auto route (CLAUDE.md "No unbounded global auto"). The model is an operator choice; if the operator uses a free tier that may train on prompts, the setup wizard shows that warning again.
- Read-only, by construction: the diagnosis role is registered with **zero tools**. It cannot file or edit issues, change a group, post to a GC, write to a repo, or reach the network. A test asserts the registration has no tools.
- Taint: the input contains text that can come from remote servers (exception messages, log lines). The diagnosis session is a reader session (doc 15) and its output is text only. Output is rendered as plain text with no links and no images, which prevents exfiltration through a rendered image URL.
- Injection fixtures: a report whose message says to ignore instructions or to file an issue must not change group state, issue content or settings. A test covers this.
- Labelling: every diagnosis is stored with `label = "unverified diagnosis"`. The writer rejects any other label, and the UI cannot render diagnosis text without its label. The label is shown above the text, never only in a tooltip.
- Budget: a daily token cap per company through doc 05 and doc 15 quotas. When the cap is hit, diagnosis stops and the group shows "diagnosis paused (budget)".
- Trigger: `ask` (default) means an owner clicks Diagnose on a group in the server's group view, and the dialog shows the exact input size and the model label before it runs. `auto` runs after a group reaches the thresholds in 8.4.
- Posting: diagnosis text appears in the group view. It goes to the issue only if `issue_diagnosis_post = on`, as a separate comment that repeats the label.

### 9.2 Who reads it

Diagnosis is a hint for the owner and for the owning agent's inbox item (section 11). It is not a verdict. The group view states this next to the text.

## 10. On-device diagnosis (later, off by default)

Planned as a later step. The device runs the local text model from doc 31 on the same redacted report. The rules:

- Off by default (`telemetry.crash.npu_diagnosis = false`).
- Input is the redacted report only. The raw capture never reaches the model, and the redactor is never replaced by a model.
- Output is labelled `unverified diagnosis (on-device)` and kept on the device. It is not sent anywhere unless the person sends it as part of a report.
- If the on-device path escalates to the server model, that is a separate content-leaving event under doc 31's `coworker.escalation.mode` (default `explicit`). Automatic escalation needs counsel (doc 31).

## 11. Bazaar's own fixes, privacy and retention

### 11.1 From crash to fix

1. A group is filed as an issue in `telemetry.crash.issue_repo`, if the owner set one.
2. The owning module's agent (from the module manifest) gets an inbox item of kind `crash` (doc 01 inbox rules). Its body is the issue summary and a link to the group, not the raw report.
3. Any fix is an ordinary change: a worktree, tests written with the fix, build and test before push, a draft PR through the agent's GitHub App (doc 21), and the owner's signed approval to merge (doc 15 Tier 2). A crash report never triggers a merge and never issues instructions.
4. When the fixing version ships, the group records `resolved_in` (section 8.4). The next occurrence from that version or later reopens the issue.

Crash reports are `source_trust = external` (doc 15). Agents that read them do so in reader sessions with no credentials and no outward tools, so a report cannot be used to act.

### 11.2 Retention

| Data | Default | Notes |
|---|---|---|
| Report body (stack, logs, device, modules) | 90 days (`telemetry.crash.retention_days`) | Soft delete, then removal. Legal hold overrides. |
| Screenshot | 30 days (`screenshot_retention_days`) | Removed at expiry; hash kept in the audit entry. |
| Group summary (fingerprint, versions, counts, issue reference) | 365 days after last occurrence | No personal data in it. |
| Diagnosis text | Same as the report it describes | Deleted with the report or group. |
| Local queue on the device | 14 days | Deleted on send or expiry. |
| Backups | Follow doc 33's window (`erasure.backup_window_days`) | Retention of an offsite replica must not exceed the erasure window (doc 33). |

### 11.3 Erasure

- Report bodies and screenshots are encrypted with the person's data key when the device has an owner (doc 32, doc 04). Erasure of that person destroys the key and writes a tombstone; the group summary stays, because it has no personal data. Shared devices (no person) use a company key, which is not destroyed by one person's erasure.
- A crash report is an erasure participant (`IErasureParticipant`, doc 32 §9). Its plan lists report, attachment and diagnosis rows.
- Erasure produces a signed proof of erasure, per the 2026-11-13 decision, that names the rows and the key id destroyed, not the content.
- Legal sufficiency of key destruction is OPEN (doc 32, C01).

### 11.4 Privacy profile

- `personal` profile (one person): default settings above.
- `company-global` profile (several human members): the privacy-law compliance settings apply (doc 32 §9). Under this profile this doc proposes that screenshots stay `off` unless the owner overrides with counsel's review, and that the group view shows whose device a report came from only to the owner. This is marked for counsel (section 13).
- Data inventory: crash report fields are listed in doc 32's data inventory, with purpose, retention and erasure method. A test checks the list against the schema.
- Privacy requests (access, deletion) against crash data are answered from the same tables (doc 32 privacy requests).

### 11.5 Audit

Every event below is appended to the hash-chained audit log in doc 15 (`IAuditLog`), in the same transaction as the change where one exists:

- report received, accepted, refused (including redaction refusal), held (residual scan),
- consent decision on the device sent to the server with the report (`sent`, `not sent`, `held`), and every mode change,
- screenshot attachment accepted, rejected, expired,
- group status changes, GitHub issue created, commented, reopened, deleted by hand,
- diagnosis run, output stored, output posted,
- data reads of crash reports by humans and agents (required by the 2026-11-13 audit decision; doc 15 must be extended, section 14),
- erasure and retention deletions.

Audit rows never contain report content. They carry ids, hashes and counts.

## 12. Server pipeline and the company server

### 12.1 Ingestion, in order

1. Authenticate the connection and check that `install_id` belongs to the device (doc 02, doc 32).
2. Check size caps and the schema version. Unknown versions are refused with an error the client can show.
3. Re-run the redaction validator (section 6). Any canary or residual match refuses the report and audits the refusal.
4. Check the attachment hash and image headers (magic bytes, dimension cap, no embedded metadata beyond size).
5. Recompute the fingerprint and record the device-hint mismatch if any.
6. Symbolicate the flagged frame with the release symbol bundle for the build id. Missing symbols store `symbols_state = missing`; the report is still kept.
7. Store the report encrypted (section 11.3), upsert the group, increment counts, and audit.
8. Apply the filing rules (section 8.4) and the diagnosis trigger (section 9.1).

### 12.2 Kodo and server-side failures

Server failures are not reported through this pipeline. Kodo (the health module named in the owner decisions of 2026-11-13, not yet given a design doc) owns server incidents, health checks and the status page. The only shared piece is the redaction validator. A future decision could let Kodo post its incidents as crash groups, but that is not proposed here.

### 12.3 Licence and placement

The redaction rules are shared by the client and the server, so they live in a small library that both reference. The project name is not on the owner-approved list in CLAUDE.md, so placement is OPEN (section 13). Under the 2026-11-13 licence decision the library is AGPL. The current repo says `Bazaar.Sdk` and `Bazaar.Contracts` are Apache-2.0 (section 14), so the library must not go into `Bazaar.Contracts` until that is settled.

## 13. Open questions

Only the questions the design cannot settle without the owner or counsel.

1. **Which repository receives crash issues for Bazaar's own modules?** The public platform repo (`codex-alchemist-dev/Bazaar`) would publish redacted failure details to the world. A company repo keeps them private. Default: `telemetry.crash.issue_repo` is empty, so nothing is filed. Owner to choose.
2. **Project name for the shared redaction library.** CLAUDE.md lists approved project names and this name is not on it. Proposed `Bazaar.Diagnostics`. Owner to approve, and to settle the licence placement (section 12.3).
3. **Counsel: company-global profile.** Whether device-user data in crash reports, and screenshots, need stricter defaults when several members share an instance, and whether filing aggregated crash groups on a public repo publishes personal data. Needs counsel (doc 32).
4. **Counsel: diagnosis on a free tier.** Whether a diagnosis run through a provider that may train on prompts is acceptable under the operator's setup warning, or whether diagnosis should refuse free-tier models by default. Needs counsel and the owner's call (CLAUDE.md "Free-tier data").

## 14. Conflicts with existing docs

Flagged, not silently resolved. The 2026-11-13 owner decisions refer to the audit log as doc 48. No doc 48 exists in this repo; the audit log is specified in doc 15 (§ "Audit log and kill switch") and summarised in doc 32 §8. This doc links there. Owner to confirm, or to create doc 48.

| Where | Conflict | Proposed handling |
|---|---|---|
| doc 15, audit log section (line 89) | The 2026-11-13 decision adds reads of all data by humans and agents, Merkle proofs, signed checkpoints, external anchoring, multi-sink and an offsite replica. Doc 15 has the hash chain, export and a fixed event list. | Doc 15 must be extended. Section 11.5 lists the crash events that depend on it. |
| doc 23, Content policy (line 74) | Doc 23 says no content appears in telemetry. Crash reports carry redacted logs and optional screenshots. | This doc keeps crash reports out of OpenTelemetry, `gw_usage` and `gc_message` (section 5.4). Doc 23 should state that crash reports are a separate pipeline. |
| doc 21, Identity (line 26) | Doc 21 assumes one GitHub App per agent. Crash filing needs one system App with issue write on one repo. | Add a system App to doc 21 and to doc 14's custody list. Owner registers it. |
| doc 14 (line 16), doc 28 (line 48) | Consistent: no client holds a GitHub credential. The server files issues only through doc 14's broker. | No change. |
| doc 32 §9, data inventory | Crash data is not yet in the inventory. | Add rows (section 11.4). |
| CLAUDE.md licences (Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts`) vs the 2026-11-13 decision (AGPL everywhere) | Affects where the shared redaction library may live. | Owner to settle (section 13, item 2). NOTICE in the repo says licensing is pending counsel review. |
| CONTRIBUTING.md (DCO only) vs the 2026-11-13 CLA for core code | Not a crash-reporting conflict. Listed because the redaction library would be core code. | Owner and counsel. |

## 15. Testing summary

- Redaction: corpus, canary, determinism, model independence, adversarial input, server re-check (section 6.3).
- Capture: a planted exception in each head produces the expected fields; the flagged index points at the first Bazaar frame; capture never blocks the failing thread (timed test).
- Consent: off makes no network call (network spy); ask sends only after an explicit Send, and the sent hash equals the previewed hash; auto sends without UI; `auto` plus `screenshot = ask` is refused at save.
- Screenshot: undeclared pane drops the screenshot; declared sensitive regions are solid fills in the image (Avalonia.Headless).
- Fingerprint: golden stacks give fixed fingerprints; a line shift keeps the fingerprint; async wrapper names normalise; a message with an error code ignores the message.
- Filing: one issue per group under the deduplication marker; thresholds; the daily cap; reopen on regression from a fixed version; no issue when the repo is empty.
- Diagnosis: label enforced in the writer; zero-tool registration; injection fixtures change nothing; budget stop.
- Erasure: a plan lists report, attachment and diagnosis; key destruction leaves the group summary readable and the report unreadable.

Test layers follow doc 34: xUnit v3 for unit and contract tests, real Postgres for ingestion and group tests, Avalonia.Headless for the dialogs and screenshot masking.

## 16. Work packages (PLANNED)

Each package has its own tests and is built before it is merged.

1. Error code registry and contract test (`Bazaar.Contracts`).
2. Redaction data classes, source generator and analyzer; rules v1 and corpus (shared library, placement per section 13).
3. Client capture: exception hooks, bounded ring buffer, flagged frame, device information, module versions, sentinel file.
4. Report DTOs and the log property allow-list; wire messages `crash.submit`, `crash.ack`, `crash.attachment.*`.
5. Consent settings in the registry (doc 03), validation at save, audit of changes.
6. Consent UX in the shared library and the Windows, Android, Linux heads, and the Calcinato command (depends on doc 41 mnemonics).
7. Screenshot capture and `IRedactionRegions` in panes.
8. Server ingestion, storage, encryption, retention jobs, erasure participant (depends on doc 04, doc 32).
9. Fingerprint and grouping (server), symbol bundle lookup (depends on doc 37).
10. System GitHub App filing with deduplication (depends on doc 14 and the owner's App registration, and doc 21 amendment).
11. Server-side diagnosis role and the group view (depends on doc 05).
12. On-device NPU diagnosis (later, depends on doc 31; off by default).

Work package 2 and the fingerprint golden tests are the first to build, since the rest depends on them.
