# 47. Licensing, contributions and trademarks

Status: DECIDED for the owner decision of 2026-11-13 (section 3), including the SDK and Contracts licence: AGPL-3.0 everywhere (M1). Follow-up 2026-11-13: `src/Bazaar.GC.Abstractions` also moved to AGPL-3.0-only (owner choice, relayed by the coordinator), with its own LICENSE copy and csproj field. OPEN for the repo mismatches in section 4 that do not follow from M1 (M2 to M8 follow M1 and are confirmed in their own small PRs). PROPOSED for the module exception wording (section 6) and the CLA text outline (section 7). PLANNED for everything not yet built (marketplace checks, trademark policy, CLA bot).

This document is a product and engineering design. It is not legal advice. Counsel review is required before the first public release, and every legal text in this file is a draft for counsel.

Depends on: `CLAUDE.md` (decisions in force), `docs/design/DECISIONS-LOG.md`, `docs/design/37-distribution-matrix-and-packaging.md`, `docs/research/notes/Bazaar server packaging/licences.md` (bundled PostgreSQL notices), `docs/research/notes/Bazaar stack feasibility research/cicd_repo_licensing.md` (licence research). Used by: the marketplace design (module licence checks), the module API (doc 20), the release pipeline (SBOM and signed releases).

## 1. Scope

This doc covers:

- the licence set for the platform, its SDK and contracts, and modules;
- the commercial (paid) licence and the dual-licence model;
- the module exception that lets modules use the public module API under any licence;
- contribution terms: DCO for modules and small fixes, a CLA for core code;
- the trademark policy, the marks to clear and register, and the naming rules;
- marketplace licence rules and the dependency compatibility rule;
- the counsel review checklist.

It does not decide the licence of any third-party engine plugin, or the terms of any particular commercial offer.

## 2. Current state (checked in the repo, 2026-11-13)

Every path below is relative to the repo root. Quotes are copied from the files as they stand on the checked branch. Nothing here was assumed.

| Path | What it actually says | Notes |
|---|---|---|
| `LICENSE` | Full text of GNU AGPL, Version 3, 19 November 2007 (first line: `GNU AFFERO GENERAL PUBLIC LICENSE`). | Standard FSF text. The file carries no Bazaar copyright line; the copyright notice lives in `NOTICE`. |
| `NOTICE` | `Copyright (c) 2026 The Bazaar Authors`. Says the repo "uses more than one licence, split by directory": the repo as a whole is AGPL-3.0; `src/Bazaar.Sdk/` and `src/Bazaar.Contracts/` are "licensed under the Apache License, version 2.0". Says "A file is under the licence of the directory it sits in, unless the file itself says otherwise." Status: "pending review by counsel. It may change before the first public release." | Matches the 2026-10-25 decision, not the 2026-11-13 decision. No module exception, no commercial licence, no trademark text. |
| `src/Bazaar.Sdk/LICENSE` | Full text of the Apache License, Version 2.0, January 2004. | Conflicts with the 2026-11-13 decision (M1). |
| `src/Bazaar.Contracts/LICENSE` | Full text of the Apache License, Version 2.0, January 2004. | Conflicts with the 2026-11-13 decision (M1). |
| `CONTRIBUTING.md`, "Sign-off (DCO)" | DCO only. Contributors sign off with `git commit -s`. Squash-merges carry a `Signed-off-by:` line that the sponsor adds to the PR description. "Do not add that line on someone else's behalf." | No CLA. Sign-off is sponsor-based for PRs (M4). |
| `CONTRIBUTING.md`, "Licence" | "you agree that your contribution is licensed under the licence of the directory it changes: GNU AGPL-3.0 for most of the repository, and Apache-2.0 for `src/Bazaar.Sdk/` and `src/Bazaar.Contracts/`. ... The licensing is pending counsel review." | Directory rule, no CLA, no module terms (M5). |
| `README.md` (lines 29 and 30) | AGPL-3.0 for the repository; SDK and contracts "are licensed under the Apache License, version 2.0. Each directory has its own `LICENSE` file." | Public-facing copy of the same split (M1). |
| `AUTHORS` | "Human contributors are listed below by GitHub handle." Initial author listed by handle as project owner. Agents are "disclosed in the git history through `Co-Authored-By:` trailers". | No conflict. A CLA process needs a contributor record (M12). |
| `Directory.Build.props` (line 17) | `<PackageLicenseExpression>AGPL-3.0-only</PackageLicenseExpression>` | SPDX choice of "-only" versus "-or-later" is not recorded anywhere (M6). |
| `src/Bazaar.Sdk/Bazaar.Sdk.csproj` (line 4) | `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>` | Conflicts with M1. |
| `src/Bazaar.Contracts/Bazaar.Contracts.csproj` (line 4) | `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>` | Conflicts with M1. |
| `packaging/scoop/bazaar-client.json` (line 5) | `"license": "AGPL-3.0-only"` | Matches the root licence. |
| `CLAUDE.md` (line 14) | "Licences (owner, 2026-10-25): AGPL-3.0 at the root (server and clients), Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts`; copyright "The Bazaar Authors". Counsel review before the first public release." | The date in the file is 2026-10-25. The owner brief for this task said 2025-10-25; the file is taken as correct (M7). |
| `docs/research/notes/Bazaar server packaging/licences.md` (section 2) | "Bazaar's own licence (AGPL-3.0 at the root, Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts`) does not change for the bundle." | Follows M1. |
| `docs/research/notes/Bazaar stack feasibility research/cicd_repo_licensing.md` (line 153) | Research recommendation: "AGPL-3.0 for the server and client code if the hosted-fork concern is central; Apache-2.0 for `Bazaar.Sdk` and ..." | Research input only, not a decision. Records the DCO versus CLA trade-off (line 154). |

