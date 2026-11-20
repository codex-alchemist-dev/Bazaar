# 57. Athanor: the built-in IDE and editor

Status: PROPOSED (2026-11-18). Number 57 (52 to 56 taken by other docs). Implements the module split for Athanor. The name, Monaco, the WebView2 island, the key model and the theme rule are DECIDED elsewhere and only restated here with their source. Everything else here is PROPOSED and was filled by the working session where the docs were silent (CLAUDE.md, configurability rule).
Depends on: 41 §4.10, §4.12, §4.14, §4.15; 27 (client architecture, `IEditorHost`); 28 (editor host, process model, Monaco gate); 41b (client module boundaries); 40 (module layout); 49 (keyboard model).

## 1. What is DECIDED (restated, not new)

- Athanor is the built-in IDE and text editor of the Bazaar client, and runs standalone as a replacement for the system text editor (doc 41 §4.10, §4.15; FEATURES.md).
- Monaco in a WebView2 island, composition embedding as the first design. Windows only. Android never uses Monaco (doc 27, 28, CLAUDE.md). The Linux editor waits for a WebKitGTK spike after S12 (doc 27 Q9).
- One client process, many top-level windows, one shared WebView2 environment, one Monaco bundle, one controller per window, tabs swap Monaco models over a JS bridge (doc 28 process model).
- The editor is Monaco per window behind `IEditorHost`, so a native fallback can replace it (doc 28). Local-disk saves follow `windows.editor.local_write`, default `server-backed` (doc 28).
- One theme file drives Athanor's colours, fonts and syntax palette. No second palette (doc 41 §4.15, doc 27). Generator: `MonacoThemeGenerator` (exists).
- Monaco's diff editor is the single diff component, used by the Git client module (doc 41 §4.14, §4.15). No custom editing engine.
- Keys follow the client key model (doc 41 §4.12, doc 49): Vim is the reference when in doubt, no Ctrl chords. Athanor inherits Monaco's keyboard model and documents its exceptions (doc 49).
- Hosting is gated by spike S12. Nothing in this doc assumes S12 passes.
- Renderer crash: reload the bundle and reopen models from the server. Unsaved edits stay in a local draft file until the server accepts them (doc 28).
- Clearance for the name Athanor is a later task (doc 41 §4.14). Keep it out of package IDs that are published publicly until decided; the in-repo project names below are working names.

## 2. Module split (PROPOSED)

Names follow doc 41b §2a: `Bazaar.Client.<Name>` and `Bazaar.Server.<Name>`, each with an `.Abstractions` project when other modules reference it. Nothing goes in `Bazaar.Client.Core` or `Bazaar.Server` (architecture ratchet), and no module references `Bazaar.Server.csproj`.

| Module | Holds | Gate |
|---|---|---|
| `Bazaar.Client.Athanor.Abstractions` | `IEditorHost`, `IEditorFileSource`, `IEditorDraftStore`, `IEditorKeyHandoff`, document and bridge-message records, `EditorLanguage`. Contracts only. | none |
| `Bazaar.Client.Athanor` | Host-agnostic logic: workspace and tab model, bridge codec, theme push, save coordinator with revision checks, draft store, language registry, diff session, key handoff mapping, the `athanor` pane module and `IClientModule` manifest, and a plain-text fallback host. Pure .NET plus Avalonia for the pane. | none (testable headless) |
| `Bazaar.Client.Athanor.WebView2` | The Windows-only `IEditorHost` for Monaco in a WebView2 island, plus the WebView2 environment pre-warm. | S12 |
| `Bazaar.Client.Athanor.Bundle` | The Monaco bundle build, the JS bridge script, and the headless Chromium harness (test-only). | S12 |
| `Bazaar.Server.Athanor` | Server-backed document service: open, save with revision check, workspace root checks, audit rows. | needs an owner-visible wire contract (§6) |

`Bazaar.Client.GitGui` references `Bazaar.Client.Athanor.Abstractions` for the diff host.

The first PR series builds the first two rows and tests. The WebView2, bundle and server rows are listed so the names are reserved; their code waits for S12 and the wire contract.

## 3. Host contract (PROPOSED)

`IEditorHost` is the seam. A host owns one editor surface (one window) and speaks the bridge in §4.

- `OpenAsync(EditorDocument)`, `CloseAsync(documentId)`, `FocusAsync()`, `SetThemeAsync(themeJson)`, `SetReadOnlyAsync(bool)`, `ShowDiffAsync(original, modified, language)`.
- Events: `Changed` (dirty, version), `SaveRequested`, `KeyPressed` (for the Vim layer), `Ready`, `Crashed`.
- A host never touches the network. All reads and writes go through `IEditorFileSource`, which the head wires to the server connection. The client holds no repository credential (doc 28; doc 21).
- `PlainTextFallbackHost` implements the same interface over an Avalonia `TextBox` for tests, for Linux before its spike, and as the failure fallback if S12 fails. It exists so Athanor works end to end in headless tests.

