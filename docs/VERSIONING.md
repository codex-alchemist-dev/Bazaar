# Versioning and release channels

Status: DECIDED by the owner (2026-10-25) where marked; codenames v2, v3 and v5 are owner-accepted; v4 is Wormwood (owner-accepted).
Inspired by RESIBO V3 (`domtrifoundation/Receipt-System-V3`, `docs/MAINTENANCE.md` and `common/version.py`). This scheme differs from RESIBO's in three places: SemVer instead of per-PR counters, star-and-alchemy codenames instead of minerals, and immutable release tags.

## 1. Version numbers [DECIDED]

**SemVer 2.0:** `vMAJOR.MINOR.PATCH[-pre][+build]`.

| Part | Meaning (from v1.0.0) |
|---|---|
| MAJOR | A breaking change for operators or integrators: a wire-protocol major (doc 02), a schema change that cannot roll back (doc 04), a breaking `Bazaar.Sdk` / `Bazaar.Contracts` API, or a removed setting. |
| MINOR | Backward-compatible features. |
| PATCH | Backward-compatible fixes only. |
| `-pre` | Pre-release identifier: `-alpha.N`, `-beta.N`, `-rc.N`. It also selects the channel (section 4). |
| `+build` | The short commit hash. The .NET SDK appends it to `AssemblyInformationalVersion` automatically, so it is never a constant in the repo (same reason as RESIBO). |

