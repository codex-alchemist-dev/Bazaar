# 49. Gap decisions: operations

Status: DECIDED for every owner decision recorded here (owner, 2026-11-13). Nothing in this doc is built. Implementation items are PLANNED. Sub-choices drafted here so the decision can be built are PROPOSED and the owner may override them. Items the brief does not decide are OPEN.

Scope: eight operations topics from the 2026-11-13 gap list: SCALE, ACCESSIBILITY, SUPPLY CHAIN, OFFLINE, CONDUIT LIMITS, UPDATES, MODULE API STABILITY and the EXTRACTION PLAN. The security and identity gaps (taint, Passport, audit mechanics, biometrics, secrets, recovery) are in doc 48 and are not repeated here.

Read first: 02 (wire and sync), 03 (server host and providers), 04 (schema and migrations), 27 (client architecture), 33 (backup, DR and export), 34 (testing strategy), 37 (distribution and packaging). Planned docs referenced here that are not yet in `docs/design/`: 39 (Conduit remote connectivity), 41 (client mnemonics and navigation), 42 (module API and contracts), 46 (Kodo), 48 (security and identity gaps).

Read `docs/design/DECISIONS-LOG.md` for the round history. Where this doc and an older doc disagree, this doc governs for the topics it covers once the owner accepts it. Section 9 lists every disagreement found, with the doc and line, so the older doc can be corrected in the same change that implements the decision (CLAUDE.md, "fix the doc in the same change or ask").

## 0. Vocabulary

- **DECIDED**: an owner decision. It overrides older text.
- **PLANNED**: design intent. Not built.
- **PROPOSED**: a drafted sub-choice needed to build a DECIDED item. Owner may override.
- **OPEN**: the brief does not decide it. Each OPEN item has a default that applies until decided, and where the configurability rule applies (DECISIONS-LOG round 13) the value is an operator setting with a safe default.

Settings named here use the registry format of doc 03 and doc 04: key, type, default, scope, who sets it, and whether a change is audited.

---

## 1. SCALE

**Decision (DECIDED, owner 2026-11-13).** A large company runs on a single node. The architecture must allow that node to be split into several hosts later without rewriting domain code.

**Definition.** A node is one `Bazaar.Server.Host` process group, one PostgreSQL instance, one cache instance (Valkey or Garnet), the Vault git repositories on local disk, and the VM host (doc 16). "Large" is not yet measured. Concrete sizing numbers are OPEN until the benchmarks run (spike S3 for GC partitioning, spike S8 for VM sizing; doc 04 section on scale, doc 35).

**Design consequences.**

1. **Runtime roles are modules with seams.** Each role has an interface and a transport: API and wire host (doc 02), GC writer (doc 01 and 04), agent worker (doc 06), VM host (doc 16), Vault and git service (doc 18, `IGitProvider`), model gateway (doc 05), Conduit ingress (doc 39), and Kodo (doc 46). On one node they run in one process group by default. A role moves to another host by configuration (`scale.roles.<role>.host`). Only the transport changes; the interface does not.
2. **One sequencer per database.** GC sequence numbers come from `UPDATE gc SET head_seq = head_seq + 1 ... RETURNING` under the row lock (doc 04). The first multi-node step keeps one PostgreSQL primary as the sequencer. Read replicas then serve search and the Constellation.
3. **GC placement for write scale.** Ordering is per GC, never global. A `gc_placement` table maps each GC to the one host that owns its writes. Ownership is a lease with a fencing token, using the lease pattern of doc 06. Two writers for one GC can never both win, because the fencing token is checked in the write transaction.
4. **No authoritative in-memory state.** Anything a second host needs lives in PostgreSQL or the Vault. Cache and presence (Valkey or Garnet, doc 03) are rebuildable. Rule for code review and an analyser test: no static mutable state in `Bazaar.Server` except caches marked rebuildable.
5. **Work is claimed through rows.** Background work uses `FOR UPDATE SKIP LOCKED` on the `job` table (doc 03) and turn leases (doc 06). N workers on N hosts then claim safely. There is no in-memory queue across role boundaries (PROPOSED).
6. **Vault repositories have one writer.** `IGitProvider` holds a per-repository lock on the host that holds the repository. Placement follows the same `gc_placement`-style table. Vault remotes are the sync point between hosts (doc 33 already requires a remote).
7. **Fan-out.** GC fan-out is Postgres `NOTIFY` (DECIDED, doc 03). `NOTIFY` works within one database. Cross-database fan-out is OPEN and is decided only when a second database exists.
8. **Scale profiles as settings.** `scale.profile` (values: `small`, `medium`, `large`, `custom`) sets defaults for `gc.partition_count` (doc 04, default 16), worker concurrency, VM concurrency, and cache sizes. Each profile's values come from the benchmark. The operator may override any value. Profile changes are audited.
9. **Kodo watches the node.** Kodo tracks CPU, memory, disk, IO wait, connection count, `NOTIFY` lag, and job queue age (PLANNED, doc 46). A capacity forecast warns before saturation and names the role to move first.

**Split order (PROPOSED).** Each step needs a measured trigger and an owner decision. None is automatic.

1. Database to its own host. Configuration only (connection string and bootstrap, doc 03).
2. VM host to its own host (VM backend provider, doc 16).
3. Model gateway and Conduit ingress to an edge host.
4. Workers to N hosts, claim by row (consequence 5).
5. GC placement across hosts (consequence 3).
6. Read replicas for search and the Constellation (doc 19 scale and LOD).

**Tests (PLANNED, doc 34 layers).** A load profile per scale profile. A "split test" runs the single-node suite with each role in its own process over the network transport and must produce identical GC logs. A fencing test: two writers race for one GC placement, and exactly one write survives.

**Cross-links:** 01, 03, 04 (partitioning, spike S3), 06 (leases), 16 (VM sizing, spike S8), 18 (Vault), 19 (Constellation scale), 33 (restore to scratch), 34, 35.

**Status:** DECIDED (single node, splittable later). PROPOSED (split order, placement table, fencing). OPEN (every sizing number; cross-database fan-out).

---

## 2. ACCESSIBILITY

**Decision (DECIDED, owner 2026-11-13).** Keyboard operation, screen reader support and internationalisation are required from day one. They are acceptance criteria for every feature, not a later pass.

**Supersedes:** doc 27 question 7 (screen reader as a v1 requirement is now yes), doc 19 question 3 (keyboard list mirror is now required), and the PROPOSED "Localisation" section of doc 27 (now DECIDED in this section).

### 2.1 Keyboard

