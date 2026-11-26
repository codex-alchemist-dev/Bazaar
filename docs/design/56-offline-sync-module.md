# Offline sync module (offline editing with merge)

Status: DECIDED for direction and per-class merge rules (doc 49 section 4). The module layout, wire shapes and defaults below are PROPOSED unless tagged otherwise.
Implements: `docs/design/49-gap-decisions-operations.md` section 4 · Depends on: 01 (GC log and versions), 02 (replay, sequence), 12 (Kanban fields), 18 (Vault git), 19 (layouts), 27 (client cache and queue), 48 (audit log) · Used by: client heads, doc 33 (recovery)

## 1. Purpose

Doc 49 section 4 decides that clients edit fully offline and merge on reconnect, with one merge rule per data class. This doc fixes where that code lives and the shapes it uses. It adds no new merge rule. Where doc 49 is silent, this doc fills the gap as PROPOSED.

## 2. Module layout (PROPOSED)

Per the module-sides rule, server and client halves are separate modules, with a shared contracts project. Nothing goes into `Bazaar.Server`, `Bazaar.Client.Core` or `Bazaar.Core`. No module references `Bazaar.Server.csproj`.

| Project | Role | References |
|---|---|---|
| `Bazaar.OfflineSync.Abstractions` | Shared contracts: hybrid logical clock, `OfflineWrite` envelope, `OfflineWriteBatch`, data classes, merge outcomes, `IOfflineMergeRule`, `IOfflineSyncStore`. No implementation beyond value types. | none |
| `Bazaar.Server.OfflineSync` | Intake of batches, idempotent replay, the per-class merge rules, merge audit records. Persists through `IOfflineSyncStore` (an in-memory store ships for tests and dev; the Postgres store follows in its own PR with its own migration namespace `offline_sync`). | `Bazaar.OfflineSync.Abstractions` |
| `Bazaar.Client.OfflineSync` | Encrypted pending-write queue, "as of" read cache metadata, connection and banner state, the replay driver. | `Bazaar.OfflineSync.Abstractions` |

Component ids: `offline-sync` (server) and its client counterpart. Soft dependencies: `gc`, `kanban`, `vault`, `audit`. A missing dependency disables only the merge rule for that data class; the queue still holds the writes and reports them as "waiting for module".

### 2a. Overlap check (2026-11-18)