## 4. Bridge protocol (PROPOSED)

JSON messages over the WebView2 message channel, source-generated `System.Text.Json` (same rule as the wire format, no reflection, no Protobuf). Two directions:

- Host to editor: `open`, `close`, `setTheme`, `setReadOnly`, `showDiff`, `applyEdit`, `focus`.
- Editor to host: `ready`, `changed`, `saveRequested`, `keyPressed`, `selection`, `error`.

Every message carries a `type`, a monotonically increasing `seq` and the `documentId` where one applies. The codec rejects unknown types, oversize payloads (default 8 MiB, configurable) and out-of-order `seq` from the editor, and never evaluates a script string built from user text. Theme payloads come from `MonacoThemeGenerator` and are pushed without a reload.

## 5. Documents, saves and drafts (PROPOSED)

- A document is identified by a `DocumentId` made of a workspace id, a path inside it and a server revision. Paths are normalised and may not escape the workspace root; the check is on both the client and the server.
- **Save:** the coordinator sends the content with the revision it opened. The server accepts only when the revision still matches; a mismatch returns a conflict, and the editor shows Monaco's diff against the server copy. No silent overwrite.
- **Drafts:** every dirty document is mirrored to a local draft file (debounced, default 1 s), keyed by workspace and path, removed only after the server accepts the save. A draft survives renderer crash and client restart and is offered back on reopen. Draft files hold only what the user typed, are stored under the client data folder, and are covered by the data-portability export as a derived item (doc 44 follows its own rules).
- **Local-disk saves:** `windows.editor.local_write` stays as doc 28 says. `server-backed` (default) keeps local folders read-only until a save is accepted. Athanor never writes a local path itself without that setting.
- **Large and binary files:** open read-only above a size limit (default 5 MiB, configurable) and refuse binary content.
- **Language:** chosen by file extension and a first-line hint from a registry data table. Language servers are Monaco's built-in services only in v1; external language servers need their own design.

## 6. Server side (PROPOSED, not built in the first series)

`Bazaar.Server.Athanor` exposes open and save for files in a workspace the server controls: the Vault repo, agent worktrees and project repos. It adds no file store of its own. It resolves a workspace id through the existing Vault (doc 18) and git (doc 21) providers and never reads outside the workspace root.

**Wire pair (PROPOSED, for doc 02 after review).** Two request and response types on the existing envelope (`k` is `req` and `res`, or `err`). Payloads are JSON records in `Bazaar.Contracts`, namespaced `Athanor`.

| Type | Request payload | Response payload |
|---|---|---|
| `editor.open` | `workspaceId`, `path` | `content`, `revision`, `languageHint?`, `readOnly`, `sizeBytes` |
| `editor.save` | `workspaceId`, `path`, `baseRevision`, `content` | `newRevision` |

Revisions are opaque server strings (for a git workspace, the blob id of the file at the head being edited). Error codes on `err`: `editor.not_found`, `editor.conflict` (carries `serverRevision` and `serverContent`), `editor.forbidden`, `editor.too_large`, `editor.binary`, `editor.read_only_workspace`, `editor.unavailable`. `editor.conflict` maps to `SaveOutcome.Conflict`, and `editor.unavailable` and transport failures map to `SaveOutcome.Unavailable`.

**Authority.** The caller is the signed-in human; the save is the human's own edit, so it needs no approval gate, but it requires the `vault.write` or `git.worktree.write` scope on the workspace (doc 48 §1 vocabulary, doc 15). A save never merges or pushes: it writes the working tree or Vault file, and the normal PR and approval path (doc 21, owner signed approval) governs anything outward. Agents never call these messages. Every accepted save writes an audit row (user, workspace, path, base and new revision, byte count).

**Limits (settings, safe defaults).** `athanor.max_open_bytes` 5 MiB, `athanor.max_save_bytes` 5 MiB, `athanor.save_rate_per_min` 60 per user. Path rules are the client's (§5) enforced again on the server, plus symlink resolution that must stay inside the root.