- **Every function is reachable from the keyboard.** The whole client is navigable by mnemonics, not only the terminal (DECIDED; mnemonic table, examples and remapping are in doc 41). Examples from the owner: `ngc1`, `nrfdrm`, `topgc`, `unread`, `settings`.
- **Command palette.** A searchable palette lists every command. It is opened by a mnemonic, with no Ctrl chords (DECIDED). Users can remap any binding. Modules register and remap their own commands through the same registry (module API, section 7).
- **Stable command identity.** Every command has a locale-independent id. Mnemonics are per-locale data (doc 27), so a translated interface has translated mnemonics that map to the same command ids.
- **Focus.** One visible focus indicator at all times, drawn from a theme token that must meet the contrast rule. Focus order follows the layout per pane. Closing a pane or dialog returns focus to the control that opened it. A modal dialog traps focus and says so in its accessible name.
- **Calcinato (terminal).** Vim-style modes, no Ctrl chords. The mode (NORMAL, INSERT) is shown as text and announced on change (doc 27). Local-NPU suggestions are off by default (DECIDED).
- **ATHANOR (editor, Monaco).** Monaco's accessibility mode is on. The key map is the same Vim-style map as the terminal (doc 28).
- **Constellation (doc 19).** A keyboard list mirror of every visible node is required (DECIDED). It uses the same selection state as the canvas. Neighbour keys move through the graph. The list is filterable by text.
- **Touch (Android).** Every gesture has a focusable equivalent. The radial menu, whose centre is dictation (DECIDED, doc 29), has a list fallback with the same commands.
- **Test (PLANNED).** Each pane has a keyboard-only test: open, act, close, with no pointer. Avalonia.Headless drives the key sequences.

### 2.2 Screen reader

- **Platform exposure.** Windows uses UI Automation, Linux uses AT-SPI2, Android uses TalkBack. Avalonia automation peers give every control a name, role, value and state.
- **Custom surfaces** (Constellation canvas, terminal, Monaco, GC message list) implement explicit automation peers. They expose text equivalents, not only pixels.
- **Live announcements (PROPOSED).** New GC messages are announced under a user setting: none, mentions only, or all messages in the focused GC. Announcements are throttled. Opening a GC never reads the whole list.
- **Message reading order (PROPOSED).** One label per message: author, role badge, model badge, time, then the content. Cost chips and presence are read only when focused. Agent output is read as content, with no interpretation.
- **Colour.** Badges and states are text, not colour alone (doc 27).
- **Motion.** A reduced-motion token. The Constellation can switch to discrete step updates (doc 19).
- **Contrast.** Theme validation enforces WCAG 2.2 AA text contrast on theme load and rejects a theme that fails (doc 27).
- **Verification.** Avalonia.Headless can check the automation tree, names and focus order, but cannot hear output. Each release therefore needs a recorded screen reader pass on real hardware: Narrator or NVDA on Windows, Orca on Linux, TalkBack on Android (doc 27 already forbids an accessibility claim before the Narrator and TalkBack checks; Orca is added here). The pass is a release gate in the checklist, recorded in the release notes. Headless tests cannot replace it.
- **Server status page.** The read-only page (CLAUDE.md allows it) must meet the same WCAG 2.2 AA and keyboard rules.

### 2.3 Internationalisation

- **Resources, not literals.** All user-visible strings live in resource files per module. Sentences are never concatenated from fragments. Placeholders use named parameters with plural and gender rules.
- **Language packs are modules (PROPOSED).** A language pack is a module of category `locale` with its own version. It is verified and updated like any module (section 6).
- **Culture.** Dates, times, numbers, currency and durations format through the viewer's culture. Cost chips and usage figures use the same rule.
- **Time zones.** Each agent has its own timezone (doc 09). Each human sees times in the viewer's zone.
- **Direction.** Right-to-left through `FlowDirection`. Layouts mirror. Constellation and Monaco text direction follows the content.
- **Fonts.** CJK and other script fallbacks are part of the theme fallback chain (doc 27).
- **Server messages are codes.** The server sends an error code plus parameters. Clients render the text. System messages in a GC (type `system`) are stored as a code and parameters, not as rendered text. Stored text would freeze the language in an append-only log (doc 01).
- **Content is not translated.** People's and agents' messages stay as written. Each message stores its BCP 47 language tag (PROPOSED) for search, hyphenation and font choice.
- **Search (PROPOSED).** Full-text search uses a language-aware configuration per message language (doc 04 full-text search).
- **Pseudo-localisation in CI (PLANNED).** Strings are checked with expanded text (30 to 40 percent longer), a right-to-left pseudo-locale, and an analyser that flags user-visible string literals in view code.
- **OPEN.** Which languages ship in the first release. Default until decided: English only, with the full pipeline in place so that a language is a module install.

**Cross-links:** 19 (Constellation list mirror), 27 (theme validation, mnemonics per locale, WCAG target, localisation), 28 (Monaco accessibility), 29 (TalkBack, radial menu), 41 (mnemonics), 09 (timezones), 01 (system messages).

**Status:** DECIDED (keyboard, screen reader, i18n from day one; mnemonic-driven navigation; list mirror). PROPOSED (announcement policy, reading order, language tags, pseudo-localisation). OPEN (shipped languages).

---

## 3. SUPPLY CHAIN

**Decision (DECIDED, owner 2026-11-13).** Every release ships a software bill of materials (SBOM) and is signed. Reproducible builds come later (PLANNED, not a first-release gate).

**Scope.** Every artifact the project or a company build produces: the server host, client packages (MSIX, MSI, EXE, zip, APK, AAB, AppImage, Flatpak, deb, rpm), OCI images, the Kodo runtime (doc 46), each module package, each language pack, and each bundled dependency module (PostgreSQL, pgvector and runtimes tagged "Deps").

### 3.1 SBOM

- **One SBOM per artifact.** Each module also has its own SBOM, so a module update can be assessed alone.
- **Format (PROPOSED).** CycloneDX JSON, the current 1.x schema when the build runs, as the primary. SPDX JSON is generated from the same data for consumers who need it. Both are generated by CI from the build inputs. Neither is hand-written.
- **Contents.** Every NuGet package with exact version and content hash; every native binary (PostgreSQL, pgvector, runtimes); the container base image digest; the pinned .NET SDK from `global.json`; the licence of each component. The licence data feeds the install-time licence check and the marketplace (section 3.4).
- **Locks (PROPOSED).** NuGet lock files (`packages.lock.json`) are committed for every project. Central package management (CLAUDE.md) pins versions in one place.
- **Doc 30 step 10 (NuGet SBOM in CI)** becomes mandatory for every artifact, not only Linux.

### 3.2 Signing

- **Every released artifact is signed.** An unsigned artifact is never published as a release. This supersedes the "unsigned by default" rule of doc 37 (section 9, conflict S-1). Local development builds are unsigned, labelled "development build", and are refused on every release channel.
- **Two signature layers (PROPOSED).**
  - **Layer A, project release signature.** Applied by the project's release pipeline to artifacts built from the public repository. Anyone can verify it. Windows packages use an Authenticode certificate through the route in doc 28 (SignPath).
  - **Layer B, company build signature.** The company server builds and signs its own packages (the brief: "signing keys stay on the server"). The install signing key is created at setup and held in the server secret store (doc 14), never on a client. Clients trust it through pairing. Company builds are not signed with the project key, because that would let any company impersonate the project.
- **Consequence.** A company build signed by its own key does not get the OS publisher trust that a project-signed build gets. The install guide must explain the trust step. OPEN: the custody of the project release key and whether a certificate that the operating system trusts is available (doc 28 open question on certificates; counsel review).
- **Verification.** The installer and the client verify the signature and the SBOM hash before they apply an update. Kodo verifies the signature of its immutable runtime on every start (DECIDED, doc 46).

