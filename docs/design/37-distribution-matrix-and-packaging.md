# 37. Distribution matrix and packaging

Status: PROPOSED. Scope: how every Bazaar component is packaged, installed, updated and published. Extends doc 30 (Linux client and packaging) and doc 28 (Windows client); where they disagree, this doc and `DECISIONS-LOG.md` win once the owner accepts it.

## 1. Principles

- **Packaging is a component.** Each package format is a Bazaar Component: its own project under `packaging/`, one `AddBazaarX()`-style registration where it runs in code, contract-only dependencies, and `TODO(component)` markers until the component model merges.
- **One version, many artifacts.** Every artifact carries the same product version (`VersionPrefix` in `Directory.Build.props`, see `docs/VERSIONING.md`). The display form is `v00.MM.PP`; the machine form is SemVer (`0.MM.PP`). The owner asked for a "valid 0.0.N underneath". `VERSIONING.md` already decided `0.MM.PP` (for example `v00.01.00` maps to `0.1.0`), so this doc follows that. Owner to confirm.
- **Signing is parked, not skipped.** Every artifact is produced unsigned by default and signed when the owner supplies a certificate or signing account. Unsigned builds are labelled as such in release notes.
- **No silent defaults.** Registering a handler or a default app is declared in the package, then the user is sent to the OS settings page to make it the default. Never write UserChoice.
- **Server and client are separate tracks.** A Windows service needs a machine-level installer. Velopack is for the per-user client only (doc 28, doc 30).

## 2. Distribution matrix

| Platform | Artifact | Who it serves | Built on | Status |
|---|---|---|---|---|
| Windows client | MSIX (sideload first) | Modern Windows, clean uninstall | PC | Build now |
| Windows client | winget manifest (points at the release installer URL and SHA-256) | `winget install` | PC (manifest generation) | Manifest now; submission parked |
| Windows client | Velopack per-user installer, delta updates | Auto-update path | PC | Build now (doc 28) |
| Windows client | Scoop manifest | Scoop users | PC | Build now |
| Windows client | Chocolatey package (`.nuspec` and install script) | Chocolatey users | PC | Build now, push parked |
| Windows client | Unpackaged zip (portable) | Anyone without an installer | PC | Build now |
| Windows server | MSI (WiX v5), registers `Bazaar.Server` service, data under `%ProgramData%`, MajorUpgrade | Self-host (CKorvix_V1 target) | PC | Build now |
| Windows server | MSIX | Not for the service (doc 30 gap) | n/a | Out of v1 until spiked |
| Linux client | Flatpak manifest (Flathub submission parked) | Desktop Linux | CI | Build now |
| Linux client | AppImage | Portable Linux | CI | Build now |
| Linux client | `.deb`, `.rpm` | Debian/Ubuntu, Fedora/RHEL | CI | Build now |
| Linux client | Snap | Ubuntu Store users | CI | Manifest now; store push parked |
| Linux server | systemd unit plus tarball, `.deb`, `.rpm` | Headless server | CI | Build now |
| Server (any OS) | OCI image (Compose file, multi-arch) | Container hosts | CI | Build now |
| macOS | `.pkg` and `.dmg`, Homebrew cask | macOS clients and server | CI (macOS runner) | Build when the docs allow; owner to confirm |
| Android | APK (sideload) and AAB (Play) | Android client (doc 29) | CI | APK now; Play upload parked |
| Package channels | Homebrew formula or cask, Scoop, Chocolatey, winget, Flathub, Snap | Discovery | Manifests in repo | Manifests now; submission parked |

Parked items (need the owner's accounts or secrets) are listed in section 7.

## 3. Versions and channels

- Tags: annotated, immutable, `v00.MM.PP` (`vX.Y.Z[-pre]` from v1.0.0). Tags map one-to-one to the machine version.
- Channels follow `VERSIONING.md` section 3: LTSC, Stable, Beta, Alpha, Latest-Commit. Each channel is a separate feed: the GitHub Releases pre-release flag, the winget `Channel` field, the Flatpak branch and the Velopack channel name.
- Clients and the server read the channel from their own settings; the operator chooses it.
- The installer refuses a downgrade and reads the installed schema version before replacing files (doc 30, section 5).

## 4. Release pipeline

1. A tag on `main` triggers `release.yml` (GitHub Actions).
2. Build jobs run in parallel: Windows (PC or hosted Windows runner), Linux (CI), macOS (CI, when enabled), Android (CI).
3. Each job builds its artifacts, runs its tests, and writes `SHA256SUMS`.
4. Signing steps run only when the secrets exist. Without them, the artifact is published unsigned and the release notes say so.
5. Manifest generation writes the winget, Scoop, Chocolatey, Homebrew, Flatpak and Snap files from the released URLs and hashes. A manifest is never generated before its artifact exists.
6. The GitHub Release is created as a draft, then published after every required artifact is present.
7. Submission jobs to external channels run only with their tokens (parked).

No artifact is built on a developer machine and uploaded by hand.

## 5. Install, upgrade and rollback rules

- Installers never replace files while the service runs. The MSI stops the service, swaps binaries, runs migrations, then starts the service.
- A failed migration leaves the old binary in place (doc 30).
- `CREATE INDEX CONCURRENTLY` runs as its own step.
- Uninstall keeps data under `%ProgramData%` unless the user picks removal.

## 6. Open questions

1. Version form: `0.MM.PP` (VERSIONING.md) or the owner's "valid 0.0.N". Owner to choose.
2. macOS: whether the docs allow it for v1 (owner).
3. Velopack versus MSIX for the client (doc 28 decided Velopack; this doc keeps MSIX as an extra channel).
4. Winget manifest publication rules are unverified (distribution research note).

## 7. Parked: needs the owner's accounts or secrets

Listed once. Everything else is built.

- Authenticode code-signing certificate (Windows client, server MSI, MSIX). SignPath application needed (doc 28, round 4).
- Apple Developer ID and notarisation (macOS pkg and dmg).
- Google Play developer account (AAB upload).
- Flathub submission (needs a maintainer and a reviewed app ID).
- Winget community repo submission (needs a GitHub token for `winget-pkgs`).
- Chocolatey.org API key.
- Snap Store account.
- Homebrew tap or core submission token.
- Scoop bucket repo (needs a repo to host it).
- Container registry push credentials (for example GHCR under the org).
- Release-signing key for Velopack feeds.
