# Encrypted data folder: at-rest encryption for the data root and PostgreSQL

Status: DECIDED for the requirement (owner, 2026-11-19, message `cmsg_01MC1BkUhiVHzqr38uyxWzeHPuokZJa7aiq87n8BAaMdas`); the scope (whole data root) and the unlock mode are DECIDED (owner cards, 2026-11-19); the other mechanics are PROPOSED, and the BitLocker VHDX facts were measured in spike D1 on the owner's PC (section 3.1); the LUKS facts are unverified until the Linux half of the spike runs.
Depends on: `03-server-host-and-providers.md` (startup, providers, floors), `04-postgres-schema-and-migrations.md` (per-person keys), `14-identity-service-and-passport.md` (KEK, `identity.kek_provider`), `33-backup-dr-and-export.md`, `42-deps-as-modules.md` (bundled PostgreSQL), `45-onboarding.md` (setup steps), `46-kodo-health-module.md`, `48-gap-decisions-security-identity.md` (sections 5, 6, 9, 10), `61-sealed-storage.md`
Modules: `Bazaar.Server.DataVolume` and `.Abstractions` (component id `data-volume`), provider modules `Bazaar.Server.DataVolume.Windows` and `Bazaar.Server.DataVolume.Linux`, and `Bazaar.Server.ColumnSeal` and `.Abstractions` (component id `column-seal`)

## Owner requirement (DECIDED)
The owner asked whether people can read the PostgreSQL database outside Bazaar. The answer was: over the network, no (SCRAM, loopback); from the files, yes, because community PostgreSQL has no transparent data encryption and only personal and secret fields are encrypted by the application. Owner, 2026-11-19: "Sure. Encryption is on the disk data folder level."