### 3.3 Provenance (PROPOSED)

- Each artifact has a build provenance attestation in the SLSA style: source commit, builder identity, build steps, and input digests. It is stored next to the SBOM.
- The update preflight verifies the attestation (section 6.6).
- Company builds produce their own attestation on the company server.

### 3.4 Vulnerability and licence checks

- **Vulnerability scan (PLANNED).** The SBOM is checked against advisory data at release time and daily afterwards. A new advisory against a shipped component raises a Kodo alert and creates a security-category update (section 6.1).
- **Licence check.** Dependencies must be AGPL-compatible (brief). CI blocks a release that has an incompatible licence. The install and the marketplace run the same check on modules (licence shown to the user, brief).
- **Verified tag.** The marketplace's automated checks are licence, malware, vulnerabilities and signature, taken from this section, followed by human review and a monitored sandbox run (brief, marketplace verified tag).

### 3.5 Reproducible builds (PLANNED, later)

Preparation now, with no gate: deterministic builds (the SDK default), `ContinuousIntegrationBuild` in CI, pinned SDK through `global.json`, pinned container base digests, sorted SBOM output, lock files, and no build-time timestamps in artifacts except in signed metadata. The target later: rebuilding a tag produces identical bytes, or a documented and reviewed diff. The owner starts the reproducibility project when the release pipeline has been stable for a set period (OPEN: the period).

### 3.6 Release flow (PROPOSED)

1. A tag triggers the release pipeline.
2. Each platform job builds, tests and writes `SHA256SUMS`, the SBOM and the NOTICE file.
3. Signing runs. A missing signing key fails the release. It does not fall back to unsigned.
4. Provenance is generated.
5. The release is created as a draft. The owner promotes it after every artifact is present and verified.
6. Package manifests (winget, Scoop, Chocolatey, Homebrew, Flatpak, Snap) are generated from the released URLs and hashes (doc 37 section 4).

**Cross-links:** 14 (signing key storage), 28 (SignPath, Authenticode), 29 (APK signing), 30 (SBOM stage, signing mode), 34 (verification gate), 37 (distribution), 42 (module signing and compatibility), 48 (module trust, verified tag review).

**Status:** DECIDED (SBOM per artifact, signed releases, reproducible builds later). PROPOSED (CycloneDX primary, two-layer signatures, attestation, release flow, lock files). OPEN (project key custody and OS trust; reproducibility start).

---

## 4. OFFLINE

**Decision (DECIDED, owner 2026-11-13).** Full offline editing with merge. A client keeps working with no connection, and its changes merge with the server when it reconnects.

**Scope of offline editing.** Offline is full for: GC messages (send, edit, react, read markers), drafts, Kanban cards (fields, moves, assignment, dependency edits), Vault documents (create, edit, link), Constellation annotations (which are Vault notes), and personal settings.

**Not offline.** Anything that needs the server's authority to execute is refused offline or queued for fresh approval, never run later on a stale approval: PR merges (need a signed approval, doc 21), owner approvals (need a fresh signature, doc 29), agent runs and VM operations, the Studio relay (doc 20), outbound email (doc 22), and company-level settings.

### 4.1 Local store (PROPOSED)

- **Store.** SQLite (public domain) per user profile on each device. The database is encrypted at rest with a data key wrapped by the operating system keystore: DPAPI on Windows, Android Keystore on Android, Secret Service or an equivalent on Linux.
- **Scope of the cache.** This replaces the cache rule of docs 02, 27 and 29, which say the cache holds no GC content (section 9, conflict O-1). The local cache holds the GC messages and Kanban cards the user can see, within a retention window (`offline.cache.days`, PROPOSED default 30) and a size cap (`offline.cache.max_mb`, PROPOSED default 2048). Cache content is encrypted.
- **Row metadata.** Each row has the server id, a client-generated UUIDv7 `local_id`, the base revision it was edited from, a dirty flag, and the outbox state.
- **Outbox.** A durable, ordered queue of commands. Each command carries its `local_id` as the idempotency key and a device sequence number. Commands are replayed in device order per stream. Across streams no order is promised.
- **Bounds.** `offline.outbox.max_items` and `offline.outbox.max_bytes` (PROPOSED defaults 10,000 items and 256 MiB). When the outbox is full the client refuses new offline writes and says why. An outbox item is never dropped silently.
- **Device clock.** Used for display and device-local order only. Server sequence numbers and server revisions are the only ordering authority (section 4.4).

### 4.2 Merge rules per data class

| Data class | Offline write | Merge rule | Conflict outcome |
|---|---|---|---|
| GC message, new | Append with `local_id` | Server assigns seq on replay (doc 02 dedupes by id). Position is the server order. | None. An append never conflicts. The message shows as pending until acknowledged, then sorts by server seq. |
| GC message, edit | New version with base version N | If N is still head, the edit becomes N+1. Otherwise it is stored as a new version flagged `edited_from_stale_base`. | Both versions are kept (brief: every version is kept with an "edited" marker). The history shows the two versions. Nothing is overwritten silently. |
| GC reaction | Add or remove, keyed by (message, user, emoji) | Operations apply in device order and are idempotent by `local_id`. | Final state is the server's last applied operation per key. |
| GC read marker | Seq value | Max-merge. The seq only moves forward. | None. |
| GC delete | Not offered | The GC log is append-only and never purged (DECIDED). | Not applicable. |
| Draft | Local | Drafts are per device. Cross-device sync is PROPOSED as LWW by revision, carried in the per-person encrypted fields (DECISIONS-LOG). | LWW for drafts only. |
| Kanban card fields (title, description, labels) | Field write with base revision | Three-way check per field: if the server value equals the base, apply. Otherwise send to the conflict inbox. | Both values are shown. Nothing is applied by timestamp. |
| Kanban status move | Command with expected card revision and target column | Re-validated against the doc 12 state machine on replay. Automatic Blocked marking (doc 12) wins over a manual move. | Applied when still legal. Otherwise rejected with the reason and placed in the conflict inbox. |
| Kanban Done on a PR card | Refused offline | Done means the PR is merged (doc 12, DECIDED), and the merge is server-side. | Not queued as a Done move. |
| Dependency edge add | Add command | Add-only set. Concurrent adds of the same edge become one edge. | Cycles are rejected on replay (doc 12 cycle rules). The rejected edge goes to the conflict inbox. |
| Dependency edge remove | Remove with base edge | Applied only if the edge is unchanged since base. | Otherwise a conflict. Add wins by default and the removal is shown. |
| Vault document | Local git commits on the device branch (section 4.2.1) | Git three-way merge per file. YAML front matter is merged key by key first. Wiki-links are rechecked after merge. | Clean merge lands on main through `VaultWriter`. A conflict lands nowhere on main (section 4.2.1). |
| Constellation layout | Not stored | Layout is derived (doc 19). | Not applicable. Annotations follow the Vault row above. |
| Personal settings | Key write with base revision | Per-key three-way check. Only keys declared personal are writable offline. | Conflicts go to the inbox. |
| Company settings | Refused offline | Owner-only and audited (doc 04 settings, DECISIONS-LOG round 13). | Not queued. |
| Approvals, merges, VM and engine actions, outbound email | Refused or queued as a request | A request is shown to the approver on reconnect with a fresh approval. The approval binds the exact payload hash (doc 15 mechanism). | Changed payload invalidates the request (doc 29). Nothing is approved by inference. |
| Owner-class writes | Queued for owner only | Idempotent, with expiry `offline.owner_action.expiry_hours` (PROPOSED 24). | After expiry the item moves to "needs review" and the owner is asked again. |
| Reputation and scores | Never written by clients | Clients never write scores (doc 11). | Not applicable. |
| Telemetry and crash reports | Queued | Sent on reconnect, subject to the telemetry setting (doc 46 and the crash design). | None. |