Read before adding the module: the GC log writer already dedupes on a client message id (`Bazaar.Server/Gc/Write`, doc 02 replay), the wire layer already resumes GC feeds (`Bazaar.Server/Wire/Sync`), Kodo has its own journal replay (doc 46), and `Bazaar.Vault.Abstractions` holds the Vault contracts. No HLC, device queue or per-class merge exists elsewhere. So: the GC rules are thin adapters that call the GC writer through `Bazaar.GC.Abstractions` and keep its client-id idempotency (this module's `WriteId` is that client id); the Vault rule is implemented by the Vault module; live GC feed resume stays in the wire layer, and this module covers only writes made while offline.

## 3. Shared shapes (PROPOSED)

- **Hybrid logical clock (HLC).** `(wallMillis: long, counter: int, deviceId: Guid)`, ordered by wall time, then counter, then device id, so the order is total. `Tick(now)` and `Receive(remote, now)` follow the standard HLC algorithm. The server rejects writes whose wall time is more than `MaxClockSkew` (default 24 hours, operator setting, floor 1 minute) ahead of the server clock; such a write is held for review, never dropped.
- **`OfflineWrite`.** `WriteId` (client UUID, the idempotency key), `DeviceId`, `DeviceSequence` (gapless per device, starts at 1), `DataClass`, `Target` (the object key: GC id, task id, vault path, preference key), `BaseVersion` (per field for Kanban, a commit id for Vault, none for append-only classes), `Payload` (opaque bytes, class-specific, JSON by the wire codec), `Hlc`.
- **`OfflineWriteBatch`.** One device's writes in `DeviceSequence` order. The server applies a batch atomically per write, never reordering within the batch, and acknowledges the highest applied `DeviceSequence`.
- **Data classes.** `GcMessage`, `GcMessageEdit`, `VaultCommit`, `KanbanField`, `KanbanEdge`, `ConstellationPosition`, `Preference`, `Attachment`, `AuditAction`. Settings and policies have no data class: the queue refuses them with a reason (doc 49).
- **Outcomes.** `Applied`, `Duplicate` (same `WriteId` seen), `Conflict` (goes to a review item), `ConcurrentEdit` (GC edits that both survive), `HeldForReview` (clock skew, safety-flagged preference), `Rejected` (with reason, for example a forbidden Kanban transition), `ModuleUnavailable`.
- Every outcome other than `Duplicate` writes one audit record: device, `WriteId`, base version, server version, outcome (doc 49).

## 4. Idempotency and ordering (PROPOSED)

- The server keeps `(DeviceId, WriteId)` as a unique key with the stored outcome. A replay returns the stored outcome and changes nothing.
- A device sequence gap (the server has applied sequence 7 and receives 9) stops the batch at the gap and returns `GapAt(8)`; the client resends from 8. A sequence already applied is a `Duplicate`.
- Merge order for the classes with a defined arrival order (GC, audit) is arrival order at the server, with each device's own order kept inside its batch (doc 49).
- The merge rules are pure functions of `(current server state, write)` plus the store. The same set of writes yields the same result for any arrival order wherever doc 49 says it must (Kanban fields, last-writer-wins keys, attachments, edge unions).

## 5. Per-class rules as code (doc 49, restated for the implementer)

1. **GC message.** Dedupe on `WriteId`; assign the next per-GC sequence on append; client device time is stored for the "sent offline at" label only.
2. **GC edit.** New version row; the higher server sequence is current; the other is kept and flagged `ConcurrentEdit`.
3. **Kanban field.** Applies only if the server field version equals the write's `BaseVersion`; otherwise `Conflict` with a review card. Status changes re-run transition rules after merge; Blocked is recomputed. Edges merge as a union, then the cycle check runs on the result; a cycle rejects the offending edge. Removing an edge is a field change.
4. **Constellation position.** Last-writer-wins per node key by HLC; the previous value is kept in history.
5. **Preference.** Last-writer-wins per key by HLC. Keys marked safety-relevant (notification rules) become `HeldForReview` on a conflicting change.
6. **Attachment.** Content-addressed; identical hash is a no-op `Applied`.
7. **Audit action.** Appended in arrival order and hashed into the chain by the audit module; no client-side order.
8. **Vault commit.** The git mechanics (three-way merge with `diff3`, `conflict/<device>/<timestamp>` branch, review item) belong to the Vault and Git modules. This module only carries the push intent and records the outcome; the rule is a `IOfflineMergeRule` implemented by the Vault module through the abstractions. The first PR ships the contract, not the git rule.

## 6. Client queue (PROPOSED)

- The queue is a local file store encrypted with AES-256-GCM. The key comes from `IOfflineQueueKeyProvider` (platform key storage: DPAPI on Windows, key file elsewhere, the same rule as the KEK). Each record is authenticated with its `DeviceId` and `DeviceSequence` as associated data, so records cannot be swapped or reordered undetected. A tampered record fails closed: the queue stops and reports corruption; it never skips.
- Queue limit default 50,000 writes or 256 MiB, operator-tunable; when full, new offline edits are refused with a visible reason, never silently dropped.
- Disabled controls offline (settings, company locks, agent memory, Passport, secrets) are listed by a policy in the client module, so heads render the same reason text.
- Connection state is `Live`, `Offline(asOf)`, `Syncing(progress)`, `Attention(count)` for the doc 27 connection chip.

## 7. Testing

- Unit tests per merge rule, in the module's own test project.
- Property tests: random offline sessions merged in every arrival order of the same batches give the same Kanban, preference and position results; GC order is stable and gapless (doc 49).
- Chaos tests: drop the link after each write of a batch, replay, and assert no duplicates and an identical final state (doc 34, doc 46 chaos mode).
- Queue tests: encryption round trip, tamper and reorder detection, restart recovery.

## 8. Open Questions

1. Retention of `(DeviceId, WriteId)` rows. PROPOSED: keep for the life of the device registration. The schema itself (tables `offline_write_seen`, `offline_device_seq`, `offline_write_claim`, `offline_review_item` and the per-class state tables) is built in `Bazaar.Server.OfflineSync.Postgres`, migration namespace `offline_sync`.
2. Whether `MaxClockSkew` should be per device class (a phone clock drifts more). PROPOSED: one value.
3. Vault merge rule wiring: the Vault module's engine seam is `IVaultOfflineMergeEngine` in `Bazaar.Vault.Abstractions` (`Merged(NewHead)`, `Conflicted(BranchName, ConflictedPaths)`, `Rejected(Reason)`). An adapter in this module maps it to `IVaultOfflineMerger` and opens the review item; the host picks the engine by repository (so `Repository` is not part of the engine seam). `VaultPushIntent.Branch` is the device's source branch; the target is always the vault main head. The commit trailers `Bazaar-Device`, `Bazaar-Offline-Branch`, `Bazaar-Offline-Write` (the `WriteId`) and `Bazaar-Edit-Base` are PROPOSED in doc 18. The adapter lands once the Vault merger branch merges.

4. Persistent merge-state stores must apply the claim of a `WriteId` and the state change in one transaction, and the host must serialize merges per `Target` (the Kanban edge cycle check relies on it). The in-memory states in step 1 are for tests and dev only.
5. Where a held safety preference stores its value until review. PROPOSED: in the review item.
6. Kanban Blocked recompute is delegated to the transition checker supplied by the Kanban module.
7. Queue integrity limits: dropping whole records from the file end, or deleting the file, is not detectable without an anchor outside the file. PROPOSED: store the acked sequence and record count in platform key storage next to the key. A torn tail after a crash refuses to open (fail closed). PROPOSED repair, most robust option: (a) writes append a record, flush and fsync before the write is reported queued, so only the last record can ever be torn; (b) on open, if exactly the final record fails to authenticate or is short, the queue moves the whole file to `<name>.quarantine-<utc>` (never deletes), rebuilds a new file from the valid prefix, and reports `Attention` with the count of lost writes (at most one, the unacknowledged last write) so the user can redo it; (c) any failure before the last record (tamper, reorder) is never repaired and stays fail closed; (d) the anchor above (record count and acked sequence in key storage) distinguishes a torn tail from truncation: repair applies only when the stored count is at most one more than the valid prefix.
8. `ModuleUnavailable` outcomes: the client keeps those writes queued and shows them as waiting for module (this matches section 2). The first implementation acks them per the server's `AckedSequence`; follow-up before heads ship.
9. A queue directory is single-process. PROPOSED: an exclusive file lock. Built: `offline-queue.lock` is opened with no sharing for the life of the queue, so the operating system releases it on a crash and a second open throws `OfflineQueueLockedException`.

## 9. Build state

PLANNED. Step 1 (done): abstractions, server merge engine with in-memory store, client queue. Step 2 (built): Postgres store, one transaction per write with a per-target advisory lock. Step 3 (built, head composition pending): `POST /offline-sync/batch` bound to the authenticated device, client HTTP transport; The route binds to the device of the access token that the authentication middleware verified (`GetAuthenticatedCaller`, doc 32 section 5); a credential with no server-assigned device id still answers 403 `device_unbound`. Step 4 (built as a seam): `VaultCommitRule` delegates to `IVaultOfflineMerger`, which the Vault module implements. A file-backed anchor (`FileOfflineQueueAnchorStore`: record count and acked sequence, HMAC under the queue key and bound to the device id, atomic temp-and-rename saves) is built as the cross-platform fallback; a platform key-storage store (DPAPI or the OS keychain) is still to come. Torn-queue repair is built (quarantine copies the file rather than moving it, so a crash mid-repair never leaves no queue; the anchor's record count is the highest appended sequence); the replay driver seeds Attention with the repair's lost-write count, and it stays until the queue is reopened (PROPOSED: a client acknowledge action clears it). `OfflineSyncClientFactory` composes queue, transport, tracker and driver; heads compose only pane modules today, so wiring a non-pane client module into a head needs a module list in the client kernel (OPEN, Core thread).

**Platform key storage (built).** `Bazaar.Client.OfflineSync.PlatformKeys` is its own module (the core queue keeps no OS-specific code). `ProtectedFileKeyProvider` is the persistent `IOfflineQueueKeyProvider`: it creates the 32-byte key once, wraps it with an `IOfflineKeyProtector` (`DpapiOfflineKeyProtector` in the current-user scope on Windows, `KeyFileOfflineKeyProtector` elsewhere, with the file at mode 0600) and writes it by temp file and rename. A damaged, foreign or wrong-length key file throws `OfflineQueueCorruptedException` and is never replaced, since a fresh key would silently orphan the queue. The OS keychain (macOS, Android keystore) is a further protector behind the same interface, still to come with those heads.
