# Packaged PostgreSQL: licence, notices and counsel questions (P10)

Status: DRAFT for counsel review. Not legal advice. Nothing here ships until counsel signs off and each **[UNVERIFIED]** item is confirmed from the upstream source.

## 1. What the bundle contains

| Component | Version target | Licence (as understood) | Status |
|---|---|---|---|
| PostgreSQL server, initdb, pg_ctl, pg_dump, pg_upgrade | 18 (default), 19 (opt-in) | PostgreSQL License | Stated in packaging.md section 5. Keep the copyright and permission notice in the bundle. |
| pgvector extension | per major | Stated as permissive in the research note; the licence name was not recorded. **[UNVERIFIED]** | Confirm the exact licence text from the pgvector repository before shipping. |
| Windows binaries (EDB zips or self-built) | per major | Same as PostgreSQL when built from source. **[UNVERIFIED]** for the EDB zips. | Confirm the zips may be redistributed as binaries, not only through the installer. |
| Visual C++ runtime (Windows) | as used by the build | Microsoft redistributable terms. **[UNVERIFIED]** | Confirm the redistribution terms for whichever runtime the binaries link against. |

`pg_search` is not bundled on Windows (no Windows build, research note). The Linux bundle may use it only if it is packaged separately and its licence is recorded here first.

## 2. Notices file the bundle must carry

The release pipeline places these files in `pg/<major>/` next to the binaries:

- `LICENSE-PostgreSQL.txt`: the PostgreSQL License text, with the copyright line from the PostgreSQL source tree for that major.
- `LICENSE-pgvector.txt`: the pgvector licence text, copied from the tagged release used for the build.
- `NOTICE.txt`: the bundle version, the upstream source URL and tag for each component, and the SHA-256 of each shipped binary.

Bazaar's own licence (AGPL-3.0 at the root, Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts`) does not change for the bundle. The bundle is a separate work with its own notices.

## 3. Obligations to satisfy at ship time

1. Keep the upstream copyright and permission notice for every bundled component, unmodified.
2. Say which components were modified. Current plan: none modified except build flags. If the pgvector build patches anything, list the patch.
3. Give the source location for each component, or the offer the licence requires. The upstream tag and URL in `NOTICE.txt` cover the PostgreSQL License case.
4. Do not present the bundle as the upstream project, and do not use its marks in product names.

## 4. Counsel question list

1. Is redistributing EDB Windows binary zips allowed, or must the bundle be built from source? (packaging.md section 5, **[UNVERIFIED]**)
2. Does the pgvector licence need anything beyond keeping its notice?
3. Which MSVC runtime redistribution terms apply to the Windows build?
4. Is the notices layout in section 2 enough, or does the installer need an on-screen notice?
5. Does shipping a managed child process of PostgreSQL inside an AGPL-3.0 product create any obligation beyond the notices above? (Expected answer: no, because it is a separate program, but confirm.)
6. Signing: the upstream binaries are unsigned. Does verifying by hash at install satisfy the policy, or must Bazaar sign the bundle?

## 5. Open items

- Confirm each **[UNVERIFIED]** item from the upstream repository, and record the answer here with the date and link.
- P7 (Windows bundle build script) writes `NOTICE.txt` with the real hashes. This file is the template, not the output.
