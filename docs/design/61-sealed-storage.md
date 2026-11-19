# Sealed storage: encryption and tamper detection for on-disk data

Status: DECIDED for the requirement (owner, 2026-11-19, message `cmsg_01MC1BkUhiVHzqr38uyxWzeHKwHGpbCxbmcQVzZBXGVhhR`); the mechanics below are DECIDED unless tagged otherwise.
Depends on: `14-identity-service-and-passport.md` (KEK, DEKs), `48-gap-decisions-security-identity.md` (sections 5, 6, 10), `07-soul-system.md`, `08-memory-system.md`, `18-vault.md`, `16-agent-vms-and-environments.md`
Module: `Bazaar.Server.SealedStorage` and `Bazaar.Server.SealedStorage.Abstractions` (component id `sealed-storage`)

## Owner requirement (DECIDED)
Owner, 2026-11-19: "soul data should be encrypted in their folders and shouldn't be able to be manually edited from the outside ... as well as any data that needs security like that."

Made concrete:
1. **No plaintext on disk.** Every file in the sealed set (inventory below) is stored only as authenticated ciphertext. The server writes no plaintext copy, temp file or cache of it outside process memory.
2. **Authenticated encryption under the KEK.** Each file is AES-256-GCM encrypted under a data key (DEK). DEKs are wrapped by the host KEK through `IKekProvider` (doc 14; DPAPI on Windows, protected key file elsewhere). This module does not implement a KEK, a secret store or a Vault.
3. **Edits from outside fail closed.** Any change to a sealed file or its store made outside Bazaar (edit, truncate, swap with another sealed file, restore of an older copy, deletion, unknown file added (reported, never read)) is detected on read. The read throws `SealTamperedException`, returns no data, writes an audit entry (`seal.tamper`, doc 48 section 5) and raises a Kodo health event. There is no "repair", "ignore" or "read anyway" mode. Recovery is restore from backup (doc 33) or an owner-approved reseal.
4. **Not a setting.** This is a security floor (doc 48 section 4.1 style): an operator may add stores to the sealed set, never remove the set below the inventory, and never turn off verification.

## What this does and does not protect
Protects against: someone with file-system access who is not Bazaar (a person editing a Soul charter in Notepad, a script, malware that can write but not call DPAPI as the service account, an agent VM escape that reaches the share, a stolen disk or copied data root on another machine).
Does not protect against: an attacker running as the Bazaar service account on the live host (the KEK is usable there; true for every provider), full memory compromise, or loss of the KEK (data is then unreadable by design; recovery bundle and backups are the answer, doc 48 section 6). Rollback of the whole data root to an older consistent snapshot is detected only when a counter anchor is configured (section 3).

## Inventory: what needs sealing
| Data | On disk where | Today | Decision |
|---|---|---|---|
| Soul charter, preferences, Soul version bodies, per-agent folder metadata (doc 07 mount) | Soul store and agent folders under the data root | Plain files or DB text | **Sealed set**, scope `soul:<soul-id>` |
| Agent private memory (doc 08, Vault area `agents/<id>/memory`) | Vault working copy | Plain Markdown in git | **Sealed set** for the working copy and index cache, scope `memory:<agent-id>`. The git history of the company Vault repo is an [OPEN] question (Q1). |
| Browser cookie profiles, Passport-backed profiles, OAuth token caches (doc 14, doc 16) | Host profile folders | Plain, or encrypted per doc 14 [PROPOSED] | **Sealed set**, scope `passport:<agent-id>`; doc 14 "cookie profile at rest" becomes this module |
| Bootstrap values: database credential, KEK reference (doc 03, doc 48 section 10) | Host config file | Plain | **Sealed set**, scope `host:bootstrap` |
| GitHub App private keys, provider keys, mail credentials | `ISecretStore` rows (envelope encrypted, doc 14) | Already encrypted | No change; on-disk copies, if any, move to the sealed set |
| KEK key file (`file` provider) | Data root | Protected file | No change here; Windows uses DPAPI. A tamper check on the key file is Q2. |
| Recovery bundles, backups, Soul exports | Backup target | Already encrypted (doc 48 section 6, doc 44 Argon2id) | No change. Soul export never includes memory (doc 54). |
| Audit log files | Postgres plus signed exports | Tamper-evident hash chain (doc 48 section 5) | No change; integrity, not secrecy |
| Postgres data directory | Data root | Plain pages | Encrypted volume, doc 63 (owner decision 2026-11-19: Bazaar encrypts the data folder at folder level). Sealed scopes under the data root sit inside that volume. Soul rows with secrets are already field-encrypted. |

## Design
**Scope and key.** A scope is a named group of files with one DEK (`soul:<id>`, `memory:<agent-id>`, `passport:<agent-id>`, `host:bootstrap`). The DEK is 32 random bytes, wrapped by `IKekProvider.WrapAsync` with associated data `bazaar.sealed.dek.v1|<scope>|<kekId>`, and stored in the scope's header file. **Crypto-shred:** deleting a scope's header destroys its data (matches doc 14, doc 48 section 9). **Rotation:** rewrap the DEK under a new KEK; files are not re-encrypted.

