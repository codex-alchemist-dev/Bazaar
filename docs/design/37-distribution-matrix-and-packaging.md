# 37. Distribution matrix and packaging

Status: PROPOSED (rewritten 2026-11-12 for the owner's one-click company build). Scope: how every Bazaar component is packaged, installed, updated and produced. Extends doc 30 (Linux client and packaging) and doc 28 (Windows client); where they disagree, this doc and `DECISIONS-LOG.md` win once the owner accepts it.

## 1. Principles

- **Packaging is a component.** Each package format is a Bazaar Component: its own project under `packaging/`, one `AddBazaarX()`-style registration where it runs in code, contract-only dependencies, and `TODO(component)` markers until the component model merges.
- **One version, many artifacts.** Every artifact carries the same product version (`VersionPrefix` in `Directory.Build.props`, see `docs/VERSIONING.md`). The display form is `v00.MM.PP`; the machine form is SemVer (`0.MM.PP`). Owner to confirm (section 6).
- **The server is the build station (owner decision, 2026-11-12).** Each Bazaar company runs its own `Bazaar.Server`. The server installer builds that company's client installers in one click and leaves them in an output folder. No terminal, no scripts, no SDK setup, and no build steps happen outside the installer window. Section 4a defines this.
- **This repo ships server builds only.** The shared source builds server packages and the build pipeline that a company server uses. Client packages are produced per company, never published as a single generic client.
- **Signing is parked, not skipped.** Every artifact is produced unsigned by default and signed when the company supplies a certificate or signing account. Unsigned builds are labelled as such in the output folder.
- **No silent defaults.** Registering a handler or a default app is declared in the package, then the user is sent to the OS settings page to make it the default. Never write UserChoice.
- **Server and client are separate tracks.** A Windows service needs a machine-level installer. Velopack is for the per-user client only (doc 28, doc 30).

## 2. Distribution matrix

Who builds what: **company server** (on the operator's machine, via the server installer) or **repo CI** (server packages only).

| Platform | Artifact | Who it serves | Produced by | Status |
|---|---|---|---|---|
| Windows server | MSI (WiX v5), registers `Bazaar.Server` service, data under `%ProgramData%`, MajorUpgrade | Self-host (operator's PC) | Repo CI | Build now |
| Windows server | MSIX | Not for the service (doc 30 gap) | n/a | Out of v1 until spiked |
| Linux server | systemd unit plus tarball, `.deb`, `.rpm` | Headless server | Repo CI | Build now |
| Server (any OS) | OCI image (Compose file, multi-arch) | Container hosts | Repo CI | Build now |
| Windows client | MSIX (sideload first) | That company's Windows devices | Company server, one click | Build now (pipeline) |
| Windows client | Velopack per-user installer, delta updates | Auto-update path | Company server, one click | Build now (doc 28) |
| Windows client | Unpackaged zip (portable) | Anyone without an installer | Company server, one click | Build now |
| Windows client | winget, Scoop, Chocolatey manifests for the company's own feed | Company devices | Company server, one click | Manifests now; publication parked |
| Linux client | Flatpak bundle (local repo) | That company's Linux devices | Company server, one click | Build now |
| Linux client | AppImage | Portable Linux | Company server, one click | Build now |
| Linux client | `.deb`, `.rpm`, Snap manifest | Debian/Ubuntu, Fedora/RHEL, Snap | Company server, one click | Build now; store push parked |
| Android | APK (sideload) and AAB (Play) | That company's Android devices | Company server, one click | APK now; Play upload parked |
| macOS | `.pkg` and `.dmg`, Homebrew cask | macOS clients and server | Company server (macOS host) | Owner to confirm (section 6) |

Parked items (need the owner's accounts or secrets) are listed in section 7.

Each company build is a set built from the same source with that company's module list and baked layout and theme (doc 41 sections 4.1 and 4.13). Two companies never share a client binary.

## 3. Versions and channels

- Tags: annotated, immutable, `v00.MM.PP` (`vX.Y.Z[-pre]` from v1.0.0). Tags map one-to-one to the machine version.
- Channels follow `VERSIONING.md` section 3: LTSC, Stable, Beta, Alpha, Latest-Commit. The company server carries the channel it was installed on, and its client builds carry the same channel label.
- Clients and the server read the channel from their own settings; the operator chooses it.
- The installer refuses a downgrade and reads the installed schema version before replacing files (doc 30, section 5).

## 4. Release pipeline (repo CI)

1. A tag on `main` triggers `release.yml` (GitHub Actions).
2. Build jobs run in parallel: Windows (PC or hosted Windows runner), Linux (CI), macOS (CI, when enabled).
3. Each job builds the server packages (section 2), runs its tests, and writes `SHA256SUMS`.
4. Signing steps run only when the secrets exist. Without them, the artifact is published unsigned and the release notes say so.
5. The GitHub Release is created as a draft, then published after every required server artifact is present.
6. The pipeline also publishes the company build pipeline itself (section 4a) as a server package, so every company server can build its own clients.

No artifact is built on a developer machine and uploaded by hand. No client binary is published from this repo.

## 4a. Company build on the server (one click)

Running the server installer produces the company's client installers in an output folder. The installer does all of the following, with no user action after the first click:

- **Bundled toolchain.** The server installer carries (or fetches on first run, see the spikes) the build toolchain the clients need: the .NET SDK for the target, and the platform packaging tools. The user installs nothing separately.
- **Platform packaging preparation.** The installer prepares what apt, flatpak and the other formats need (repository metadata, the local Flatpak repo, the `.desktop` and `xdg-mime` files) so the outputs are ready to install.
- **Inputs.** The company's module list, the baked default layout and theme (doc 41), the channel, and the company's signing settings. The server writes these from its own company configuration; the user does not hand-edit build files.
- **Outputs.** One output folder per build, for example `out/CDAX/` containing `CDAX.msix`, `CDAX.apk`, the Velopack feed, the Flatpak repo and a `SHA256SUMS` file. Unsigned outputs are labelled in a `README.txt` in the folder.
- **Failure.** A failed platform build leaves the other outputs in place and writes a plain report in the output folder. The server never publishes partial builds as complete.
- **Scheduling.** A build runs on install, on an update of the server, or when the company changes its module list or baked layout. It is one job on the server, visible in the Observatory (doc 23), never a separate tool.

Feasibility questions are spikes, not assumptions. Each must be answered before this section is built:

- **S-B1 Android SDK bundling.** The Android SDK and the .NET Android workload are large. Measure the installer size if bundled, against the time to download on first run. Doc 29 keeps the Android head out of the default solution, so the company build must add it on demand.
- **S-B2 Offline builds.** Whether a company server can build all clients with no network after install. The toolchain, NuGet packages and Android SDK must be cached in the installer.
- **S-B3 Signing keys.** Where signing keys live. Keys held by the company server are convenient but put signing on the server. Keys held off-server need an extra step, which conflicts with "no user steps". See the open decision in doc 41.
- **S-B4 Linux packaging without a terminal.** Whether `.deb` and Flatpak outputs can be produced by a server running as a service, with no user shell.

## 5. Install, upgrade and rollback rules

- Installers never replace files while the service runs. The MSI stops the service, swaps binaries, runs migrations, then starts the service.
- A failed migration leaves the old binary in place (doc 30).
- `CREATE INDEX CONCURRENTLY` runs as its own step.
- Uninstall keeps data under `%ProgramData%` unless the user picks removal.
- Client installation to a device (QR code or link from the company server) is not yet designed. Pairing (doc 32) covers the device credential only, not the app install.

## 6. Open questions

1. Version form: `0.MM.PP` (VERSIONING.md) or the owner's "valid 0.0.N". Owner to choose.
2. macOS: whether the docs allow it for v1, and whether a company server on macOS is a v1 target (owner).
3. Velopack versus MSIX for the client (doc 28 decided Velopack; this doc keeps MSIX as an extra channel).
4. Winget manifest publication rules are unverified (distribution research note).
5. Signing key custody (S-B3): decided 2026-11-12, keys live on the company server, protected like other server secrets. Counsel still checks key custody (doc 28, round 4).
6. Android SDK (S-B1): decided 2026-11-12, downloaded on first run, so the server installer stays small. The first Android build needs network. Spike S-B1 still measures download size and time. Offline first-run builds (S-B2) now depend on a later cache decision.

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