**Before v1.0.0 [DECIDED]:** builds carry the placeholder codename **Beta** (RESIBO's `x00` period). The machine version is `0.MINOR.PATCH`, and its display form is zero-padded (next paragraph). Roadmap stages (`docs/ROADMAP.md`) are labels and are not tied to version numbers: completing a stage bumps nothing by itself. `MINOR` is bumped when a release adds backward-compatible features, and `PATCH` counts merged changes since the last `MINOR` (owner, 2026-10-29: this decouples the milestone tie). During 0.x anything may change. `v1.0.0` is a deliberate jump, taken only when the owner judges Bazaar truly ready (owner, 2026-10-25: Beta will last a long while, through the first real projects). It is never reached by ordinary counting, and `MINOR` may grow past 9.

**Display format: zero-padded [DECIDED, owner, 2026-10-29].** The owner wants early patch numbers not to run out, so the display and tag form pads each part to two digits: `v00.MM.PP`, for example `v00.00.01`, `v00.01.00` (Stage 1), `v00.01.02`. This also answers the earlier open question about an `xYY.ZZ.AA` scheme: the format is zero-padded SemVer with three parts, not a four-part scheme. The padded form is the one shown to people and used for tags. A part that passes 99 widens to three digits for that part only, which needs a line here.

Tradeoff, for the owner to confirm: SemVer 2.0 forbids leading zeros, and NuGet and .NET normalise them away (`00.01.00` becomes `0.1.0`). So the padded form is display and tag only. `VersionPrefix` in `Directory.Build.props` stays the valid SemVer machine form (`0.1.0`), and the padded form is derived from it one-to-one. Velopack, NuGet and any SemVer comparer use the machine form. Tags use the padded form and map to the machine form on build.

**Source of truth:** `VersionPrefix` in `Directory.Build.props`. Code reads it through `Bazaar.Contracts.ProductVersion` (`Informational`, `Codename(major)`, `CurrentCodename`). Every build carries the same product version: server, clients, SDK and Contracts.

**Separate version lines, not tied to the product number:**

| Line | Scheme | Owner |
|---|---|---|
| Wire protocol | `major.minor` (C40) | doc 02 |
| Database schema | migration number; startup refuses a mismatch (C44) | doc 04 |
| Settings registry | one entry per key; removing a key is a product MAJOR | doc 03 |

**Bumping:** the PR that merges into `main` bumps `VersionPrefix`: `PATCH` for a merged change, `MINOR` when a release adds features (0.x), and `MAJOR` for a breaking change (1.x and later). Stage completion is not a bump. A release is an annotated tag on `main`, in display form (`v00.MM.PP`, or `vX.Y.Z[-pre]` from v1.0.0); tags are immutable. The `-pre` suffix keeps its SemVer form.

## 2. Major-version codenames [DECIDED theme; all names through v5 owner-accepted]

Codex Alchemist names majors **Z to A**. Bazaar's theme (owner, 2026-10-25) is **words that belong to both alchemy and the stars**. The list grows only as majors ship (RESIBO's rule: don't front-load it). `ProductVersion` holds only names that are in use.

| Version | Codename | Why it fits both |
|---|---|---|
| 0.x (shown `v00.MM.PP`) | Beta | Placeholder for the pre-release period. |
| v1.0.0 | **Zuhal** (owner-picked, 2026-10-25) | Arabic name of Saturn, a wandering star; alchemists tied Saturn to lead, the base metal the Great Work starts from. |
| v2.0.0 | **Yliaster** (owner-accepted, 2026-10-25) | Paracelsus's prime matter, which he also described in an astral (sidereal) form. |
| v3.0.0 | **Xerion** (owner-accepted, 2026-10-25) | The dry powder of the stone, the root of "elixir". X is thin, like RESIBO's X. |
| **Wormwood** (owner-accepted, 2026-10-25) | A bitter medicinal herb in old pharmacopoeias, and the star Wormwood, which falls in Revelation 8:10-11 and makes the waters bitter. The alchemy link is the weaker of the two halves. |
| v5.0.0 | **Venus** (owner-accepted, 2026-10-25) | A planet, and the alchemical name for copper. |

Codename art and banners (About screen, credits, server status page) are kept for every codename forever. An LTSC install on an old major must still show its own name (RESIBO's retention rule).

## 3. Merging [DECIDED]

- Owner (2026-10-25): during full development, Claude may merge PRs that pass the requirements: CI green on the head commit (build and tests on Windows and Ubuntu, secret scan), no merge conflict, no open blocking review thread, and a draft PR marked ready first. Squash merge only. The owner can withdraw this at any time.
- DCO: the decided model is the owner's `Signed-off-by` at squash-merge. A Claude merge carries no human sign-off, so these merges are recorded as "merged under the owner's standing permission" in the squash message. [OPEN] Whether pre-release Claude merges need a later owner sign-off pass before the first public release (with the history purge already planned).
- `main` only ever moves by squash merge. Release tags go on `main`.

## 4. Channels [DECIDED]

Five channels, after RESIBO. The **Bazaar.Server operator picks one per install** (setting `update.channel`, doc 03 registry); clients follow the server's channel and only take builds whose wire protocol the server supports (doc 02).

| Channel | What it gets | Git shape |
|---|---|---|
| LTSC | One major, security and critical fixes only, backported | Real branch `ltsc/<codename>` forked at the major's release tag; fixes are cherry-picked |
| Stable | `vX.Y.Z` with no pre-release suffix | Tags on `main` |
| Beta | `vX.Y.Z-beta.N` and `-rc.N` | Tags on `main` |
| Alpha | `vX.Y.Z-alpha.N` | Tags on `main` |
| Latest-Commit | CI build of every `main` commit, `vX.Y.Z-dev.N+sha` | No tag; owner-only, never offered to other operators |

Stable, Beta and Alpha are points on one linear history, so they need no branches. Only LTSC diverges. Unlike RESIBO's moving channel labels, Bazaar tags are immutable SemVer tags and the pre-release suffix decides the channel; the update feed (Velopack, doc 30) publishes each build to its channel's feed.

[PROPOSED] defaults: new installs get Stable (Beta during 0.x, since no Stable exists yet). An LTSC line starts only when a major needs long-term support, and its support length is a setting.

## 5. Where the version shows

The server status page and admin panel (version, codename, channel, commit), the client About screen and credits roll (codename lineage plus third-party licence notices, which AGPL and Apache dependencies require), the wire `hello` handshake (protocol range), and logs. [PROPOSED] A codename banner appears on the client splash screen, like RESIBO's boot screen.