#### 4.2.1 Vault merge (PROPOSED detail)

- Each device works on a local clone of the Vault repositories it can see (doc 18), on its own branch `dev/<device-id>`. Offline work is ordinary git commits.
- On reconnect the device fetches and rebases its branch onto the server main. If the rebase is unclean it merges instead. The server then lands the result on main through `VaultWriter`, which keeps the single write path that agent memory already uses (doc 18).
- **Clean merge.** Lands on main. Wiki-links are rechecked. An unresolved link becomes a ghost node (doc 19), not an error.
- **Conflict.** The conflicting file does not land on main. The server records the conflict in the inbox with the base, the server version and the device version. The user resolves it in the editor with a three-way view (doc 28). The resolved file lands through the same path.
- **Rule.** Conflict markers never reach main.
- **Restores.** A Vault restore goes to the remote head, never to an older commit (doc 33).

### 4.3 Reconnect protocol (PROPOSED)

1. The client authenticates (doc 32, doc 48). The server returns the head of each stream: GC sequence numbers, Vault head, Kanban revisions, settings revision.
2. The client sends the outbox in device order with idempotency keys. The server answers each item as accepted, merged, conflict or rejected, with the reason.
3. The client catches up on each stream from its last known seq (doc 02, `gc:<id>` resume).
4. The Vault sync of section 4.2.1 runs.
5. The conflict inbox fills. The status chip shows the state: offline (cache age), syncing, or N conflicts (doc 27 connection chip).

A reply lost on the wire is recovered by replaying the same `local_id`. The server returns the stored original result for the idempotency window (PROPOSED: 7 days, setting `offline.idempotency_days`).

### 4.4 Ordering and clocks

Server sequence numbers (GC) and server revisions (everything else) are the only ordering authority. Device clocks never decide an order. Clock drift is a Kodo signal (doc 46).

### 4.5 Conflict inbox

One inbox per person, keyboard-first, with the mnemonic `conflicts` (PROPOSED, doc 41). Each entry shows both versions, the base, and the actions: keep mine, keep theirs, merge in the editor, or defer. Each action is audited.

### 4.6 Long offline periods and server restores

- A long offline period never discards the outbox. After `offline.max_days` (PROPOSED 14) without sync, the client asks the person to review the outbox. Items that expire move to "needs review".
- If the server is restored to an earlier point (doc 33), a client may have seen sequence numbers that no longer exist. The server answers with a resync instruction. The client keeps its outbox and reconciles it against the new head: items already present are recognised by `local_id`, and missing items are resubmitted. Doc 33's rule for an unrecoverable range (a `system` entry in each affected GC) applies unchanged.

### 4.7 Security notes

The local store is encrypted. It is wiped on device revocation (doc 32). Erasure tombstones (doc 33, crypto-erase) reach offline devices on their next sync, and a device that stays offline past the outbox window is treated as unverified. Full identity and trust rules are in doc 48.

### 4.8 Tests (PLANNED)

- Convergence property tests: any replay order of the same operation set gives the same state for set and LWW data, and replaying twice changes nothing.
- The offline replay test from doc 02 (10,000 messages offline, reconnect, exact set and order, no duplicates, socket killed mid-page).
- Concurrent edit: two devices edit one message; both versions exist and the stale one is flagged.
- Kanban: a move re-validated after the card changed, plus Blocked marking winning over a manual move.
- Vault: clean merge, conflict, front matter key merge, and a broken wiki-link after merge.
- Restore: a client with outbox items reconciles after a server restore to an earlier point.

**Cross-links:** 02 (replay, dedupe, resume), 04 (sequence numbers), 11 (clients never write scores), 12 (Kanban rules), 15 (approval payload hash), 18 (Vault), 27 (cache and cold-start shell), 28 (editor offline rule superseded), 29 (phone offline), 32 (device revoke), 33 (restore and crypto-erase), 48 (identity).

**Status:** DECIDED (full offline editing with merge). PROPOSED (SQLite store, merge table, Vault branch model, reconnect protocol, limits). OPEN (none in this section beyond the defaults above).

---

## 5. CONDUIT LIMITS

**Decision (DECIDED, owner 2026-11-13).** Conduit has built-in rate limits and authentication lockout. They are on by default. Settings may tighten them, never loosen them past the ceilings and floors in section 5.4.

Conduit is the remote connectivity module (`Bazaar.Conduit`: Cloudflared, P2P and LAN paths; planned doc 39). The limits apply to every ingress path Conduit exposes. A home host with no inbound connections (DECIDED, doc 03 and doc 21) has nothing to limit on that path, and the limits apply the moment an ingress is enabled.

### 5.1 Rate limits

- **Layers.** Per source address (before authentication), per identity (after authentication), per route class (auth, sync, upload, search, admin, webhook), and per connection (WebSocket frames and bytes).
- **Algorithm (PROPOSED).** Token bucket per key, with a burst and a refill rate. Checks run in middleware before body parsing. Size caps run first.
- **Responses.** HTTP 429 with `Retry-After`. WebSocket close code 4429 (doc 02). Clients honour `Retry-After` and show a "waiting" state on the connection chip.
- **Trusted proxies.** The source address comes from a configured trusted proxy only (for example a Cloudflare Tunnel, doc 32). Forwarded headers from any other peer are ignored. Setting `conduit.trusted_proxies`, default none.
- **Webhooks.** Webhook signatures are verified before any other work (doc 21). Unsigned requests cost almost nothing and are rejected early.
- **Not agent pacing.** These limits protect the server from clients and the internet. Agent pacing is the Work-Life Simulation (doc 09) and the quotas in doc 15. The two systems are never merged.

### 5.2 Authentication lockout

- **Counting.** Failed authentication attempts are counted per account and per source address.
- **Escalation.** A progressive delay after the first failures. Then a temporary lockout. A repeat lockout is longer, with exponential growth up to a cap.
- **Account lockout scope (PROPOSED).** An account lockout blocks password, TOTP and passkey sign-in for that account from every source for the lockout window. A source block affects only that source. Account lockout can be used to deny service to an account, so the owner's recovery path and any hardware-key path are exempt. The exemption rules belong to doc 48.
- **Events.** Each lockout is written to the audit log (doc 48 owns the audit mechanics), raises a Kodo alert, and notifies the owner's approval channel (doc 48).

### 5.3 Other ingress limits