**File format v1.** Each sealed file is `magic "BZSL" | format byte 1 | generation (u64) | nonce (12) | ciphertext | tag (16)`. Cipher is AES-256-GCM with a fresh random nonce per write. Associated data is `bazaar.sealed.file.v1|<scope>|<logical-path>|<generation>|<format>`, so a file moved to another path or scope, or replaced by an older generation of itself, fails the tag check. The logical path is normalised (forward slashes, no `..`, no absolute paths, ordinal case compare) and is the same on every OS. Disk names are the logical path hashed (HMAC-SHA-256 under a key derived from the DEK), so file names leak nothing.

**Manifest.** Each scope has a sealed manifest listing every logical path with its generation and the SHA-256 of its stored bytes, and a manifest generation that only increases. Reading a file checks it against the manifest; listing a scope lists the manifest, not the folder. A manifest entry with no file, and a hash or generation mismatch, are tamper. A file on disk that the manifest does not know is never read or listed, and `VerifyAsync` reports it (`seal.unreferenced`). A write puts the new file under a new name (`<name-hmac>.<generation>.bzs`, never overwriting), then replaces the manifest by atomic rename (the commit point), then deletes the old generation. File generations come from the manifest counter, so a deleted and recreated path never reuses one. A crash before the commit leaves an unreferenced file that is never read; a crash after leaves a stale old file that is likewise never read. `VerifyAsync` reports both.

**Anti-rollback anchor.** The manifest generation and a manifest hash are published to an `ISealAnchor` after each write. Shipped anchors: the audit log (a checkpoint entry, doc 48 section 5.2, the default when Postgres is up) and a local counter file under a separate location (fallback for bootstrap and offline). At open, a manifest older than the anchor is tamper. An install with only the local anchor detects in-place edits and swaps, and rollback only on the same host.

**API (Abstractions).** `ISealedStore` (`OpenScopeAsync`), `ISealedScope` (`ReadAsync`, `WriteAsync` with expected generation, `DeleteAsync`, `ListAsync`, `ShredAsync`), `SealTamperedException`, `ISealAnchor`, `ISealAuditSink`. Writes carry the expected generation; a stale write is rejected, like `soul_versions` (doc 07). Reads return bytes or a stream; plaintext is held in pinned, zeroed buffers where the runtime allows.

**Known limits (from the 2026-11-19 security review).** The local counter-file anchor is not itself authenticated: deleting a scope's folder and its anchor file together looks like a scope that never existed. Detecting that needs an authenticated, monotonic index of scopes held in the audit-log anchor (WP6); until then the audit-log anchor is the recommended default when Postgres is up. A manifest with writes but no anchor, or a manifest missing next to other files, is already tamper. KEK rotation fails closed (a scope wrapped under another KEK id does not open); the rewrap path is WP9. A crash during shred can leave a scope that reports tamper until it is shredded again. Writers are serialised by a per-scope OS lock file and flushed to disk before each rename.

**Failure behaviour.** Missing KEK, wrong KEK, bad tag, bad magic, unknown format, manifest mismatch and anchor mismatch all throw and audit; none returns partial data. An unsealed (plaintext) file found where a sealed file is expected is tamper, not a legacy path. First-time migration of existing plain data is a separate, owner-started, audited import that seals and then securely removes the plaintext.

**Consumers.** Souls (`Bazaar.Server.Souls`, doc 07), Memory (`Bazaar.Server.Memory`), Identity/Passport and Host bootstrap call `ISealedStore` through their own modules. Adoption is one work package per consumer; each is its own PR and PROPOSED until merged. Nothing here puts feature code in Bazaar.Core, Bazaar.Server or Bazaar.Client.Core.

## Questions for the owner
1. Company Vault git history: seal the whole repo (git objects through a sealed object store; breaks external git tooling and GitHub push of plaintext) or keep the git remote as plain Markdown and seal only host working copies? Default until answered: seal working copies and the index only; nothing in the Vault repo changes. [OPEN]
2. Should the `file` KEK provider's key file also be sealed with an OS secret service where one exists (the keyring provider in `Bazaar.Secrets`)? Default: yes where available, else protected file. [PROPOSED]

## Work packages (PLANNED)
1. Module with scopes, file format, manifest and `SealTamperedException`; anchor and audit sink interfaces; tests (this change).
2. Souls adoption. 3. Memory working copy adoption. 4. Passport/cookie profile adoption (doc 14). 5. Host bootstrap values (doc 03). 6. Audit-log anchor implementation. 7. Plaintext import tool. 8. Kodo health event wiring. 9. KEK rotation rewrap and an authenticated scope index.

## Testing
Round trip; flipped bit anywhere; truncated file; file moved to another path and another scope; older generation restored; file deleted; unknown file added (reported, never read); manifest rolled back with anchor; wrong KEK; concurrent writers with stale generation; crash between file and manifest rename; no plaintext in any file under the root (byte search for a canary string); shred leaves nothing readable.
