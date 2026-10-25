# Backup, disaster recovery and Soul portability for Bazaar (as of 2026-10-24)

Scope: PostgreSQL backup on a home NUC (Windows first, OS-agnostic), Hyper-V VM backup, secrets backup, RPO/RTO and a restore drill, Soul export/import format, Soul trust and privacy. Budget was about 19 tool calls, so several points remain unverified (see Gaps). Not legal advice.

Verification labels used below: **verified** = primary source read in this session; **secondary** = search-result summary or third-party page; **unverified** = my own knowledge or inference, not checked.

Internal context read: `/home/user/Bazaar/docs/deep-dives/Bazaar.Soul.md`. Relevant DECIDED items: Soul Passport must live outside the VM disk (section 2, differencing-disk gotcha); the VM backend is a provider (Hyper-V on Windows, QEMU/KVM on Linux); the server is OS-agnostic. Soul Marketplace is PROPOSED, not DECIDED.

---

## 1. PostgreSQL backup: method, Windows availability, pgvector

### Takeaway
Use `pg_basebackup` for base backups plus continuous WAL archiving for point-in-time recovery (PITR), and keep a nightly `pg_dump -Fc` as a logical, cross-version safety net. pgBackRest and Barman are not a clean fit on Windows, and WAL-G's official binaries are Linux-only, so the Windows-first path is the core PostgreSQL tools.