- Maximum request body, header count and header size. Upload routes have their own cap (setting per route class).
- WebSocket frame size, and the outbound queue depth (doc 02, default 1,000 events, then `lagged`).
- Concurrent connections per source and per identity (per device).
- Header and body read timeouts against slow clients. The hello timeout 4408 applies (doc 02).
- **Admission under pressure (PLANNED).** When Kodo reports red health, Conduit sheds the lowest priority routes first: search and uploads. Webhook processing is deferred and acknowledged, not dropped.

### 5.4 Defaults, ceilings and floors (PROPOSED values, measured later)

A ceiling is the highest value an operator may set for a rate. A floor is the lowest value an operator may set for a protection threshold. Settings may tighten in both directions. They may not loosen past these.

| Setting | Default | Ceiling or floor | Notes |
|---|---|---|---|
| `conduit.rate.unauth.per_source_per_min` | 60 | ceiling 300 | Requests before authentication, per source |
| `conduit.rate.auth_attempts.per_source_per_10min` | 20 | ceiling 50 | Authentication attempts, per source |
| `conduit.lockout.failures_per_account` | 5 | floor 3, ceiling 10 | Failures before account lockout |
| `conduit.lockout.first_minutes` | 15 | floor 1 | First account lockout window |
| `conduit.lockout.max_hours` | 24 | ceiling 24 | Cap on the exponential repeat window |
| `conduit.rate.identity.per_min` | 600 | ceiling 3,000 | Authenticated requests, per identity |
| `conduit.ws.frames_per_sec` | 200 | ceiling 2,000 | Per connection |
| `conduit.ws.sockets_per_identity` | 8 | ceiling 32 | Concurrent sockets per identity |
| `conduit.trusted_proxies` | none | not loosenable by default | Trusted address list |

### 5.5 Observability and tests

- Counters per route and per reason go to Kodo (PLANNED). Top offending sources are listed.
- Tests (PLANNED): token bucket with a fake clock; escalation and cap; a spoofed `X-Forwarded-For` is ignored unless the peer is trusted; 429 and close 4429 behaviour; the recovery path works while an account is locked (cross-check with doc 48).

**Cross-links:** 02 (close codes, outbound queue, hello timeout), 09 (pacing, not rate limits), 14 and 48 (sign-in, TOTP tiers, recovery exemption), 15 (quotas), 21 (webhook signatures), 32 (remote access options), 39 (Conduit), 46 (alerts).

**Status:** DECIDED (built-in rate limits and auth lockout, on by default, settings within ceilings and floors). PROPOSED (token bucket, defaults, account lockout scope). OPEN (none in this section; values follow the load tests).

---

## 6. UPDATES

**Decision (DECIDED, owner 2026-11-13).** The default is automatic for security updates and manual for everything else. Policies are set per module, category, version type and channel, with conditions and maintenance windows. Every apply takes a snapshot and rolls back automatically on failure. Rollouts are canary and staged. Preflight and a migration dry-run run before any change. Operators can pin, defer and skip. Approvals are by role, can be delegated, and are expressed as policy-as-code. A fleet dashboard shows the state, and every decision is audited.

**Scope.** The server host; the Kodo runtime; every module; dependency modules (PostgreSQL 18 and PostgreSQL 19 are two versions of one module, pgvector, and runtimes, brief "Deps as modules"); client packages, updated in place; language packs; the model and source catalogue data (doc 05); and VM golden images, which follow the parent and child rules of doc 16.

### 6.1 Vocabulary

- **Category.** `security` (set by an advisory match or the security feed), `bugfix`, `feature`, `deprecation`, `breaking` (major version), `dependency` (dependency modules), `catalogue`, `locale`, `image` (VM golden images).
- **Version type.** `patch`, `minor`, `major`, `prerelease` (beta or alpha builds).
- **Channel.** LTSC, Stable, Beta, Alpha, Latest-Commit (`VERSIONING.md`, doc 37).
- **Target.** Global, category, version type, channel, module, or module plus channel.

### 6.2 Policy model

- **Rule.** A rule has a match on its target, an action (`auto`, `manual`, `defer`, `pin`, `skip`), conditions, a maintenance window, an approval requirement, a canary plan, and a health gate.
- **Precedence (PROPOSED).** Pin, then skip, then an explicit module rule, then a module and category rule, then a category rule, then a version type rule, then the channel default, then the global default. Where two rules are equally specific, the stricter action wins: pin over auto, manual over auto. Evaluation never depends on file order.
- **Shipped defaults.** `security` is `auto`, applied after canary and preflight, in the next maintenance window. Every other category is `manual`. Prerelease channels are `manual`. The LTSC channel is `auto` for security only. `breaking` can never be `auto` (a floor). `feature` can never be `auto` by default, and an operator may enable it only with an approval rule.
- **Conditions.** Kodo health is green or amber (operator choice). The backup is younger than a set age. Disk headroom is sufficient. No incident is open in incident mode (doc 46 freezes risky changes). The apply does not interrupt running agent turns without a drain.
- **Maintenance windows.** Named windows with a recurrence and a timezone. Several windows are allowed. An emergency override needs a role approval and is audited. Auto actions run only inside a window. A manual approval may run outside a window only when the approver says "now".
- **Drain.** Agents are paced by shifts and curfews (doc 09). An update drains active agent turns and pauses new turns during the apply window (PROPOSED). Work resumes from the lease state after the window (doc 06).

### 6.3 Lifecycle of one update

`discovered`, then `evaluated` (rule and policy hash recorded), then `pending` (waiting for a decision, a window, or a canary soak), then `preflight`, then `snapshot`, then `applying` (canary ring first), then `health-gate`, then `promoted` (remaining rings), then `done`. A failure goes to `rolling-back` and then `rolled-back`. A policy outcome can also set `deferred`, `skipped` or `pinned`. Every transition is an audit event.

### 6.4 Snapshot and automatic rollback

- **Snapshot before every apply (PROPOSED).** It records the module versions and the active pointer, the configuration and policy versions, the VM image references (doc 16, parent kept while children exist), and for stateful modules a database marker taken with a consistent base backup and WAL position (doc 33).
- **Versioned installs and atomic switch.** Each module and runtime is installed in its own immutable versioned directory. A single active pointer switches atomically (the Kodo A/B rule, DECIDED, doc 46, applied to every module). The previous version stays installed until the new version passes its health gate and the retention window ends. Default retention: the last two versions per module.
- **Automatic rollback triggers.** A failed apply step. A failed migration. A health gate failure inside the gate window (PROPOSED default 15 minutes). An error rate above the Kodo rule for the new version.
- **Database rollback for updates (resolves doc 33 rollback policy for updates only).** Migrations are forward-only (doc 04). A schema rollback is a restore to the pre-update marker (doc 33). A restore discards GC entries after the marker (doc 33 rollback policy), so the apply runs under a short write freeze: GC writes pause for the apply window, which means nothing after the marker exists to lose. If the freeze must end before the health gate passes, rollback becomes a roll-forward with a fix release, and the owner is asked. A restore after accepted writes follows doc 33's sequence procedure. The general restore policy stays with doc 33.
- **Atomic test (PLANNED).** The pointer switch is tested by killing the process at each step and checking that the running version is always one complete version.

### 6.5 Canary and staged rollout