Searched and not found in the repo's Markdown and config files: a CLA text, a module exception, a commercial licence, a trademark policy, a dependency compatibility rule, and the words "module exception". The search covered `CLA`, `Apache`, `AGPL`, `license`, and `Contributor`.

## 3. Owner decision of 2026-11-13 (DECIDED)

1. **One licence for all repos: GNU AGPL-3.0**, plus **paid commercial licences**. The model is dual licensing: the AGPL text is the public licence, and a commercial licence is sold to parties who cannot or will not accept AGPL terms.
2. **Module exception.** The AGPL core carries a written module exception. Intent: modules that use the public module API may be under any licence, including closed commercial licences. The exception exists so that the extension ecosystem is not forced into AGPL. The core stays AGPL. See section 6 for draft wording.
3. **Contributions.** DCO sign-off for modules and small fixes. A CLA for core code. The CLA keeps the right to relicense and to offer commercial licences. It applies to extracted repos too (see DECISIONS in `docs/design/DECISIONS-LOG.md`, the extraction plan).
4. **Names and marks.** A published trademark policy. Core marks (Bazaar, CDAX, key module names) are registered after clearance searches. "Codex" and "RUBEDO" need clearance first.
5. **Marketplace.** Open-source and commercial or paid modules are both listed. Licence-compatibility checks run at install. The module's licence is shown to the user.
6. **Dependencies must be AGPL-compatible.** See section 8.
7. **Counsel review** before public release. Not legal advice.
8. **Update 2026-11-13: SDK and Contracts (item 1 above, M1 DECIDED).** `Bazaar.Sdk` and `Bazaar.Contracts` are AGPL-3.0, the same as the rest of the repo. The `PackageLicenseExpression` in their csproj files and in `Directory.Build.props` is `AGPL-3.0-only`, and the per-directory `LICENSE` files and `NOTICE` are AGPL-3.0. Section 4 rows M3 and M6 follow from this; the owner's choice of `-only` or `-or-later` (M6) is still OPEN for counsel. The working tree already carries some of these edits. Each file change is checked in its own PR.
9. **Update 2026-11-13: commercial licence pricing is decided later.** Until customers exist, the offer is AGPL-3.0 plus "contact us". The CLA (section 7) keeps the option of dual licensing open. This document gives no prices and no order-form terms. Counsel drafts the commercial licence when pricing is decided (M10).