**Server module details (PROPOSED, first server PR, 2026-11-20).** `Bazaar.Server.Athanor` and `Bazaar.Server.Athanor.Abstractions` hold the open and save handlers, path rules, a per-user sliding-window rate limiter, an in-memory store for tests and the three settings keys. Choices where this section was silent:
- The module does not reference `Bazaar.Server`, so it holds no envelope types. `EditorOpenHandler` and `EditorSaveHandler` take the caller and the payload record and return an `EditorResult<T>` (value, or error code with message, `retryAfterMs` and conflict detail). The host adapter maps that to `res` or `err`. The payload records live in `Bazaar.Server.Athanor.Abstractions` until the pair is accepted into `Bazaar.Contracts` and `WireJsonContext`.
- Storage is `IEditorWorkspaceStore` (workspace lookup, size-capped read, atomic compare-and-set write, symlink containment); authority is `IEditorAuthorizer`, asked for `Read` on open and `Write` on save with the workspace kind (`Vault` needs `vault.write`, `GitWorktree` needs `git.worktree.write`). Open needs read access (`editor.forbidden` otherwise) and returns `readOnly: true` when the workspace is read-only or the caller lacks the write scope.
- Codes where none is named: a malformed path, empty field or path rule violation returns the envelope's `bad_request`; the save rate limit returns the envelope's `rate_limited` with `retryAfterMs`. Saving to a file that does not exist returns `editor.not_found` (the editor creates no files).
- Path rules: backslashes are separators; `.` and empty segments are dropped; `:`, control characters, a leading separator, `..`, a trailing separator, a segment ending in `.` or a space, and paths over 1024 characters are rejected.
- Check order on save: payload, path, workspace (`not_found`), read-only workspace, write scope (`forbidden`), rate limit, size (UTF-8 bytes), binary (NUL or invalid UTF-8), then the store write. Every attempt that passes the scope check counts toward the rate limit, including conflicts. Open and save limits are checked with `>`, so a file of exactly the limit is accepted.
- Settings `athanor.max_open_bytes`, `athanor.max_save_bytes` and `athanor.save_rate_per_min` are install-scope, operator-changed keys, minimum 1, contributed through `ISettingContribution`. The host supplies `IAthanorLimits` from the settings reader.
- The audit row is `kind: editor.save`, actor the user id, payload `{workspaceId, path, baseRevision, newRevision, bytes}`, appended after the write. Rejected and conflicting saves write no row. If the append throws, the error propagates to the host as `internal` and the file stays written.

Until the server module exists, `IEditorFileSource` has a read-only in-memory implementation for tests and a no-op "server unavailable" implementation for offline (doc 28: offline shows cached state only, no write queue). A server-backed `IEditorFileSource` over this pair lands as `Bazaar.Client.Athanor.Wire` after the contracts are accepted.

## 7. Key model (PROPOSED details)

Monaco gets raw key events. Athanor's `IEditorKeyHandoff` maps a key event to the client key router first: if the router claims the key (a mnemonic, the command bar, a pane switch), Monaco never sees it. Vim-style editing inside the editor is Monaco's own keymap layer configured to match the client Vim reference (doc 41 §4.12); the exact set of exceptions is documented in the module README and in doc 49 when S12 resolves focus handoff. No Ctrl chords are added.

## 8. Palette commands and navigation

Registered through the module manifest (doc 41 §8): `athanor.open`, `athanor.new`, `athanor.save`, `athanor.diff`. The syntax-first grammar (doc 41 §4.12) gets the code `ed` (editor), per-company configurable. Registration is a manifest declaration, not an edit to Client.Core.

## 9. Tests

Unit: bridge codec round trips and rejection cases; seq ordering; save coordinator revision conflicts; draft store crash recovery; path normalisation and escape rejection; language registry; theme push golden against `MonacoThemeGenerator`; key handoff mapping. `Avalonia.Headless`: the plain-text fallback host in the `athanor` pane (CLAUDE.md: every UI change gets a headless test). The WebView2 host and the bundle harness arrive with S12.

## 10. Open questions

1. Whether external language servers are in v1 (default: no).
2. Acceptance of the §6 wire pair and the exact write scope names (doc 15, doc 48).
3. Linux editor surface after S12 (doc 27 Q9).
4. Clearance of the name Athanor.

## 11. Overlap check against existing and designed modules (2026-11-18)

Read before adding the module, as the owner asked. Result: Athanor reuses or defers to each of these and duplicates none.

| Existing or designed | Relation | What Athanor does |
|---|---|---|
| `Bazaar.Client.Terminal` and Calcinato (doc 28 local terminal, S10/S11) | Sibling, same naming family, separate surface (shell versus text). | Shares nothing but the window and key model. No terminal code here. |
| `Bazaar.Client.GitGui` (doc 41 §4.14; not yet in `src/`) | Consumer. | GitGui uses Athanor's `IEditorHost.ShowDiffAsync`. Athanor adds no PR or diff list UI. |
| Coding agent harness (doc 17) | Different actor: agents edit inside VMs through server tools. | Athanor is the human's editor only. It never runs agent edit tools. |
| Vault (doc 18, `Bazaar.Vault.Abstractions`, the Vault search pane) | The Vault is Markdown in git. | Athanor opens Vault files through `IEditorFileSource`. No second Vault index or link resolver. |
| Theme (`MonacoThemeGenerator` in Client.Core) | Reused as is. | `EditorThemeProvider` only calls it. |
| Module manifest (`Bazaar.Contracts.Modules`) and `IPaneModule` | Reused. | Athanor declares a manifest and one pane kind; no new loader. |
| Client key router and `Bazaar.Ex` mnemonic navigation | Reused. | `IEditorKeyHandoff` asks the router; no second key map. |
| Server file access (doc 21 proxy, doc 16 VM files) | Not built. | `Bazaar.Server.Athanor` stays a PROPOSED contract (§6) and must reuse existing server file and approval paths, not add a parallel one. |