- **Rings (PROPOSED).** Ring 0 is the owner's own clients and the canary agents (`vm.rebase_canary_agents`, doc 16). Ring 1 is a configured subset of clients. Ring 2 is all clients. Agent VM images rebase per agent after the canary agents (doc 16).
- **Server modules on one node.** There is no server-side ring on a single node. Server updates are protected by preflight, the health gate, and instant rollback. Server rings become real when multi-node placement exists (section 1).
- **Project channels.** Staging across installs is done by the release channels of the project (Alpha, Beta, Stable, LTSC), which is the operator's choice (doc 37).
- **Soak (PROPOSED).** Each ring has a soak time: 4 hours for security, 24 hours for everything else. Promotion needs the soak complete, the error budget not burned (doc 46 SLOs), and no crash spike in the crash data (doc 46).
- **Halt.** Any role with the right can halt a rollout. A halt is audited and freezes promotion for that update until it is resumed by a role.

### 6.6 Preflight and migration dry-run

- **Preflight (PLANNED list).** Signature and SBOM verification and attestation (section 3). Compatibility matrix (section 7). Dependency resolution, with PostgreSQL 18 and 19 both checked. Licence check. Disk and memory headroom. A fresh backup, taken before the snapshot. Configuration validation with dry-run (onboarding, brief). The module conformance suite against the installed host (section 7.4). Kodo readiness.
- **Migration dry-run.** The latest backup is restored to a scratch instance (doc 33, restore rule 1: never restore in place). The new migrations run there, and the dry-run records duration, locks taken, table rewrite size, and the schema diff. The module self-test runs. The report is attached to the approval card. The dry-run never touches the live instance. Disk for the scratch restore is part of preflight.
- A failed dry-run blocks the update. The report says why.

### 6.7 Pin, defer and skip

- **Pin.** Holds a module at one version. A pin requires a reason and an expiry date. A pin past its expiry prompts for a decision. A security advisory against a pinned version raises an alert that needs a decision: unpin, update, or accept the risk with a recorded reason.
- **Defer.** Postpones a pending update by a number of days (default 7, ceiling 30) or until a date. The reason is shown on the card. Deferral of a `security` update is capped at the ceiling and then needs a risk acceptance.
- **Skip.** Skips one specific version. The skip ends when a newer version appears in the same category. Skipping a security version needs a recorded risk acceptance.

### 6.8 Role approvals, delegation and policy-as-code

- **Approvals.** A manual rule names the roles that may approve (custom roles, brief) and the count required (default 1; 2 for `breaking`). The requester cannot approve their own request. The owner may waive this in policy, and the waiver is audited.
- **Owner-only.** Changes to the update policy itself and to the Kodo immutability settings are owner-only and cannot be delegated.
- **Delegation.** The owner delegates approval authority to a role or a person for categories and version types, with an expiry. A delegation is itself a policy object. A delegated approval records "approved by X under delegation D from the owner". A delegation cannot exceed the delegator's rights. Delegations cannot be re-delegated by default. A delegation can be revoked at once.
- **Policy-as-code (PROPOSED).** Policies live in the company's headless configuration (onboarding, brief), in the format that file uses (PROPOSED: YAML with a JSON Schema). The file is validated by schema and linted. A `policy test` command simulates candidate updates and prints the decision each would get. A change to the policy goes through the same approval path and is audited with its diff. Every decision records the hash of the policy version that produced it.

```yaml
# PROPOSED shape, illustrative only
updates:
  defaults:
    security: { action: auto, window: nightly, rings: [owner-devices], soak: 4h }
    feature:  { action: manual, approvals: { roles: [release-lead], count: 1 } }
  modules:
    bazaar.postgres:
      stable: { action: pin, version: "18.*", reason: "waiting for pgvector build", expires: 2026-12-31 }
  windows:
    nightly: { cron: "0 2 * * *", timezone: "operator-zone", max: 2h }
```

### 6.9 Fleet dashboard

- **Per company (DECIDED).** Shows, per module and per client: installed and available versions, channel, pending decisions, deferrals, pins with age, skips, rollouts by ring, failed and rolled-back updates, the policy version, the last successful backup, and health. It lives in the Kodo monitor pane, which every client has (DECIDED, doc 46), and in an Updates pane. Role-gated controls: approve, defer, pin, halt, roll back.
- **Across companies (OPEN).** One operator running several installs. The default until decided: each install is its own fleet, and there is no cross-install view.

### 6.10 Audit of decisions

Every decision record holds: update id, module, from and to versions, category, version type, channel, the matched rule id and the policy hash, the decision (`auto`, `approve`, `defer`, `skip`, `pin`, `halt`), the actor (a person, a role, a delegation, or a system rule), the evidence (preflight report id, dry-run id, health gate results), timestamps, and the outcome. Automatic decisions are recorded in the same form. The records are audit events, so they follow the audit mechanics of doc 48 and appear in the Kodo history view (doc 46, history time-travel).

### 6.11 Clients

Clients update in place from the server's signed package feed (brief). Packaging mechanics stay in docs 28, 29, 30 and 37. This section sets the policy. A client below the server's minimum protocol version receives close code 4426 (doc 02) and is told to update.

### 6.12 Tests (PLANNED)

- Precedence table tests: every rule combination, with the stricter action winning.
- Simulated fleet runs with fault injection: apply failure, migration failure, health gate failure. Each must roll back to the snapshot and produce an audit trail.
- The write freeze: GC writes attempted during the apply window are refused or queued, and a rollback loses no accepted write.
- Scratch migration dry-run fixture on the real PostgreSQL 18 and 19 (doc 04 test rule).
- Pointer switch atomicity (section 6.4).

**Cross-links:** 02 (4426, versions), 04 (forward-only migrations), 05 (catalogue updates), 06 (leases and drain), 09 (shifts and curfews), 14 and 48 (approval and audit mechanics), 15 (approval payload), 16 (golden parents, canary rebase), 21 (merges), 30 (SBOM and channels), 33 (restore, rollback policy, scratch restore), 37 (channels and packaging), 42 (compatibility matrix), 46 (Kodo health gates, incident mode, fleet view, history).

**Status:** DECIDED (defaults, policy dimensions, snapshot and rollback, canary and staged rollout, preflight and dry-run, pin, defer, skip, role approvals, delegation, policy-as-code, fleet dashboard, audit). PROPOSED (precedence, rings and soak, freeze, drain, file format, record fields). OPEN (cross-company fleet view; the policy file format pending the onboarding doc).

---

## 7. MODULE API STABILITY

**Decision (DECIDED, owner 2026-11-13).** A semantic versioning contract, compatibility shims, contract tests, and a compatibility matrix shown in the module manager.

### 7.1 The public surface (PROPOSED)

The module API is the set of public types in `Bazaar.Sdk` and `Bazaar.Contracts`, plus three things: the module manifest schema, the mandatory Kodo module health contract (liveness, readiness, dependencies, self-test; DECIDED, doc 46), and the extension points (`IModule`, pane registration, command and mnemonic registration, settings registration). Everything else is internal.