### Cited Findings
- PostgreSQL documents `pg_basebackup` as the easiest way to take a base backup, and says continuous archiving combines a file-system base backup with archived WAL so you can replay to a point in time. — [PostgreSQL 18 docs, Ch. 25.3 Continuous Archiving and PITR](https://www.postgresql.org/docs/current/continuous-archiving.html) — **verified** (page served PG 18 text)
- Required settings: `wal_level` at `replica` or higher, `archive_mode = on`, and `archive_command` (or `archive_library`). Example Unix command uses `test ! -f ... && cp %p ...`. — same source — **verified**
- "It is important that the archive command return zero exit status if and only if it succeeds." And: "set up and test your procedure for archiving WAL files *before* you take your first base backup." — same source — **verified**
- The docs state that `pg_dump` output "cannot be used as part of a continuous-archiving solution" because logical dumps "do not contain enough information to be used by WAL replay." — same source — **verified**
- Restore procedure: stop server, restore base backup with correct ownership, clear `pg_wal/`, copy saved WAL, set `restore_command`, create `recovery.signal`, start, then "Inspect the contents of the database." Advice to keep "several backup sets." — same source — **verified**
- Incremental `pg_basebackup --incremental` needs WAL summaries and all earlier backups it depends on to restore. — same source — **verified**
- `pg_dump` gives a consistent export under concurrent use and does not block readers or writers, but "except in simple cases, pg_dump is generally not the right choice for taking regular backups of production databases." Formats: plain (psql), custom `-Fc` (pg_restore, selective restore), directory (only format allowing `-j` parallel dump), tar. — [PostgreSQL 18 docs, pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html) — **verified**
- Security: restoring a dump runs arbitrary code chosen by the source superuser; inspect plain SQL (or `pg_restore --file`) before restoring untrusted dumps. Relevant to Soul-related DB exports. — same source — **verified**
- pgBackRest 2.60.0 user guide targets Debian/Ubuntu and says the commands are "the same on any Unix system." Repository backends include S3-compatible, GCS, Azure, SFTP, and local paths. The page does not mention Windows. — [pgBackRest user guide](https://pgbackrest.org/user-guide.html) — **verified** (text read; no Windows statement found in the first 100k characters)
- pgBackRest does not run on Windows: an October 2024 pgsql-general reply says so and links GitHub issue #2431; a 2020 maintainer comment says it was not yet ported. — [PostgreSQL mailing list thread](https://www.postgresql.org/message-id/1095977784.1286473.1682600395090%40office.mailbox.org) and related threads found via search — **secondary** (I did not open issue #2431)
- WAL-G: the project's readme offers prebuilt binaries for Linux AMD64 only; Windows appears only as a "development on windows" build section. — WAL-G docs search summary, [WAL-G PostgreSQL docs](https://wal-g.readthedocs.io/PostgreSQL/) — **secondary** (search summary; the readme itself was not fetched)
- Barman: Windows backup is "possible, but still experimental" per the official docs as quoted by a blog; streaming-only mode (pg_basebackup over replication, no SSH) opened it to Windows; rsync/SSH differential backups are not supported on Windows, so expect full backups only (2023 mailing-list note). — [Barman FAQ](https://pgbarman.org/faq/requirements) (the FAQ page itself is older and says Barman does not work on Windows) — **secondary**
- pgvector README: WAL-based replication and point-in-time recovery are supported ("pgvector uses the write-ahead log (WAL), which allows for replication and point-in-time recovery"). `vector` supports up to 16,000 dimensions; HNSW indexes on `vector` support up to 2,000 dimensions; `halfvec` up to 4,000 dimensions. HNSW has slower builds and more memory use than IVFFlat. Version shown: 0.8.7. — [pgvector README](https://github.com/pgvector/pgvector) — **verified**
- Backup/restore of pgvector data is not described separately in the README; the extension's data is ordinary table/index data covered by the same WAL and dump mechanisms (inference from the WAL statement and PostgreSQL docs above). — **inference**

### Inferences
- On Windows, the realistic stack is `pg_basebackup` (nightly, tar or plain) + `archive_command` writing WAL to a local folder + `pg_dump -Fc` nightly. Offsite copies come from a file-level tool (restic/Kopia/rclone), not from PostgreSQL tooling.
- If the team wants pgBackRest's features (incremental, verify, retention), run PostgreSQL on Linux (the provider model allows this) or run pgBackRest in a Linux VM against the Windows host's Postgres over the network. That is a design choice, not a Windows-compatible option.
- Embedding size (arithmetic, my assumption): a 1536-dim `float4` vector is 6,144 bytes raw. One million embeddings is about 6 GB before index overhead; an HNSW index adds more. The GC log text (say 1 KB per message) is about 1 GB per million messages. Mostly-append-only text plus embeddings should be tens of GB for a hobby company, not TB. These figures are unverified estimates, not benchmarks.
- Restore time for a few-GB database is dominated by HNSW index rebuild if indexes are rebuilt from a dump (pg_dump stores `CREATE INDEX`); a physical base backup restores indexes without rebuilding. Timings are unverified; measure on the NUC during the restore drill.

### Gaps
- No primary-source Windows support statement from pgBackRest or WAL-G was read; both are secondary.
- No measured timings for dump, restore, or index rebuild on the NUC. Sizing is estimated only.
- Barman's current Windows status is not confirmed from its current docs.

---

## 2. Offsite storage and tools (Backblaze B2, S3-compatible, restic, Kopia, rclone)

### Takeaway
At hobby scale (tens of GB), Backblaze B2 is cheap: at the listed rate, 50 GB costs about $0.35 per month. Restic is documented as supporting Windows, Linux and macOS, so it is a reasonable file-level backup tool for the base-backup and WAL archive directories; Kopia's platform list was not verified.

### Cited Findings
- Backblaze B2 storage: $6.95 per TB per month; egress free up to 3x average monthly storage, then $0.01/GB; first 10 GB free; Cloudflare-partner egress free. — [Backblaze cloud storage pricing](https://www.backblaze.com/cloud-storage/pricing) — **verified** (page read), but the page shows only "© 2024" and no pricing date, so **price currency as of 2026-10-24 is unverified**.
- Restic supports Windows, Linux, macOS and several BSDs. — [Restic (Wikipedia)](https://en.wikipedia.org/wiki/Restic) and [SlackBuilds restic listing](https://slackbuilds.org/repository/15.0/system/restic/) — **secondary**
- Restic's native backends (per its docs, mirrored on Gitee) include local directory, SFTP, REST server, and Amazon S3. — [Gitee restic mirror](https://gitee.com/efreets/restic) — **secondary**. Backblaze B2 as a restic backend: **unverified** (from memory; check restic docs).
- pgBackRest supports S3-compatible, GCS, Azure and SFTP repositories. — [pgBackRest user guide](https://pgbackrest.org/user-guide.html) — **verified**
- Kopia platform support: **not verified** (search returned nothing authoritative).
- rclone: not researched.

### Inferences
- Cost arithmetic (from the B2 rate above): 50 GB = $0.35/month, 200 GB = $1.39/month, 1 TB = $6.95/month, before any API or egress charges beyond the free allowance. Restore drills add egress; at 3x average storage free, a few restores per year stay free if storage is steady (inference from the stated allowance).
- Encrypt before upload. Restic encrypts repositories by design (secondary/unverified detail); a PostgreSQL base backup or dump must be encrypted by the backup tool or a wrapper, since PostgreSQL does not encrypt backups itself (inference).

### Gaps
- Kopia and rclone Windows support not verified.
- Current B2 price and whether the 3x egress allowance applies unchanged in 2026 are unverified.

---

## 3. Hyper-V VM backup (checkpoints vs exports vs VHDX copy)

### Takeaway
Microsoft's Hyper-V checkpoint and backup pages could not be fetched (both returned 404), so the Microsoft guidance here is unverified. Bazaar's own design already removes most of the risk: Soul data lives outside the VM disk, so the VM can be recreated from the golden image, and only the small child disks plus host-side Soul data need backup.

### Cited Findings
- Internal design: one golden parent disk with per-agent differencing child disks; changing the parent invalidates children; Soul data must live outside the VM disk. — `/home/user/Bazaar/docs/deep-dives/Bazaar.Soul.md`, section 2 — **verified** (internal doc, DECIDED)
- The Soul doc notes the golden image cannot change once children exist without a deliberate rebase. — same — **verified**
- Microsoft Hyper-V checkpoint documentation: URL `https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/hyper-v-checkpoints` returned HTTP 404. — **unverified**
- Microsoft Hyper-V backup and recovery documentation: URL `https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/hyper-v-backup-and-recovery` returned HTTP 404. — **unverified**

### Inferences (general Hyper-V knowledge, **unverified**, check before relying)
- Checkpoints are not a backup: they chain differencing disks (avhdx) on top of the disk and depend on the parent; a broken chain loses data. Use checkpoints only for short-term rollback.
- Export-VM (or a stopped-VM copy of the VM configuration and VHDX files) is the simplest full backup of a child disk plus its parent. A child disk without its parent cannot boot.
- Practical plan (inference): back up the golden parent VHDX once per toolchain version (it is large but changes rarely), back up each child VHDX only if it holds state worth keeping, and treat Soul data (host-side folder/DB) as the primary backup target. Rebuilding an agent = new child off the golden parent + restore Soul data + mount the scoped folder.

### Gaps
- No primary Microsoft statement on checkpoint/export behaviour was read.
- Whether VSS-based backup of a running Server Core guest is needed is unverified.
- Nested WSL2 disk state (inside the VM) is not covered by any source read here.

---

## 4. Secrets: encrypting backups, key escrow, lost master key

### Takeaway
The Soul Passport (credentials, tokens, cookies) must be encrypted at rest with a master key that is separate from the backup repository. Losing the master key makes encrypted backups unreadable; keeping it only next to the backups defeats the encryption. Escrow the key offline. No primary source on key-escrow tooling was read, so specific tool choices are unverified.

### Cited Findings
- Passport must be outside the VM disk. — `/home/user/Bazaar/docs/deep-dives/Bazaar.Soul.md` section 1 and 2 — **verified** (internal, DECIDED)
- Restic repositories are encrypted; Kopia encrypts repositories (Kopia encryption detail not verified). — [Restic (Wikipedia)](https://en.wikipedia.org/wiki/Restic) — **secondary** for restic only

### Inferences
- Two keys: (a) a repository key used by the backup tool (restic/Kopia password), and (b) an application master key that encrypts the Passport in the database. Store (a) and (b) in different places. Print or hold an offline copy of both (e.g., a password manager with a second person's access, or a sealed envelope off-site). If (b) is lost, the Passport must be re-issued: every GitHub App key, OAuth grant, email credential and API key must be rotated. Soul memory and history are still recoverable.
- Crypto-shredding (see section 6) also depends on key management: destroying a key erases data, so keys must be backed up with the same care.

### Gaps
- Key escrow tools (age, sops, KeePass, Windows DPAPI interaction) not researched. DPAPI behaviour for a Windows Service account is **unverified**.
- Whether GitHub App private keys can be re-generated without disruption: not checked here.

---

## 5. Recovery objectives and restore drill

### Takeaway
For a hobby deployment, a practical target is RPO about 1 hour for the database (WAL archive with `archive_timeout`) and RTO about 1 day for a full NUC rebuild. These targets are my recommendation, not sourced; the key inputs (WAL archive frequency, restore timing) must be measured.

### Cited Findings
- `archive_timeout` is a PostgreSQL setting that forces a WAL segment switch so that archived WAL is not stale. — PostgreSQL docs, section 25.3 (the archive_timeout parameter lives in the WAL configuration reference, not read here) — **unverified in detail**; the continuous-archiving page confirms archiving as the mechanism — **verified**
- WAL archiving requires a test before the first base backup. — [PostgreSQL continuous archiving](https://www.postgresql.org/docs/current/continuous-archiving.html) — **verified**

### Inferences
- RPO: WAL archiving to an offsite copy every hour (or sooner) gives about 1 hour of loss at worst; losing the NUC without offsite WAL gives the age of the last shipped segment.
- RTO: rebuild the NUC (Windows Server Core, Hyper-V role, PostgreSQL, Garnet/Valkey as per provider), restore PostgreSQL base backup + WAL, recreate golden image from the toolchain script, restore Soul folders, re-issue Passport if the master key is lost. Estimate one working day; measure in the drill.

### Restore drill checklist (proposed)
1. Quarterly: restore the latest base backup + WAL into a scratch VM or a second PostgreSQL instance on another machine. Confirm the database starts and that row counts for the GC log, memory and Kanban match the source at a fixed timestamp.
2. Check that the latest WAL segment is at most the RPO old (`pg_stat_archiver` view or the archive folder timestamps).
3. Restore `pg_dump -Fc` into a scratch database with `pg_restore`; run the pgvector similarity query for a known memory and confirm results.
4. Rebuild one agent: create a child disk from the golden parent, mount the Soul folder, start the agent, confirm it reads its memories and Passport.
5. Test key recovery: decrypt the Passport using the offline master key copy, not the server's.
6. Record elapsed times for each step to refine RTO; record byte sizes for sizing.
7. Confirm offsite restore uses only the credentials stored offsite (not the NUC's), and that the backup repository password works from a clean machine.

### Gaps
- No measured timings. Targets unverified.

---

## 6. GDPR-style erasure in an append-only GC log

### Takeaway
GDPR Article 17 gives erasure rights, but Article 17(3) has exceptions (freedom of expression; archiving in the public interest or research where erasure would seriously impair those purposes). Crypto-shredding (per-person encryption keys, destroyed on request) is the common engineering pattern for append-only logs; vendor and blog sources support it technically, and no court decision on its legal sufficiency was found. Get counsel's view.

### Cited Findings
- Article 17(1) grounds include: personal data "no longer necessary in relation to the purposes," consent withdrawn, and unlawful processing. Article 17(3)(a) excludes exercise of freedom of expression; 17(3)(d) covers archiving, research and statistics where erasure would make those aims impossible or seriously impair them. — [GDPR Article 17 (gdpr-info.eu)](https://gdpr-info.eu/art-17-gdpr/) — **verified** (text read); the page did not include Article 5 text, so storage-limitation and accuracy principles were not read — **unverified**
- Crypto-shredding: encrypt each data subject's personal fields under a per-subject key, then "forget" them by destroying the key; vendor docs describe envelope encryption with keys in a KMS and warn to tombstone every key copy. — [Kurrent blog on crypto shredding in event-sourced systems](https://kurrent.io/blog/protecting-sensitive-data-in-event-sourced-systems-with-crypto-shredding-1) and [Conduktor crypto-shredding (Kafka)](https://www.conduktor.io/glossary/crypto-shredding-for-kafka) — **secondary** (vendor/blog)
- A commentary on transparency logs states it could not find a court decision confirming crypto erasure is legally sufficient. — linked from search results ([elixirforum thread on auditability vs crypto-shredding](https://elixirforum.com/t/balancing-auditability-and-privacy-with-crypto-shredding/73125)) — **secondary**
- July 2026 Technical Disclosure Commons paper describes an atomic crypto-shred that keeps an append-only audit ledger with a completeness check; it is a disclosure, not a standard. — [TDCommons 10873](https://www.tdcommons.org/dpubs_series/10873) — **secondary**, unverified in detail

### Inferences
- For GCs: keep the log append-only, and store personal fields (names, contact details, free-text about a person) as encrypted per-person payloads referenced by ID. An erasure request destroys that person's key and writes a tombstone event. Message structure, timestamps and authorship stay, which keeps the log's integrity while removing identifiability. Whether the remaining structure is still personal data is a legal question.
- Backups keep old keys until they expire; the erasure must also purge key copies in backups (or the retention period must be documented). This is a real gap in the crypto-shred model.
- The GC log's agent-to-agent messages may contain third-party personal data; design the schema so that a per-person key covers every field that names a person.

### Gaps
- No EDPB or ICO guidance on append-only logs or crypto-shredding was read.
- Other append-only systems (e.g., Git history rewriting, ledger "redaction" features) were not researched.

---

## 7. Soul export/import format

### 7a. Existing portable agent formats

#### Takeaway
No existing standard covers a full Soul (identity, memory, reputation, accounts). Agent Skills (SKILL.md) fits the skills part well and is simple. Open Agent Specification fits an agent definition (declarative workflow) but is not a backup format. A2A Agent Cards are discovery metadata, not identity. MCP covers tools and context, not identity. AGENTS.md was not researched.

#### Cited Findings
- Agent Skills: a skill is a directory with a required `SKILL.md`; frontmatter `name` (max 64 chars, lowercase a-z, 0-9, hyphen, must match directory name) and `description` (1–1024 chars). Optional: `license`, `compatibility` (≤500 chars), `metadata` (string map), `allowed-tools` (experimental, space-separated). Optional folders: `scripts/`, `references/`, `assets/`. The spec's text read did not include a security or trust section. — [Agent Skills specification](https://agentskills.io/specification) — **verified**
- Agent Skills: "Scripts should be self-contained or clearly document dependencies"; supported languages "depend on the agent implementation." — same — **verified**
- Open Agent Specification (Oracle): declarative, framework-agnostic format for agents and workflows; tech report arXiv 2510.04173 (October 2025); Python and TypeScript SDKs and adapters for LangGraph, CrewAI, AutoGen; ACM CAIS 2026 paper. — [arXiv 2510.04173](https://arxiv.org/html/2510.04173v2) and [CAIS 2026 paper](https://caisconf.org/program/2026/papers/open-agent-specification-a-unified-representation-for-ai-agents/) — **secondary** (search summaries; the arXiv paper was not read)
- A2A v1.0.0 spec: an Agent Card is "A JSON metadata document published by an A2A Server, describing its identity, capabilities, skills." Section 8.4 covers "Agent Card Signing" (canonicalization, signature format, verification); section 13.3 covers extended-card access control. Body text of 8.4 and 13.3 was not read. — [A2A specification](https://a2a-protocol.org/latest/specification/) — **verified** for the definition; signature details **unverified**
- MCP and AGENTS.md: not researched in this session.

#### Inferences
- Use Agent Skills as the on-disk format for skills in a Soul bundle (the directory-plus-SKILL.md shape is the closest thing to a portable standard).
- Use a Bazaar-specific manifest (JSON) for everything else: schema version, Soul identity fields, personality document, model whitelist, skill list, memory export, signatures. Map the agent-facing part to an A2A Agent Card only if Bazaar exposes agents over A2A.
- Do not use an Agent Card as the Soul format: it describes a running service's identity, and a Soul is a data bundle.

### 7b. What to include and exclude

#### Takeaway
Export the role and the working knowledge; exclude everything tied to credentials, the company, or other people. Memory is the risky part: share only curated, anonymised, reviewed items.

#### Recommended contents (inference from the Soul doc's definitions)
- Include: personality and role document (Markdown), skill packages (Agent Skills format), toolchain manifest (golden-image version, not disk), per-agent model whitelist (no global `auto/*`, per DECIDED), learned procedures, and curated memories that have passed a review step.
- Exclude: Soul Passport (credentials, GitHub App keys, OAuth grants, browser sessions, cookies, API keys, SSH keys, MFA metadata) — hard exclude; VM disks; GC messages; company Vault content; Kanban items; raw embeddings (they can carry source text; this risk is general knowledge, **unverified** for this case); reputation scores tied to a company (they are company-specific judgments; export only as a self-declared summary, flagged as unverified); the agent's private memories about named people.

### 7c. Versioning and migration

#### Takeaway
Use a manifest with an integer schema major version, semver for the bundle, ordered migrations, and reject unknown major versions on import. These are standard practices, not sourced from the research here.

#### Inferences
- `manifest.json` holds `schemaVersion` (major.minor), creator tool version, and a list of files with SHA-256 digests. Importers reject a higher major version and apply a migration chain for older ones.
- Preserve unknown fields in a "extensions" map so future fields survive a round trip.
- Keep the Agent Skills `metadata.version` for skills separately.

### 7d. Signing and provenance

#### Takeaway
A detached signature over the manifest (with per-file digests) gives integrity and a provenance claim. Tie the signer to a GitHub App identity (DECIDED for agent identity). Tool choice (Sigstore, minisign, or an Ed25519 signature) was not verified.

#### Cited Findings
- A2A defines an Agent Card signing section (8.4) with canonicalization requirements and signature verification. — [A2A specification](https://a2a-protocol.org/latest/specification/) — **verified** (headings only)
- GitHub App identity is DECIDED for Bazaar agents (`/home/user/Bazaar/docs/deep-dives/Bazaar.Soul.md` and Bazaar.md). — **verified** (internal)

#### Inferences
- A signature proves who published a Soul and that it has not changed since; it does not prove the content is safe. Keep the two claims separate in the UI.
- Signing key compromise: use revocation lists in the manifest and a key-rotation field.

### 7e. Risks of importing an untrusted Soul, and mitigations

#### Takeaway
An imported Soul is external content that can change agent behaviour. Treat the personality, memories and skills as untrusted input, and never auto-run scripts or auto-grant tools or models.

#### Cited Findings
- OWASP LLM01:2025: prompt injection occurs when inputs alter model behaviour; indirect injection comes from external content the model ingests such as files; no foolproof prevention is known. Mitigations listed: constrain model behaviour; validate output formats; input and output filtering; least privilege; human approval for high-risk actions; "Segregate and identify external content"; adversarial testing. — [OWASP LLM01:2025](https://genai.owasp.org/llmrisk/llm01-prompt-injection/) — **verified**
- Agent Skills `scripts/` contains executable code; `allowed-tools` pre-approves tools and is experimental. — [Agent Skills specification](https://agentskills.io/specification) — **verified**

#### Risks and mitigations (inference unless noted)
- Prompt injection in personality or memory text: quarantine on import; store imported memories in a namespace flagged as external (OWASP "segregate and identify external content"); never let imported text edit the system prompt without a human diff review.
- Malicious skill scripts: no execution on import; run only in the agent VM after review; signature check first.
- Privilege escalation via `allowed-tools` or model whitelist: ignore imported grants; re-derive permissions from the company's own policy (DECIDED: per-agent model whitelists).
- Reputation spoofing: drop imported reputation, or label it self-declared.
- Credential exfiltration: the Passport is never in the bundle; scan the bundle for secret patterns before export.
- Data-leak through memory: review and anonymise before export; a content scanner is useful but not sufficient.
- Human approval for high-risk actions, per OWASP.

### 7f. Licensing of shared Souls

#### Takeaway
Bazaar's code licence (AGPL-3.0; Apache-2.0 for SDK and contracts) does not decide the licence of a Soul's content. Each Soul bundle needs its own licence field. Choice of licence is for the owner and counsel.

#### Cited Findings
- Agent Skills includes an optional `license` field, "a license name or reference to a bundled license file." — [Agent Skills specification](https://agentskills.io/specification) — **verified**
- Bazaar licence direction (AGPL-3.0 server/clients; Apache-2.0 SDK/contracts; DCO vs CLA open). — `/home/user/Bazaar/CLAUDE.md` decisions list — **verified** (internal)

#### Inferences
- Put a `license` field in the Soul manifest, default to "no licence granted" until the author chooses one, and state that personality text and memories may need separate licence terms.

### Gaps
- No legal review. Not legal advice.

---

## 8. Privacy summary for Soul sharing

- Pre-share review pipeline (inference): (1) strip the Passport and secrets by pattern and by schema; (2) redact named people and contact details from memories; (3) human review of the final bundle; (4) record what was removed in the manifest (count only).
- Redaction does not guarantee anonymity. Memories that describe unique events can still identify people. Treat anonymisation as a risk reduction, not a guarantee (inference).
- Erasure after sharing: a shared Soul is a copy in another install. Bazaar cannot erase it under the GDPR model; the sharing terms should say so (inference; legal view needed).

---

## Recommendation (summary)

1. Postgres (Windows host): `pg_basebackup` nightly + continuous WAL archiving to a local folder with `archive_command` tested before the first base backup; nightly `pg_dump -Fc` as a logical copy; ship base + WAL + dumps offsite with restic (Windows-supported per secondary sources) to B2 or any S3-compatible store. If Linux becomes the DB host, evaluate pgBackRest.
2. VMs: back up the golden parent once per toolchain version and Soul data as the primary target; do not rely on checkpoints for backup.
3. Keys: two keys (backup repo, application master key for the Passport), stored offline in separate places; drill the recovery.
4. Soul bundle: directory + `manifest.json` (schema version, file digests, signature, licence, exclusions list). Skills as Agent Skills folders. Exclude Passport, VM disks, GC logs, Vault, Kanban, raw embeddings, company reputation. Import into quarantine; never auto-run scripts or grant tools; require human approval.
5. GDPR: design the GC log with per-person encryption keys (crypto-shred) and document that backups keep old keys until expiry; get counsel's view.

---

## Unverified or not researched (summary)
- Hyper-V Microsoft docs (both URLs 404).
- WAL-G and pgBackRest Windows status: secondary only.
- Barman Windows status (secondary, not current docs).
- Kopia platform support; rclone; restic B2 backend.
- Backblaze price currency for 2026 (page dated "© 2024").
- Open Agent Specification details (secondary).
- A2A signature details (headings only).
- MCP and AGENTS.md (not researched).
- GDPR Article 5 text, EDPB/ICO guidance on append-only logs and crypto-shredding.
- All timings and sizes beyond arithmetic from stated assumptions.
- Key-escrow tooling and DPAPI behaviour.