## 4. Mismatch table

Each row is a place where the repo disagrees with section 3. Status is OPEN until the owner picks an option. No row is resolved by this document. Changes listed in section 5 happen only after the owner confirms.

| ID | Where | Repo says | Decision says | Status | Options |
|---|---|---|---|---|---|
| M1 | `src/Bazaar.Sdk/LICENSE`, `src/Bazaar.Contracts/LICENSE`, `NOTICE`, `README.md` lines 29 to 30, `src/Bazaar.Sdk/Bazaar.Sdk.csproj` and `src/Bazaar.Contracts/Bazaar.Contracts.csproj` (`PackageLicenseExpression`), `CLAUDE.md`, the packaging research note | Apache-2.0 for the SDK and contracts. | AGPL-3.0 across all repos. | DECIDED (owner, 2026-11-13): AGPL-3.0 for `Bazaar.Sdk` and `Bazaar.Contracts`, as for every repo. Apache-2.0 is retired for those two packages. The repo files that still say Apache-2.0 follow in small PRs (section 5). | **A.** Keep Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts` as the exception to "AGPL everywhere". Record it as a second, owner-decided exception in the DECISIONS-LOG. The module exception then needs to cover only the AGPL core. **B.** Move the SDK and contracts to AGPL-3.0, as the 2026-11-13 decision reads. Module authors then rely on the module exception for the public API, so the exception must name the SDK and contracts packages explicitly. |
| M2 | `NOTICE` | Per-directory split. Pending counsel. No module exception, no commercial licence, no trademark note. | One AGPL licence plus module exception plus commercial licence. | OPEN, depends on M1. | **A.** Rewrite `NOTICE` to one licence, the module exception pointer, the commercial licence pointer, the trademark policy pointer and the third-party notice section. **B.** Keep the split for the SDK and contracts only, and add the other pointers. Choose after M1. |
| M3 | `src/Bazaar.Sdk/LICENSE`, `src/Bazaar.Contracts/LICENSE` | Full Apache-2.0 text. | Depends on M1. | OPEN, depends on M1. | **A.** Keep the Apache text. **B.** Replace with the AGPL-3.0 text (copy of root `LICENSE`) and a short header naming the module exception. |
| M4 | `CONTRIBUTING.md`, "Sign-off (DCO)" | DCO only. Sponsor adds `Signed-off-by:` to the PR description. | DCO for modules and small fixes, CLA for core code. | OPEN: the mechanism is not decided. | **A.** CLA signed once per contributor through a CI status check (`cla`) that blocks merge until signed. The DCO check stays on every commit for modules and small fixes. **B.** CLA accepted by a signed attestation in the PR template, with a signed copy kept by the owner. Option A is the usual pattern and is easier to audit. Either way, counsel reviews the wording and where signatures are stored (see section 12). |
| M5 | `CONTRIBUTING.md`, "Licence"; `NOTICE` "A file is under the licence of the directory it sits in" | Licence follows the directory. | AGPL for the core. Modules keep their own licence under the module exception. | OPEN: how module directories are licensed. | **A.** Drop the directory rule. Each file is AGPL unless its header or its module manifest says otherwise, and the module exception is written once. **B.** Keep the directory rule and add a module directory licence table that names allowed licences per module tree. |
| M6 | `Directory.Build.props` line 17 (`AGPL-3.0-only`), `packaging/scoop/bazaar-client.json` | The SPDX form is "-only". | "AGPL-3.0". No version-suffix choice recorded. | OPEN (minor). | **A.** `AGPL-3.0-only`, the current value. Later versions need a new decision. **B.** `AGPL-3.0-or-later`, which lets future FSF versions apply. Counsel should review both. The choice also affects which dependencies are compatible (section 8). |
| M7 | `CLAUDE.md` line 14 date | Dated 2026-10-25. | The brief for this task called the same decision "2025-10-25". | OPEN (informational). | Confirm the date. This document uses 2026-10-25 because that is the date written in `CLAUDE.md`. |
| M8 | `docs/research/notes/Bazaar server packaging/licences.md` section 2 | Says the Bazaar licence "does not change for the bundle" and lists Apache-2.0 for the SDK and contracts. | Follows the chosen M1 option. | OPEN, follows M1. | Update the note after M1. |
| M9 | Whole repo | No module exception text. | Module exception required. | GAP, not a conflict. Wording in section 6 is PROPOSED for counsel. | Owner approves the intent (section 3, item 2). Counsel approves the wording. |
| M10 | Whole repo | No commercial licence text or offer. | Paid commercial licences. | GAP, not a conflict. PLANNED. | Owner picks the commercial licence form (a short order form with a licence grant, or a full negotiated contract). Counsel drafts. |
| M11 | Whole repo | No trademark policy. | Published trademark policy. | GAP. PLANNED. Draft outline in section 9. | Owner approves the outline, counsel drafts. |
| M12 | `AUTHORS` | Lists human contributors by GitHub handle. | CLA needs a contributor record. | OPEN (minor). | **A.** `AUTHORS` stays a human-readable list, and the CLA register is kept by the CLA service, not in git. **B.** CLA signers are listed in `AUTHORS` after signing. Option A keeps personal details out of the public repo. |
| M13 | `NOTICE` "Third-party software" | Says third-party notices will be added as dependencies are added. | Dependencies must be AGPL-compatible (section 8). | GAP. PLANNED. | The SBOM job (section 8) generates the notice list. Owner approves the allowlist. |

## 5. Files that change when the owner confirms (not done here)

This list is for the owner to read. This document does not change any of these files. Each change happens in its own small PR with a test or check where one applies.

1. `LICENSE` (root): no change expected. Confirm the file is the AGPL-3.0 text. Keep it unmodified (FSF text rule).
2. `NOTICE`: rewrite per M2. Add the copyright line, the module exception pointer, the commercial licence pointer, the trademark policy pointer, and the third-party notice section.
3. `src/Bazaar.Sdk/LICENSE` and `src/Bazaar.Contracts/LICENSE`: per M1 and M3. Option A keeps them. Option B replaces them with the AGPL text.
4. `src/Bazaar.Sdk/Bazaar.Sdk.csproj` and `src/Bazaar.Contracts/Bazaar.Contracts.csproj`: `PackageLicenseExpression` follows M1 (`Apache-2.0` for A, `AGPL-3.0-only` or `AGPL-3.0-or-later` for B per M6).
5. `Directory.Build.props`: per M6.
6. `packaging/scoop/bazaar-client.json`: per M6.
7. `README.md` lines 29 to 30: per M1 and M2.
8. `CONTRIBUTING.md`: "Sign-off (DCO)" section gets the DCO-for-modules-and-small-fixes rule and the CLA section (per M4). The "Licence" section is rewritten per M5. The "pending counsel review" text is kept until counsel signs off.
9. `CLAUDE.md` decisions line 14: update to the owner's decision and the M1 answer. This file is the agents' rule book, so the update goes in the same PR as the DECISIONS-LOG entry.
10. `docs/design/DECISIONS-LOG.md`: add the 2026-11-13 entry and the M1 answer.
11. `docs/research/notes/Bazaar server packaging/licences.md` section 2: per M8.
12. `AUTHORS`: per M12. Probably no change.
13. New: `docs/legal/` or a similar folder for the module exception text, CLA text, commercial licence, trademark policy. Owner picks the location. Not created here.
14. New: `.github/workflows/` CLA check (`cla`) and an updated DCO check, if M4 option A is chosen. Built later, with a test.

## 6. Module exception (PROPOSED wording, for counsel)

Intent, from section 3, item 2: modules that use the public module API may be under any licence, including closed commercial. The core stays AGPL.

Draft text (PROPOSED, not legal advice, for counsel to rewrite):

> **Module exception.** As a special exception to the GNU Affero General Public License, version 3, the copyright holders of Bazaar grant you permission to create, compile, link, load and distribute a Module that communicates with Bazaar only through the Public Module API, and to license that Module under terms of your choice, including terms that are not open source and terms for sale. The exception applies to the Module and not to the Bazaar core. A "Module" is a separate program, or a separate component loaded through a documented extension point, that uses the Public Module API. The "Public Module API" means the interfaces, types and protocol messages that Bazaar publishes as public, listed in the module API reference and covered by its semantic-versioning contract, and the SDK and contracts packages named in that reference. Modifications to the core, and code that uses internal or unlisted interfaces, are not covered by this exception and remain under the GNU Affero General Public License, version 3. Each copy of Bazaar carries this exception.

Points for counsel (each is an open legal question, not a design choice):

1. Whether an exception under section 7 of the AGPL, placed in the licence file, is the right vehicle, or whether a separate linking exception is safer.
2. Whether a module loaded in-process, or a module running as a separate process over the wire protocol, is a "combined work" under the AGPL, and whether the exception needs to cover both.
3. Whether the Public Module API definition is precise enough. The definition relies on the module API contract (semver, compatibility matrix) and on contract tests.
4. Whether the exception survives forks, and whether a fork can change the Public Module API without losing the exception.
5. If M1 option B is chosen, whether the SDK and contracts are inside the exception (they are the module API) and whether AGPL on those packages still allows Apache-style use by module authors.

The exception only works if the copyright holders can grant it. That needs the CLA (section 7), because contributors would otherwise keep rights in their own code.

## 7. Contribution terms: DCO and CLA flow

### 7.1 Which path applies

| Change | Path | Rule |
|---|---|---|
| A module (any tree that is a module under the module API) | DCO | Each commit has `Signed-off-by:` from the human contributor. |
| A small fix to the core | DCO | "Small" is an OPEN threshold (see 7.4). |
| Core code (everything else) | CLA plus DCO | CLA signed once before merge; DCO still on each commit. |
| Extracted repos (Kodo, Constellation.Renderer, later) | CLA plus DCO | Same as core, because the copyright holders must keep the rights to relicense and sell commercial licences. |

### 7.2 DCO flow (module and small fix)

1. Contributor commits with `git commit -s`, which adds `Signed-off-by: Name <address>`.
2. The DCO check runs on every commit in the PR (automated, PLANNED).
3. The PR is squash-merged. The squash message keeps the `Signed-off-by:` lines of every commit that the PR carries. The current CONTRIBUTING rule that the sponsor adds the line to the PR description is kept only as a second sign-off. OPEN: whether the sponsor line is still required once every commit carries one (see 7.4).
4. Agent-authored commits keep `Co-Authored-By:` trailers. The human sponsor's sign-off is the DCO sign-off for the agent's work. Counsel to confirm (section 12).

### 7.3 CLA flow (core code, option A of M4)

1. Contributor opens a draft PR.
2. CI runs a `cla` check. If the contributor has no current CLA on file, the check fails with a link to sign.
3. The contributor signs the CLA electronically. The signature record stores the contributor's account identifier, the CLA version, the time and the signing method. It does not store anything the contributor did not provide.
4. The check turns green on the next run. The owner's merge rule (CLAUDE.md, "Merging") already requires green CI, so a missing CLA blocks the merge.
5. The CLA version is pinned. A new CLA version asks existing signers to sign again before their next merge. Counsel to decide whether re-signing is needed.
6. CLA text grants The Bazaar Authors the right to use, relicense and sublicense the contribution under open-source and commercial terms, and keeps the contributor's copyright. Counsel to confirm assignment versus licence grant. This design assumes a licence grant, which is the usual choice for projects that keep contributor copyright.
7. Signature records are held by the CLA service, outside git (see M12, and section 12 on personal data).

### 7.4 Open questions for the owner (OPEN)

1. M4: CLA mechanism, option A (electronic, check on the PR) or option B (attestation in the PR template).
2. Definition of "small fix": OPEN. Options: (a) a fixed size limit, for example a line count per PR; (b) a path list (bug fixes to existing files only, no new public API, no new files); (c) a label set by the reviewing human. Option (b) is the most predictable. The threshold is a setting in the repo (configurable, safe default: CLA for everything not on the list).
3. Whether the sponsor sign-off line stays once every commit carries a DCO line.
4. Whether AI agent commits need a separate CLA-equivalent statement. Counsel question, see section 12.

## 8. Dependency AGPL-compatibility rule (PLANNED)

Rule: every dependency that ships in a Bazaar build (server, clients, modules that Bazaar bundles, the bundled PostgreSQL and pgvector) must have a licence compatible with AGPL-3.0. Build-only tools that do not ship are checked too, with a lower risk label.

Mechanics:

1. The release SBOM job (doc 37, distribution and packaging) records each package's SPDX licence expression.
2. A compatibility check reads the SBOM and compares each licence against an allowlist. The allowlist is a repo file, owned by the owner, reviewed by counsel.
3. A licence not on the allowlist fails the release build. Operators can override it only through an audited owner action, which is recorded in the audit log.
4. The third-party notices section of `NOTICE` is generated from the SBOM.

Starting allowlist (PROPOSED, counsel confirms each entry):

- Permissive: MIT, BSD-2-Clause, BSD-3-Clause, ISC, Apache-2.0 (one-way compatible with AGPL-3.0 under the Apache text's own terms), PostgreSQL License (for the bundled PostgreSQL).
- Copyleft compatible with AGPL-3.0: AGPL-3.0 (any version), GPL-3.0 (with the AGPL caveat counsel must confirm), LGPL-3.0 (as a library).

Not on the allowlist, requires counsel before use: GPL-2.0-only (not compatible with AGPL-3.0), any source-available licence (for example SSPL, BSL, FSL), any licence with field-of-use or commercial-only restrictions, and any licence with an unknown or unparseable expression.

The research note (`cicd_repo_licensing.md`) records that pg_search is AGPL-3.0 and also offered under a commercial licence. That case is allowed under this rule and is covered in the packaging note (`licences.md`). Counsel to confirm the bundling question listed there.

## 9. Trademark policy and marks

### 9.1 Marks

| Mark | Status | Action |
|---|---|---|
| Bazaar | Core mark (decision). | Clearance search, then registration. |
| CDAX (Bazaar.CDAX pilot company) | Core mark (decision). | Clearance search, then registration. |
| Codex (and Codex Alchemist, the owner's instance name) | Needs clearance (decision). CLAUDE.md notes it overlaps a well-known AI product name. No clearance search has been done. | Do not use in package IDs or public product names until cleared. |
| RUBEDO | Needs clearance (decision). | Clearance search first. Not used in public names until cleared. |
| Module names: Bazaar.GC, Bazaar.Constellation, Bazaar.RyuseiFuda, Bazaar.Conduit, Bazaar.KODO (Kodo), CALCINATO, ATHANOR | Key module names: owner picks which ones are registered (decision says "key module names"). | Clearance search for each registered name, then registration. |
| Third-party names used by reference (for example Avalonia, Monaco, PostgreSQL, Garnet, Valkey) | Owned by others. | Nominative use only. Never in a Bazaar product name or logo. |

### 9.2 Trademark policy outline (PLANNED, counsel drafts)

1. What the marks are and who owns them (The Bazaar Authors, or a named legal entity the owner chooses).
2. Permitted use: accurate references to Bazaar, forks that say they are forks, modules that say they work with Bazaar, operators who say they run Bazaar.
3. Restricted use: a fork or product may not use the marks in a way that suggests endorsement, a name that is confusingly similar, or a logo change. Forks must rename.
4. Module naming: a module may say "for Bazaar" but may not use a core mark in its own name without permission.
5. Marketplace: the marketplace may remove a listing that misuses a mark. The removal rule is recorded in the audit log.
6. Procedure for requests and for takedowns.

## 10. Marketplace licence rules (PLANNED)

Every module manifest declares:

- `licence`: an SPDX licence expression (required). Missing or unparseable is rejected at publish time.
- `distribution`: `open-source` or `commercial` (paid). A commercial module also declares its licence grant URL or ID.
- `uses`: `public-api` (the default, and the only value that gets the module exception) or `internal` (a module that uses unlisted interfaces; it is not covered by the module exception and must be AGPL-compatible).

Install-time checks, run by the module manager:

1. The manifest is signed and the signature verifies (module trust rule, decided in the gap decisions).
2. The licence expression parses and is on the allowlist for the module's `uses` value.
3. A `public-api` module under any licence is allowed by the module exception. An `internal` module must be AGPL-compatible, and the install is blocked otherwise.
4. The licence and distribution are shown to the user before install, and in the module manager afterwards.
5. A module with no valid licence is blocked. An override needs an audited owner action.
6. A paid module needs a licence grant that the install checks (format and not expired). How payment and entitlement work is a separate design, not decided here.

The VERIFIED tag (gap decisions) includes the automated licence check. A later version that fails the licence check revokes the tag.

## 11. Decided and PLANNED boundaries

- Core: AGPL-3.0 plus module exception. Dual-licence commercial offer.
- Modules: any licence if they use only the public API. Otherwise AGPL-compatible.
- Dependencies: AGPL-compatible (section 8).
- Marks: registered after clearance (section 9).
- Contributions: DCO for modules and small fixes, CLA for core and extracted repos (section 7).

## 12. Counsel review checklist

Counsel reviews every item before the first public release. The owner asks counsel before any file in section 5 changes to a legal text.

1. Module exception: validity, vehicle (AGPL section 7 or separate linking exception), combined-work analysis for in-process and out-of-process modules, and the precision of the Public Module API definition (section 6).
2. M1 and M6: whether the SDK and contracts keep Apache-2.0 (option A) or move to AGPL (option B), and whether AGPL-3.0-only or -or-later is chosen.
3. CLA: text, licence grant versus assignment, re-signing on version change, governing law, and whether an electronic CLA with a status check meets the requirements of the jurisdictions the project expects contributors from.
4. DCO: whether the sponsor sign-off line adds anything once every commit has a DCO line.
5. AI agent contributions: whether they can be copyright-protected, how the human sponsor's sign-off and CLA cover them, and whether the disclosure in `AUTHORS` and `Co-Authored-By:` trailers is enough.
6. Commercial licence: the order form or contract, warranties, and how dual licensing interacts with the CLA grant.
7. Trademark: clearance searches for Codex and RUBEDO and for each core mark and key module name, the registration jurisdictions, and the trademark policy in section 9.2.
8. Marketplace: the allowlist in section 8, the `uses` rule in section 10, and whether a paid module under a commercial licence needs any extra terms in the marketplace.
9. Bundled PostgreSQL and pgvector: the questions in `docs/research/notes/Bazaar server packaging/licences.md` section 4.
10. Privacy: what the CLA register stores (account identifier, version, time, method), where it is held, how long it is kept, and how a contributor can ask for the record. The privacy design (doc 32) is the reference.
11. Copyright holder name: "The Bazaar Authors" as used in `NOTICE` and `CLAUDE.md`, and whether a legal entity should hold the copyright and marks.
12. Timing: the first public release waits for this checklist. Owner approval plus counsel sign-off is the gate.

This document is not legal advice. The authors of the repo are not lawyers. Each legal text here is a draft.