The surface is frozen by the Roslyn public API analyser: each public assembly keeps `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. A change to the public surface shows up in the pull request diff. Warnings are errors (CLAUDE.md), so an unshipped change to the surface fails the build until it is shipped in a version.

OPEN: the licence of `Bazaar.Sdk` and `Bazaar.Contracts` (Apache-2.0 in CLAUDE.md, AGPL-3.0 in the 2026-11-13 brief). Section 9 lists this conflict. The API rules here do not depend on the answer.

### 7.2 Semantic versioning

- **Separate version.** `Bazaar.Sdk` and `Bazaar.Contracts` each carry a SemVer version. A module declares its required range in its manifest, for example `Bazaar.Sdk >= 1.2.0 < 2.0.0`. A module's own version is separate from both (brief: modules carry their own versions in metadata).
- **MAJOR** for removing or changing a public type or member, changing the meaning of a wire field, or changing a manifest key.
- **MINOR** for additive changes: new optional members, new wire fields with defaults, new extension points.
- **PATCH** for fixes with no surface change.
- **Wire tolerance.** Readers ignore unknown JSON fields. Required fields are never added in a MINOR. Enum readers treat unknown values as `unknown` and never throw (System.Text.Json source generation, behind `IWireCodec`, DECIDED). This is what lets an older client and a newer server, or the reverse, keep working inside the protocol version rules of doc 02.
- **Pre-1.0 (Beta, 0.x).** Each surface declares a stability tier. Stable follows the rules above. Beta (0.x) may break in a MINOR only with a shim, or after a deprecation notice of at least one MINOR release, and always with a changelog entry. Experimental makes no promise and is marked in the API. Moving the core API to Stable is part of the 1.0 release (Zuhal, v1).
- **Deprecation.** A deprecated member carries `[Obsolete]` naming the replacement and the removal version. Warnings are errors in this repository, so first-party modules move first, in the same change.

### 7.3 Compatibility shims

- For each breaking change the host ships a shim that adapts the old contract to the new one. Shims are named `<Old>Shim`. A shim is registered only when a loaded module declares the old range.
- A shim is removed only at the next MAJOR. It is never removed in a MINOR or a PATCH.
- Wire shims: the server accepts the previous major's message shapes for one major, with protocol version negotiation (doc 02). A client too old for the shim window gets 4426.
- Each shim is tested by the contract tests of the old version (section 7.4).

### 7.4 Contract tests

- **Conformance suite (`Bazaar.Sdk.Testing`, PLANNED).** Every module runs it in its own CI and at install preflight (section 6.6). It covers: lifecycle (start, stop, restart, idempotent start, crash-only restart), the health contract (liveness, readiness, dependencies, self-test), settings validation, registration of panes, commands and mnemonics without collisions, unknown-enum tolerance, wire round-trips against recorded fixtures, the module's own migrations (forward-only with checksums, doc 04), and permission declarations (doc 48 trust model).
- **Old modules against the new host.** The host runs the suite for every shipped module version against the current host. A breaking host change that fails an old module's suite needs a shim or a MAJOR version.
- **Golden fixtures.** Each release of `Bazaar.Contracts` keeps its JSON examples. A test asserts that the current codec still reads every one of them.
- **Versioned with the API.** The suite is a package with the same SemVer line as the API it tests.

### 7.5 Compatibility matrix in the module manager

- **Manifest declarations.** Module version; `Bazaar.Sdk` range; host range; required Deps ranges, listing PostgreSQL 18 and 19 both where the module supports both; client range if it has a pane; schema ranges for its own tables; platform support (Windows, Linux, Android, headless).
- **Display.** The module manager shows the matrix for installed modules against host, Deps and client versions. Each cell is green (conformance passed), amber (passed with a shim in use), or red (incompatible; the update is blocked and the reason is shown).
- **CI.** Each module version is tested against every host and Deps cell in the supported set. Dual Deps are both cells (brief: always support the newest and the stable Deps). Day-0 support for betas means beta Deps are tested when they appear (brief).
- **Gate.** Updates use the matrix as a preflight gate (section 6.6). The marketplace requires a green conformance result on the current matrix for a verified tag. A later failing version revokes the tag automatically (brief).

### 7.6 Deprecation windows (PROPOSED)

Beta: at least one MINOR notice before removal. Stable: removal only at a MAJOR, with the shim alive for the whole major. The client and the fleet dashboard (section 6.9) show deprecated modules and their removal version.

### 7.7 Tests (PLANNED)

Conformance suite self-tests; public API snapshot tests (the analyser files are the snapshot); one test per shim; golden fixture round-trips; the old-against-new matrix in CI (section 7.4).

**Cross-links:** 02 (protocol version, close codes, enum handling), 03 (explicit module list, Native AOT-friendly composition, no assembly scanning), 04 (module migrations), 27, 28 and 29 (panes), 34 (test layers), 37 (channels and packaging), 42 (contract doc, planned), 46 (health contract), 48 (permissions and trust).

**Status:** DECIDED (SemVer contract, shims, contract tests, compatibility matrix). PROPOSED (surface freeze by analyser, wire tolerance rules, tiers, deprecation windows, matrix fields). OPEN (licence of Sdk and Contracts, section 9 conflict L-1).

---

## 8. EXTRACTION PLAN

**Decision (DECIDED, owner 2026-11-13).** `Kodo` (`Bazaar.Kodo`) and `Constellation.Renderer` stay normal first-party modules in this repository. Nothing in this section starts until the owner asks. When the owner asks, the steps below run in order.

**Status: DECIDED for the direction and the steps. NOT TRIGGERED.**

**Trigger.** The owner's explicit request, recorded as a decision in `DECISIONS-LOG.md`. A release, a date, or the module reaching a size does not trigger it.

**Steps.**

1. **Scope the surface.** List every reference from the module to a `Bazaar.*` type. The target is references only to `Bazaar.Sdk` and `Bazaar.Contracts` (section 7). Any other reference is a defect and is fixed by moving the type into the API or into the module. The check runs in CI (PLANNED).
2. **Bazaar-free core with an adapter.** Split the module into a core, with no `Bazaar` references (logic, models, probes, storage abstractions, renderer), and an adapter, the only project that references `Bazaar.Sdk`. The adapter implements `IModule` and registers panes and commands. Core tests run without Bazaar.
3. **Split NuGet packages.** Core, probes, storage, API, and the UI pane (for Kodo). For `Constellation.Renderer`: core and the Avalonia control. Each package ships a README, its documentation, a sample, and tests. Packages are built deterministically, with SourceLink and an SBOM, and are signed (section 3). Package names are OPEN for the owner.
4. **Its own repository.** A new repository with its own licence file, CI, SBOM, signed releases, CHANGELOG, SemVer, and channels (doc 37 model). The licence follows the decision that applies to the core at the time (brief: AGPL with the module exception, and a CLA for core code, which applies to extracted repositories too). The owner confirms the licence of the extracted repository when asked. The brief's licensing conflict (section 9, L-1) is settled first.
5. **History.** Preserve history by splitting the directory into a new branch (for example `git subtree split`). Do not rewrite the source repository's shared history. CLAUDE.md requires the owner's approval for any rewrite of shared history.
6. **Bazaar pins the version.** Bazaar references the extracted packages through central package management like any dependency. The in-tree project is removed only after Bazaar's CI passes against the pinned package. The compatibility matrix (section 7.5) records the host range and the module version.
7. **Parallel period.** For one MINOR release both copies exist. The in-tree copy is marked obsolete, and CI runs the same tests against both. Then the in-tree copy is removed.
8. **Versions.** The extracted repository's version lives in its package and manifest. The host version (`VersionPrefix`, `docs/VERSIONING.md`) is unrelated. Modules carry their own versions in metadata (brief), and the matrix links the two.
9. **Operations.** The extracted repository runs its own release pipeline. Kodo monitors its health contract. Its SBOM is a dependency entry in Bazaar's SBOM.
10. **Decisions needed when triggered.** Repository name (OPEN). Licence of the extracted repository (section 4 above). CLA scope (brief). First version of the extracted package (PROPOSED: continue from the current module version, with no reset). Whether the sandbox tiers (brief, dev sandboxes) need the package (OPEN).

**Cross-links:** 03 (composition root), 19 (Constellation), 37 (channels), 42 (module API), 46 (Kodo), 48 (module trust).

---

## 9. Conflicts found with older docs

These are recorded, not silently resolved. Each one names the doc and line. The owner decision in this doc governs for the topic. The doc owner corrects the older text in the same change that implements the decision.

| ID | Topic | Older text (doc, line) | Conflict | Resolution in this doc |
|---|---|---|---|---|
| O-1 | OFFLINE | 02 line 79 ("The cache never holds GC content"); 27 line 50 (offline cold start, cache holds the last profile); 29 line 12 ("cache holds no message bodies") | Brief requires full offline editing, which needs a local content cache | Cold-start shell rule stays. Content rule changes: section 4.1 |
| O-2 | OFFLINE | 28 line 99 ("Offline: editor goes read-only") | Brief requires offline editing of Vault documents | Vault edits offline with merge (section 4.2.1). Read-only applies only to a document with a pending conflict |
| S-1 | SUPPLY CHAIN | 37 line 9 ("Signing is parked, not skipped... produced unsigned by default"); 37 line 49 (unsigned release when secrets missing); 30 line 124 (`packaging.signing_mode` default `unsigned-beta`) | Brief DECIDED signed releases | Section 3.2. The unsigned default is removed for releases; development builds stay unsigned and are refused on release channels |
| S-2 | SUPPLY CHAIN | 37 section 4 (release built in CI and published from GitHub Actions); brief (server builds and signs per-company packages, keys on the server) | Two build paths are not described together | Both paths exist: project releases from CI, company builds on the company server (section 3.2 layers A and B). Doc 37 needs a company-build section |
| U-1 | UPDATES | 04 line 30 ("Forward-only; rollback means restore (doc 33)"); 33 line 72 (rollback policy open, Q3) | Not a conflict in direction. The update rollback question was open | Updates use a write freeze so the restore loses nothing (section 6.4). The general rollback case stays open in doc 33 |
| A-1 | ACCESSIBILITY | 27 line 181 (screen reader v1 requirement open); 19 line 158 (keyboard list mirror open) | Brief DECIDED both as required | Section 2 decides them. Doc 27 and doc 19 update their question lists |
| A-2 | ACCESSIBILITY | 27 line 121 (localisation PROPOSED) | Brief DECIDED i18n from day one | Section 2.3 |
| SC-1 | SCALE | 04 line 110 (hash partitions are a hedge, spike S3 pending); 35 line 99 | Consistent: the single-node decision keeps the hedge until measured | No conflict. Section 1 keeps S3 as the trigger for sizing |
| L-1 | LICENCE (affects 7 and 8) | CLAUDE.md, licences line (Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts`, 2026-10-25); NOTICE (same, pending counsel); brief (AGPL-3.0 across all repositories, module exception, CLA for core, 2026-11-13) | Two DECIDED licence sets disagree | OPEN. Owner to confirm the licence of Sdk and Contracts. Section 7.1 and 8 do not depend on the answer |
| L-2 | LICENCE | 21 line 20 (DCO model, "CLA is not chosen"); 17 line 169 (DCO vs CLA stays OPEN) | Brief adds a CLA for core code | Brief governs. Docs 17 and 21 update their DCO text. CONTRIBUTING.md (DCO only) also needs the CLA, outside this doc |
| D-1 | DEPS (adjacent, not this doc's topic) | 03 line 146 (PostgreSQL 18 "runs as a service"); 03 line 23 and 04 line 10 (PostgreSQL 18 only) | Brief: PostgreSQL 18 and 19 are versions of one module, bundled and run as a hidden supervised child process, zero external installs | Flagged for the owner and the dependency doc. Section 6 uses the brief's model |
| V-1 | VERSION FORM (adjacent) | 37 line 8 (display `v00.MM.PP`); CLAUDE.md versioning (display `0.MM.PP+commit`, release tags `v0.1.0`) | Two display forms | Flagged for `docs/VERSIONING.md`. Section 3 uses the SemVer tag form |
| I-1 | INGRESS (checked, consistent) | 03 line 18; 19 line 23; 21 line 18; 22 line 17; 32 line 11; 33 line 20 (no inbound by default) | No conflict. Conduit ingress is opt-in and its limits apply when enabled (section 5) | None |

---

## 10. OPEN items

Only items the brief does not decide. Each has the default that applies until the owner decides.

1. Sizing numbers for the scale profiles (default: profiles from the benchmark; section 1).
2. Cross-database fan-out (default: `NOTIFY` in one database; section 1).
3. Shipped languages (default: English, with the pipeline complete; section 2.3).
4. Project release key custody and an OS-trusted publisher certificate (default: company builds carry the company key; section 3.2).
5. Start of the reproducibility project (default: after a stable release pipeline; section 3.5).
6. Cross-company fleet view (default: one fleet per install; section 6.9).
7. Policy file format (default: YAML with a JSON Schema, pending the onboarding doc; section 6.8).
8. Licence of `Bazaar.Sdk` and `Bazaar.Contracts` (section 9, L-1).
9. Names of the extracted packages and repository (section 8).
10. Whether sandbox tiers need an extracted package (section 8).

---

## 11. Changes this doc asks for in other docs

- Doc 02, line 79; doc 27, lines 50 and 181, and the Localisation section; doc 28, line 99; doc 29, line 12: align with sections 4 and 2.
- Doc 19, line 158: keyboard list mirror now required (section 2.1).
- Doc 30, line 124 and step 10; doc 37, lines 9 and 49, and a company-build section: align with section 3.
- Doc 33, the rollback policy: add the update case (section 6.4).
- Doc 41 (planned): add the `conflicts`, `updates` and monitor mnemonics, and the client-wide mnemonic table, DECIDED in brief.
- Doc 42 (planned): the module API surface, SemVer tiers, conformance suite, and matrix (section 7).
- Doc 46 (planned, Kodo): health gates, incident mode freeze, fleet view, history, and the update monitor pane (section 6).
- Doc 48 (planned): lockout exemption for recovery, audit mechanics used by sections 5 and 6.10.
- DECISIONS-LOG: record this doc's DECIDED items under the 2026-11-13 round, with the O-, S-, L- and A- conflict IDs above.