Made concrete:
1. **The data folder is encrypted, by Bazaar, at the folder level.** The data root (PostgreSQL data directory, WAL, temp files, blobs, logs, local backup staging and every other server file) lives inside a dedicated encrypted volume that Bazaar creates, keys and mounts. Whole-disk encryption by the operator is welcome in addition, and does not satisfy this requirement on its own.
2. **Hard install requirement.** Setup cannot finish without the encrypted volume. There is no "skip" and no acknowledgement path.
3. **Fail closed at start.** The host refuses to start, and never starts PostgreSQL, when the data folder is missing, not mounted, not encrypted, or not the volume this install created. The host never creates a key, a volume or a database cluster at start (section 5). This closes the chaos finding "a missing data root mints a new KEK" (PR #872, open item 1).
4. **More columns sealed by default.** Volume encryption protects the files; it does not protect against someone who gets a SQL session or a dump. Free-text and payload columns are therefore sealed in the application by default (section 9).
5. **Not a setting.** This is a security floor (doc 03 settings registry, doc 48 section 4.1 style). Settings may tighten it and never turn it off.

This supersedes the row "Postgres data directory: out of scope, operator disk encryption" in doc 61's inventory.

## 1. What this does and does not protect
Protects against: a stolen or copied disk or data folder; a copied container file; reading the files from another OS install or another OS account while Bazaar is stopped; backups or images of the host disk; a returned or discarded drive.
Does not protect against: an administrator (or the Bazaar service account) on the live host while the volume is mounted; memory compromise; the page file, swap, hibernation file and crash dumps of the host OS (section 11); a SQL session with stolen database credentials (that is what section 9 and the existing field encryption are for).
A stolen whole machine: protection is exactly as strong as the KEK custody (doc 14). With `dpapi` or `file`, the key material is on the same machine, so an attacker who can read the system disk offline can recover it. Only a TPM-bound KEK (doc 14 `tpm`, PR #873) or an owner unlock (section 6) resists whole-machine theft. Setup states this in plain words and records the custody class.

## 2. Layout: boot root and data root
**DECIDED (owner card, 2026-11-19): the whole data root is inside the encrypted volume**, not only the PostgreSQL folder. Two folders, both set in host config (doc 03):

| Folder | Config key | Encrypted | Holds |
|---|---|---|---|
| Boot root | `Bazaar:Host:BootRoot` | No (small, holds only ciphertext and public facts) | `keys/kek.bin` (doc 14, already protected by the KEK provider), `volume/descriptor.json`, the container file when the container kind is `file`, and nothing else |
| Data root | `Bazaar:Host:DataRoot` | Yes: it is the mount point of the encrypted volume | Everything else: `pg/<major>/data`, blobs, logs, `secrets/`, sealed-storage scopes (doc 61), Vault working copies, backup staging |

Every existing consumer of the data root moves inside the encrypted volume without a code change. The only move is the KEK file, from `<data root>/keys` to `<boot root>/keys`, because the KEK must open before the volume. Host config also records the install id and the volume id (written by setup), so a swapped boot root is detected.

**Descriptor** (`volume/descriptor.json`): format version, install id, volume id (BitLocker volume GUID or LUKS UUID), provider kind, container kind and path, cipher, KEK id, the volume key wrapped through `IKekProvider` with associated data `bazaar.datavolume.key.v1|<installId>|<volumeId>|<kekId>`, and the marker key wrapped the same way (`bazaar.datavolume.marker.v1|...`). It holds no plaintext key. A changed descriptor fails the unwrap.

**Marker** (`<data root>/.bazaar-volume`): install id, volume id and creation time, authenticated with HMAC-SHA-256 under the marker key. It proves that the file system mounted at the data root is the one setup created, and not an empty mount point, a different volume or a plain folder with the same name.

## 3. Providers
OS-specific work sits behind `IDataVolumeProvider` (provider role `DataVolume`, doc 03 selection rules: `auto` probes, an explicit kind that is unusable fails startup, no silent fallback).

| Kind | Host | Mechanism | Status |
|---|---|---|---|
| `bitlocker-vhdx` | Windows Pro, Enterprise, Education, Server | A VHDX file (dynamic) holding one NTFS volume protected by BitLocker (XTS-AES-256), attached at start and mounted on the data root folder (a mount point, no drive letter). The only key protector is an external key: 32 random bytes that Bazaar generates and passes to BitLocker in memory through WMI (`Win32_EncryptableVolume.ProtectKeyWithExternalKey`, `UnlockWithExternalKey`); no `.BEK` file is written. | PROPOSED; mechanism confirmed by spike D1 (section 3.1) |
| `luks2` | Linux | A LUKS2 container (`aes-xts-plain64`, 512-bit key, argon2id not used because the key is random) on a sparse file or a dedicated block device, opened with `cryptsetup open --key-file -` (key on stdin), ext4 or XFS inside, mounted on the data root. | PROPOSED, Linux spike pending |
| `fscrypt` | Linux | Native per-directory encryption (ext4, f2fs). No container to size, but file sizes, counts and permissions stay visible. | PLANNED, later option |
| `dev-plain` | Any | No encryption. Section 8. | PROPOSED |

Container kind is `file` (default: one file under the boot root or a path the operator names) or `device` (a partition or disk the operator dedicates). Windows editions without BitLocker management (Home) have no shipped provider: setup stops at preflight with that reason. Rejected: EFS (keys live in the service account's profile and cannot hang off the KEK; no hardware binding; names and sizes visible), FUSE file systems such as gocryptfs (too slow and fragile for a database), a third-party driver such as VeraCrypt (kernel driver outside Bazaar's signed tool chain; licence not OSI-approved).

### 3.1 Spike D1 result, Windows (2026-11-19, owner's PC, Windows 11 Pro 10.0.26300, PostgreSQL 18.0)
Measured, all on `V:`, cleaned up afterwards:
- **In-memory external key works.** `ProtectKeyWithExternalKey` accepted 32 caller-supplied bytes; `Encrypt` with method 7 (XTS-AES-256, used space only) finished in about 1 s on a new 4 GB dynamic VHDX; `UnlockWithExternalKey` with the same bytes took 36 to 50 ms. No `.BEK` file was written.
- **The folder mount persists.** After detach the folder stays as a dangling reparse point; after re-attach (about 0.1 s) the mount returns by itself with the same volume GUID, and the volume is always locked until an explicit unlock.
- **Everything needs an administrator token:** attach, detach, every `Win32_EncryptableVolume` call, `manage-bde -status`, `Get-BitLockerVolume`. This confirms the volume unit of section 4.
- **An unprivileged process cannot read the protection state of a folder mount.** The shell property `System.Volume.BitLockerProtection` answers only for drive-letter roots. Unlocked, the mount looks like a normal NTFS folder; locked, reads fail with HResult `0x80310000` (`FVE_E_LOCKED_VOLUME`). So on Windows the host gets "encrypted, protection on, cipher" from the volume unit's status channel and nothing else, and verifies identity itself (next point and the marker).
- **How the mount looks to .NET:** attributes `Directory, ReparsePoint`; `LinkTarget` is `Volume{guid}\`; `Path.GetPathRoot` and `DriveInfo` report the hosting drive, not the mounted volume. The volume GUID comes from `GetVolumeNameForVolumeMountPoint` (what `mountvol <folder> /L` prints), which works without elevation. The provider uses it to compare with the descriptor.
- **A forced lock under a running PostgreSQL is fatal to the cluster process:** writes fail, the WAL writer dies, the postmaster panics on `pg_control` and exits without a clean shutdown. Consequence (now a rule in section 4): the volume unit never locks or detaches while PostgreSQL runs.
- **Cost:** `pgbench` scale 10, 4 clients, 30 s, one run each: 5060 tps encrypted against 6024 tps plain, about 16% lower. That is above the 10% budget of section 11 on a single unrepeated run; work package 1 repeats it with warm caches and tuned settings before the budget is kept or changed.
- On that PC `C:` and `X:` are already BitLocker-protected and `V:` is not, so on `V:` the VHDX is the only encryption layer. A TPM is present.

## 4. The volume unit and the host (privilege split)
Attaching a VHDX, unlocking BitLocker, `cryptsetup` and `mount` need administrator or root. The Bazaar service account is never Administrator (doc 03 floor). So two processes:

- **Volume unit** (privileged, tiny): the same executable run as `bazaar-server volume open|close|status`. Windows: a second service `BazaarVolume` running as LocalSystem, which the main service depends on in the Service Control Manager. Linux: a systemd unit `bazaar-volume.service` (root, `Before=` and `RequiredBy=` the main unit; the main unit also has `RequiresMountsFor=<data root>`). It reads the descriptor, opens the KEK, unwraps the volume key, attaches, unlocks, mounts, sets the mount's ACL or owner to the service account, zeroes the key and answers status queries (named pipe on Windows restricted to the service SID; Linux needs no channel because the facts are world-readable in `/proc` and `/sys`). It has no network access and no other verbs. **Close order (from spike D1):** the unit closes only after the host has stopped PostgreSQL cleanly; `close` refuses while a `postgres` process still has the data directory open, and the service dependency makes the service manager stop the host first. There is no forced lock in normal operation. An unexpected lock (pulled drive, crash of the unit) is handled as section 5 says: the host stops, and PostgreSQL recovers from WAL at the next start.
- **Host** (unprivileged): never holds the volume key. It holds only the marker key. It verifies; it cannot open.

## 5. Start sequence and the guard (fail closed)
Doc 03's startup gains three steps between "start logging" and "take the instance lock". Before the volume is open, logs go to stderr and the OS log only (the file sink is inside the data root).

1. **Open the KEK, never create it.** `KekProviderFactory` opens an existing key or fails. A missing or zero-length `kek.bin`, a missing boot root or a missing descriptor is `data_volume_not_initialised` and the host exits. Creating the KEK, the volume and the cluster happens only in setup (`bazaar-server setup`, doc 45) and in the restore wizard (doc 48 section 6).
2. **Verify the data volume** (`IDataVolumeGuard.VerifyAsync`), all of:
   - the data root's real path (symlinks, junctions and mount points resolved) is on a mounted volume, and that volume's id equals the descriptor's and host config's;
   - the OS reports that volume as encrypted with protection on and conversion complete (Windows: BitLocker status from the volume unit; Linux: the mount source is a device-mapper device whose `dm/uuid` starts with `CRYPT-LUKS2-` and carries the descriptor's UUID), with a cipher at or above the floor (AES-256-XTS);
   - the marker is present and its HMAC verifies under the marker key, and its install id and volume id match.
3. **Verify PostgreSQL's files before starting it.** The data directory, `pg_wal` and every tablespace location resolve inside the verified data root (no symlink out). After start, `pg_tablespace` and `data_directory` are checked again, and the cluster's system identifier must equal the one recorded in the marker at setup.

Any failure: PostgreSQL is not started (or is stopped), one log line names the check, the host exits with code 78 (`EX_CONFIG`; the code is PROPOSED) and the reason is one of `data_volume_not_initialised`, `data_volume_not_mounted`, `data_volume_not_encrypted`, `data_volume_identity_mismatch`, `data_volume_marker_invalid`, `postgres_files_outside_volume`. There is no override flag. Setup has one more reason, `data_volume_already_initialised`: `storage init` never overwrites a descriptor, a container or a non-empty data root.

**While running.** Kodo (doc 46) runs the guard as a probe every `storage.volume.recheck_seconds`. If the volume locks, unmounts or changes identity, the host stops crash-only (doc 46 section 7.2): it does not try to remount or continue. `/health/ready` reports `data_volume` as a readiness role that can never be removed from the set (doc 03 floor list gains this line). Every refusal, open and close writes an audit entry when the audit log is reachable (`storage.volume.opened`, `.closed`, `.refused`) and always a local OS log line.

**External database mode** (hidden advanced, doc 42). Bazaar cannot inspect another machine's disk. PROPOSED default: `storage.external_db.at_rest = refuse` unless the owner records an attestation naming the method (Tier 2, audited); the status page and Kodo then show "at rest: attested, not verified". When the external server is on the same host, the guard runs the OS checks on `SHOW data_directory` and treats the result as verified. It is a setting with the most protective default (configurability rule), question 3.

## 6. Keys and unlock
- **Volume key:** 32 random bytes, generated in setup inside the volume unit, used as the BitLocker external key or the LUKS key-slot key, kept only wrapped in the descriptor. It never touches disk in clear and is never logged.
- **Custody:** the volume key hangs off the KEK provider, so it inherits `dpapi`, `file`, `tpm` (PR #873) or `openbao` with no new custody decision. Recovery needs no new secret either: the sealed KEK copy (doc 14, doc 48 section 6) plus the descriptor (ciphertext, included in the recovery bundle) reopen the volume on the same disk. On new hardware the path is the normal restore: new volume, restore the database from the encrypted backup.
- **Unlock mode** (`storage.volume.unlock`), **DECIDED (owner card, 2026-11-19): automatic now, owner unlock built as a setting for later.** `auto` (default; the volume unit opens the volume at boot with no person present, which keeps unattended restarts working) or `owner` (after each host restart the volume stays locked until an owner approves the unlock from a signed-in client device; the host is down until then). With `owner`, the wrapped volume key is additionally sealed to the owner's device key so the server alone cannot open it [mechanics OPEN, depends on doc 48 section 7].
- **Rotation:** KEK rotation re-wraps the descriptor. Volume key rotation adds a new protector or key slot and removes the old one; it does not re-encrypt the data. Full re-encryption is done by migrating to a new volume (section 10).
- **Erase:** destroying the descriptor and the protector makes the volume unreadable. This is recorded as an option for doc 48 section 9; crypto-erase stays parked there pending counsel.

## 7. Size and growth
The container is thin: a dynamic VHDX or a sparse file, with a ceiling `storage.volume.max_gb` (default: the free space of the hosting drive minus a reserve, set at setup). VHDX grows by itself. On Linux the volume unit grows the file, the LUKS mapping and the file system online in steps of `storage.volume.grow_step_gb` when free space falls under the threshold. Kodo reports space inside the volume and on the hosting drive; low space degrades health and does not stop the host (doc 03). A `device` container is fixed by the operator.

## 8. Development instances
Contributors, CI and unit tests cannot always create real encrypted volumes. `dev-plain` is accepted only when all of these hold: setup created the instance with class `development` (written in host config and the marker, and it can never be changed to `production`; going to production means migrating into a new encrypted volume), `DOTNET_ENVIRONMENT=Development`, and no provider key or owner credential of class "real" is stored. A development instance shows a permanent "not for real data" state on the status page and in Kodo, and its audit log says so. Automated tests use a fake provider from `Bazaar.Testing`. The start guard itself is identical in every class: only the "is encrypted" check is answered by the provider.

## 9. Column sealing (default set)
The volume protects files. Columns are sealed so that a SQL session, a logical dump or a replica does not show content. Today's field encryption is per feature (`gc_message_version.body_ct`, `gc_event.payload_ct`, `council_ballot.reasoning_ct`, `calendar_event.title_ct`, `mail_message.body_ct`, `credential`, `browser_profile`, interrupt reasons), each with its own cipher class.

**Module.** `Bazaar.Server.ColumnSeal` provides one `IColumnSealer`: AES-256-GCM, a random 12-byte nonce, associated data `bazaar.colseal.v1|<table>|<column>|<row key>|<key id>` (a value cannot be moved to another row, column or table), stored as `<column>_ct bytea` and `<column>_nonce bytea`. Keys are DEKs wrapped through `IKekProvider`: the author's `person_key` when the content is personal (doc 04, so erasure still works), otherwise a per-scope key in a new `column_dek(scope, dek_id, wrapped_dek, kek_id, created_at, retired_at)` table shaped like `soul_dek` (doc 14). The existing feature ciphers move onto it one at a time; their stored formats are read until rewritten.

**Classes.**

| Class | Rule | Columns in today's schema |
|---|---|---|
| A. Content and payloads | Sealed by default. Free text or structured payloads written by people, agents, models or the outside world. | `turn_step.response`, `turn.context_manifest`, `approval.payload`, `approval.target`, `job.payload`, `job.last_error`, `task_events.payload`, `entry_ref.snapshot`, `community_action.body`, `soul_versions.preferences`, `session_taint.sources`, `ryusei_fire.cause`, `ryusei_trigger.spec`, `gc_event.payload` (the non-personal part; the personal part is already `payload_ct`), `mail_message.auth_json`, `gw_provider.headers`, free-text `reason` and `detail` columns (kill switch, legal hold, reputation, soul retirement, module line moves, council transitions) |
| B. Personal identifiers | Sealed under the person's key (doc 04; DECISIONS-LOG "per-person encrypted personal fields from day 1"). These are gaps against an existing decision. | `human.display_name`, `device.label`, `privacy_settings.contact`, `bot_profile.contact`, `mail_message.from_addr`, `audit_log.payload` personal fields (doc 48 section 5.5) |
| C. Titles and labels | Sealed by default, with a keyed blind index (HMAC of the normalised value) where equality lookup is needed. | `gc.title`, `gc.metadata`, `tasks.title`, `vault_docs.title`, `constellation_memory_revision.title` |
| D. Search and retrieval copies | Not sealed, because PostgreSQL must read them to search: `search_document.body`, `vault_blocks.body`, `tsvector` columns, embeddings. They exist only inside the encrypted volume. Rule kept from doc 04 question 4: no search copy is built from content whose source is sealed under a person's key unless the owner decides otherwise. | as listed |
| E. Structure | Plain: ids, sequence numbers, timestamps, states, kinds, hashes, counters, foreign keys, public keys, settings values that are not secrets. | everything else |

`setting.value` stays plain (settings are not secrets; secrets are references, doc 14). The audit hash chain covers the stored bytes, so sealing audit payload fields does not break verification after erasure (doc 48 section 5.5).

**Migration.** Each table gets a numbered migration in its own namespace adding the `_ct` and `_nonce` columns; a background job seals existing rows in batches and then the plain column is dropped in a later migration. `common/` is frozen, so its tables are altered from the `column_seal` namespace. A CI test fails when a new `text` or `jsonb` column is added without a class in the column-class registry (`docs/design/data/column-classes.json`, generated into the test).

**Cost.** Sealed columns cannot be filtered, sorted or indexed in SQL. Class A columns are read by key today. Where a feature needs a lookup on a sealed value, it uses a blind index, not a plain copy.

## 10. Setup, migration and backups
- **Setup (doc 45).** Preflight checks the edition (BitLocker management present), `cryptsetup` and kernel support on Linux, and that the volume unit can be installed. Step 3 becomes "Key, encrypted volume and bundled PostgreSQL": choose the KEK provider (moved forward from step 5, because the KEK must exist first), create the volume, write the descriptor and marker, run `initdb` inside it, record the cluster's system identifier in the marker. Step 5 keeps the Identity Service bootstrap and the audit anchor. Dry-run reports what would be created. Headless config gains `bootRoot`, `storage.volume.*`.
- **Setup mechanics (plan section `storage`).** The setup apply (doc 45 section 1.3) runs inside a serving host whose database is already up, so it cannot create the volume under the cluster. The work is therefore split in two, both in the module `Bazaar.Server.Setup.Storage`:
  - *Before the host is built.* When a setup plan is named at start (`--setup-plan`, doc 45 section 1.1) and its `storage` section asks for a volume, the host runs the same code as `storage init` first: it creates the KEK if absent, creates the volume, writes the descriptor and the marker, and then writes `config/volume.json` on the boot root with `Bazaar:Host:DataRoot`, `InstallId`, `VolumeId` and `InstanceClass`. The host reads that file as configuration below environment variables. `Bazaar:Host:BootRoot` itself comes from the installer or the environment, because nothing can be read before it. If a descriptor already exists and matches the plan, nothing is changed; if it exists and differs, the start is refused with `data_volume_already_initialised`. A volume is never re-created or overwritten.
  - *In the apply.* The step `storage.volume` (order 20, before every step that touches data) covers the `storage` section. It creates nothing: it runs the start guard and fails the apply when the mounted volume is not the one the plan names. Running it again changes nothing.
  - The volume is mounted at the plan's existing `database.dataRoot`, which must then be an absolute path. The section's keys: `storage.instanceClass` (`production` or `development`), `storage.volume.provider` (`bitlocker-vhdx`, `luks2` or `dev-plain`), `storage.volume.container`, `storage.volume.containerKind` (`file` or `device`), `storage.volume.maxGb`, `storage.volume.serviceAccount`. `dev-plain` is accepted only with class `development` and `DOTNET_ENVIRONMENT=Development` (section 8). A plan that leaves the section out asks for no volume; once the guard is registered in the host (section 12 item 4) such a host refuses to start, so the wizard and the shipped plans always fill it.
  - The identity KEK kind for the volume comes from the plan's `custody.identityKek`. Only `file` and `dpapi` can wrap the volume key today, so any other value is refused before anything is created; when the key is left out the server default applies. A named kind is also written to `config/volume.json` as `Bazaar:Identity:KekProvider`, so the host opens the same KEK on every later start.
  - *Which installs the guard covers.* The host registers the start guard and the `storage.volume` step when `Bazaar:Host:BootRoot` is set, the same rule the KEK location already follows. An install without a boot root predates this document and starts as before until it is moved with `storage migrate`. The guard becomes unconditional once the installer and the end-to-end test harness always pass a boot root and a plan with a `storage` section (section 12 item 4).
- **Existing installs.** `bazaar-server storage migrate`: stop the host, create the volume beside the old data root, copy with verification (file hashes; `pg_checksums` for the cluster), move the KEK file to the boot root, switch the data root to the mount, start, verify, then remove the plain copy with the same secure-removal step doc 61 uses. It is owner-started and audited, resumable, and leaves the old folder untouched until the verify passes. Its progress journal (`volume/migrate.json`: phases, paths and ids, no secrets) lives on the boot root. Any `postmaster.pid` in the old cluster, stale or not, and any link in the old root are refused. After the release that ships the guard, a host that finds a plain data root exits with `data_volume_not_encrypted` and prints this command.
- **Backups (doc 33).** The container file is not a backup unit (a copy of a live container is not consistent). Base backups, WAL archive and `pg_dump` output are staged inside the volume and leave it only as encrypted bundles (doc 48 section 6, doc 44). The descriptor is added to the recovery bundle.
- **PostgreSQL upgrades (doc 42 section 10.3)** run inside the same volume; scratch clusters (`PgScratchCluster`) are created under the data root, never in the system temp folder.
- **The owner's PC.** Boot root, container and tools on `V:` only (`V:\TOOL` for tools); nothing on `C:`.

## 11. Known limits
- The OS page file, swap, hibernation file and kernel crash dumps can hold database pages in clear. Setup warns when swap is not encrypted (Linux: detected; Windows: the page file is encrypted only when the system drive is). PostgreSQL core dumps are disabled.
- While mounted, the volume is readable by administrators and by the service account. File ACLs on the mount limit other accounts.
- With `unlock = auto` and a KEK that is not hardware-bound, whole-machine theft is not covered (section 1).
- Encryption costs throughput, more so on a host that already has whole-disk encryption. The PROPOSED budget is under 10% on `pgbench` against the plain folder; the first measurement was about 16% (section 3.1), so the budget is OPEN until the repeat runs.
- A VHDX attach does not survive a reboot by design; the volume unit attaches it at each start.

## 12. Work packages (PLANNED)
1. Spike D1. Windows half done (section 3.1). Remaining: repeat the `pgbench` comparison (warm caches, several runs), VHDX growth, and the Linux half (LUKS2 on a loop device with a stdin key, status from an unprivileged account, forced close under PostgreSQL, online growth).
2. `Bazaar.Server.DataVolume.Abstractions` and `Bazaar.Server.DataVolume`: descriptor, marker, key custody over `IKekProvider`, the guard, the fake and `dev-plain` providers, exit reasons. Tests with fakes.
3. KEK open-only at start, create only in setup; boot root (doc 14, doc 03).
4. Guard wired into the host start sequence and into `Bazaar.Components.ManagedPostgres` (path checks before start, tablespace and system-identifier checks after).
5. `Bazaar.Server.DataVolume.Linux` (`luks2`) with the systemd volume unit; CI job on the Linux runners (loop device).
6. `Bazaar.Server.DataVolume.Windows` (`bitlocker-vhdx`) with the `BazaarVolume` service; tests in category `Windows-host`.
7. Kodo probe and readiness role; audit entries.
8. Setup steps, dry-run and headless keys (doc 45); `storage migrate`.
9. `Bazaar.Server.ColumnSeal`: sealer, `column_dek`, blind index, column-class registry and its CI test.
10. Class B gaps, then class A, then class C, one table group per PR, each with its backfill job.
11. Move the existing feature ciphers onto `IColumnSealer`.
12. `owner` unlock mode.

## 13. Settings
| Key | Type | Default | Scope | Who | Approval |
|---|---|---|---|---|---|
| `storage.volume.provider` | enum: `auto`, `bitlocker-vhdx`, `luks2`, `dev-plain` | `auto` | install | owner, in setup | fixed after setup (change = migrate) |
| `storage.volume.container` | enum: `file`, `device` | `file` | install | owner, in setup | fixed after setup |
| `storage.volume.max_gb` | integer | hosting drive free space minus reserve | install | admin | audited |
| `storage.volume.grow_step_gb` | integer | 8 | install | admin | audited |
| `storage.volume.unlock` | enum: `auto`, `owner` | `auto` (DECIDED) | install | owner | Tier 2 |
| `storage.volume.recheck_seconds` | integer, 5 to 300 | 30 | install | admin | audited |
| `storage.external_db.at_rest` | enum: `refuse`, `attested` | `refuse` | install | owner | Tier 2 |
| `storage.column_seal.extra` | list of `table.column` | empty | install | admin | audited; adds columns to the sealed set, never removes |

Floors (not settings): a managed database always runs inside a verified encrypted volume on a production instance; the guard and the Kodo probe cannot be disabled; the cipher is never below AES-256-XTS; the class A, B and C sets can grow and never shrink; the host never creates keys, volumes or clusters at start.

## 14. Testing
Fakes: every refusal reason; descriptor edited (unwrap fails); marker missing, edited, from another install; data root replaced by a plain folder, by a symlink to a plain folder, by an empty mount point; KEK missing or zero bytes (no key is created, exit 78); tablespace or `pg_wal` outside the volume; volume locked while running (host stops). Real volumes: loop-device LUKS2 on the Linux runners (create, open with stdin key, wrong key, grow, dismount under load); BitLocker VHDX on the owner's PC (`Category=Windows-host`). Column seal: round trip; value moved to another row, column or table fails; erased person key leaves class B unreadable; no plaintext of a canary string in `pg_dump` output for sealed columns; the column-class registry test.

## 15. Questions for the owner
ANSWERED: What goes inside the encrypted volume: the whole data root (owner card, 2026-11-19).
ANSWERED: Unlock after a restart: automatic now, with owner unlock as a setting for later (owner card, 2026-11-19).

3. **External database mode.** Default: refused unless you record an attestation of how that server is encrypted; shown as "attested, not verified". Alternative: refuse always.
4. **Windows Home hosts.** Default: not supported for the server (no BitLocker management), stated at preflight. Alternative: add a second Windows provider later. No action needed unless you want Home supported.
