# Linux client, packaging, updates and signing

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md · Used by: 32-remote-access-auth-and-privacy.md, 33-backup-dr-and-export.md, 34-testing-strategy.md, 35-spikes-plan.md

## Purpose
Defines the Linux desktop client, built last on the shared Avalonia UI, and how every Bazaar component is packaged, versioned, updated and signed. Server installs (Windows Service and systemd) are included because they share the upgrade and rollback rules.

## Decided inputs
- .NET 10 and one C# codebase; Avalonia clients on a shared `Bazaar.Client.Core` (CLAUDE.md).
- Server is one OS-agnostic build, run as a Windows Service or systemd unit; Windows is the first supported host (CLAUDE.md; Bazaar.Server.md).
- The server does all compute; clients render (CLAUDE.md).
- Linux is last in coding order and needs a native PTY, not ConPTY. Feature scope is OPEN (Bazaar.Linux.md).
- Android's first distribution is a Google Play closed test (Bazaar.Android.md).
- Licences: AGPL-3.0 for the repository; Apache-2.0 for `src/Bazaar.Sdk/` and `src/Bazaar.Contracts/` (NOTICE). Licensing is pending counsel.
- Human sign-off at merge through the DCO line; agents open draft PRs only and never push to the default branch (CLAUDE.md; `pr-sponsor.yml`).
- No secrets or keys in git (CLAUDE.md; `ci.yml` secret scan).
- Velopack for the Windows client is the research favourite. CLAUDE.md does not list it, so it is a working choice until the owner confirms it (Conflict 1).

## Design

### 1. Linux client scope [PROPOSED]

| Area | Windows (doc 28) | Linux v1 proposal |
|---|---|---|
| GC chat, Kanban, Constellation, Vault, palette, theme | Yes | Yes, shared `Bazaar.Client.Core` |
| Terminal | ConPTY, embedded and detached | Native PTY (section 2), after Conflict 2 |
| Monaco IDE | WebView2 | Gated on a Monaco-on-WebKitGTK spike. Avalonia's native WebView needs the paid Accelerate licence |
| Default-app registration | Windows default-app APIs | `.desktop` entries and `xdg-mime`; default-terminal mechanism unverified |
| Updates | Velopack | Per packaging format (section 3) |
| Accessibility | UI Automation (unverified) | AT-SPI2 backend in Avalonia 12.0 (vendor claim) |

Avalonia supports X11 by default, Wayland as opt-in from 12.1.0, and lists Ubuntu 25.x, Fedora 43 and Debian 13 as Tier 1 (vendor docs). **[PROPOSED]** v1 supports Tier 1 only.

### 2. PTY terminal backend [PROPOSED]
- One interface, `ITerminalBackend`, with ConPTY on Windows and PTY on Linux (sketch below).
- Linux implementation: `openpty`/`forkpty` through P/Invoke, or a NuGet PTY package. Candidate licences and maintenance are unverified.
- Where the shell runs is Conflict 2. If sessions run on the server or an agent VM, the native PTY belongs on the Linux server host, and the client only renders a stream.

### 3. Client packaging [PROPOSED]

| Format | For | Against |
|---|---|---|
| Self-contained tarball | Baseline; no sandbox | No update channel |
| AppImage | One file, no root | Update tool (AppImageUpdate) unverified |
| Flatpak | Sandbox; Flathub updates | Terminal reach into the host needs sandbox holes; unverified |
| deb / rpm | Native installs | We would host and sign our own repositories |

**[PROPOSED]** Ship self-contained builds (no Native AOT in v1, per Bazaar.Server.md) as a tarball and an AppImage. Spike Flatpak. Defer deb and rpm. Whether Velopack covers Linux is unverified.

### 4. Installers and update paths [PROPOSED]
- **Windows client:** Velopack, per-user install under `%LocalAppData%`, delta updates from GitHub Releases (verified in Velopack docs). Velopack does not sign, does not cover Windows Services and does not document machine-wide installs. `VelopackApp.Build().Run()` must be the first call in the entry point.
- **Windows server:** Worker Service with `AddWindowsService()`. Installer as **MSI built with WiX v5** (unverified), which registers the service, keeps data under `%ProgramData%` and upgrades with MajorUpgrade. MSIX is an open gap.
- **Linux server:** tarball plus install script: dedicated `bazaar` user, `/etc/bazaar/` config, `/var/lib/bazaar/` state, systemd unit with `Type=notify` (unverified) and `Restart=on-failure`. Releases sit in `/opt/bazaar/releases/<version>` behind a `current` symlink, so switching or rolling back is one atomic step.
- **Android:** Play closed test (verified: 12 testers, 14 continuous days, personal accounts created after 13 Nov 2023). Play needs an AAB (unverified). Keys live in Play App Signing. The keep-alive uses foreground service type `dataSync` (verified), which the same page says cannot start from `BOOT_COMPLETED` on Android 15+. This affects doc 29.
- **Winget:** after the first stable release. Manifests point at a released installer URL and SHA-256. Publication rules unverified.

### 5. Versioning [PROPOSED]
- SemVer per component. One tag `vX.Y.Z` builds all components together.
- A **protocol version** integer in the handshake, separate from app versions and from the wire format (OPEN, doc 02).
- The server accepts the current and previous protocol version for one release, and otherwise returns "upgrade required" with the minimum version.
- Channels: `stable` and `beta`. Server upgrades are always operator-run; automatic server updates are off.

### 6. Server upgrade and rollback [PROPOSED]
1. The installer reads the installed app and schema versions and refuses a downgrade.
2. Stop the service and drain agent turns (doc 06). Wait for the process to exit before replacing files; a hard kill can skip `StopAsync`.
3. Take a `pg_dump` custom-format backup and verify it restores (doc 33).
4. Run migrations as an explicit step (`Bazaar.Server.Host migrate`), never at startup, under an advisory lock. Expand-then-contract only.
5. Start the new binary. Pass a health check or roll back.

Rollback switches `current` back. An old binary refuses to start if the schema is newer than it knows. A database restore is a last resort (Conflict 5).

### 7. Signing [PROPOSED; needs quotes and counsel]

| Option | Cost | Eligibility and fit |
|---|---|---|
| Unsigned | None | SmartScreen warnings; fine for owner-only betas |
| SignPath Foundation | Free for OSS (verified, vendor page) | Conditions from secondary sources: all components OSI-licensed without commercial dual-licensing, already released in the signed form, automated build. Fits AGPL, Apache and MIT Avalonia core. Blocked by Avalonia paid tiers |
| Azure Artifact Signing | Quota tiers verified; USD prices did not render | Secondary report: individuals in US/Canada, or organisations in EU/UK |
| OV or EV certificate | Not researched | Cost and token requirements unknown |

Scope: Windows executables, MSI and Velopack packages. Linux: SHA-256 sums plus a detached signature (GPG or Sigstore, unverified). Android: Play App Signing. SignPath holds its own key, so no certificate sits in the repository.

### 8. Release pipeline [PROPOSED]
- Exists: `ci.yml` (build and test on `ubuntu-latest` and `windows-latest`, gitleaks) and `pr-sponsor.yml`. No release workflow yet.
- New `release.yml`, started only by the owner (`workflow_dispatch` with a version input). Its publish job runs in a GitHub environment with the owner as required reviewer, and is the only job with `contents: write`. Actions stay pinned to commit SHAs.
- Stages: build and test, pack, sign, SBOM and NOTICE, checksum, then a **draft** release that the owner promotes. Runners stay GitHub-hosted.

## Data and interfaces (sketches)

```csharp
public interface ITerminalBackend {
    Task<ITerminalSession> OpenAsync(TerminalOptions options, CancellationToken ct);
}

public sealed record ReleaseManifest(
    string AppVersion, int MinProtocol, int MaxProtocol,
    string SchemaVersion, IReadOnlyList<ReleaseArtifact> Artifacts);

public sealed record ReleaseArtifact(string Os, string Format, string Sha256, string SignatureRef);
```

Handshake (format pending doc 02): `{ "component": "client-linux", "appVersion": "0.0.0", "protocolVersion": 1 }`.

## Behaviour and edge cases
- Client updates never touch the server, which must accept the previous protocol version during that window.
- Installers must not replace files while the service runs. A failed migration leaves the old binary in place. `CREATE INDEX CONCURRENTLY` needs its own step.
- Keys and signing tokens never reach logs or release notes.

## Testing
- Version negotiation: table tests for accept, upgrade-required and refuse-downgrade.
- Migrations on real Postgres: upgrade N-1 to N with data, roll back to N-1 on an expand-only schema.
- Service install: fake host in CI; real install on the NUC is a spike. Linux unit: `systemd-analyze verify`.
- PTY on `ubuntu-latest` with a real PTY; Linux client via Avalonia.Headless; packaged builds smoke-tested headless.
- Release gate: a script verifies every checksum and signature before the draft is created.

## Work packages
1. `ReleaseManifest` and handshake records in `Bazaar.Contracts`, with tests.
2. Server `migrate` subcommand and startup schema guard (needs doc 04).
3. `ITerminalBackend` and Linux PTY (needs Conflict 2).
4. Linux server package and smoke test (needs 2).
5. Windows server MSI spike, then build (needs 2).
6. Velopack pack spike for the Windows client (needs doc 28's head).
7. `release.yml` draft: environment gate, checksums, draft release, no signing yet.
8. Signing integration (needs Question 9).
9. Linux client head on the shared UI (needs docs 27 and 28); `.desktop` and `xdg-mime`; Flatpak spike.
10. SBOM and NOTICE from NuGet in CI.

## Conflicts
1. **Velopack as decided.** The brief calls it decided, but CLAUDE.md does not list it. Velopack does not cover Windows Services, machine-wide installs or signing, so the server needs its own installer track.
2. **Where the terminal runs.** Bazaar.Windows.md §4 and Bazaar.Linux.md put the shell on the client. CLAUDE.md says the server does all compute and clients render.
3. **Android path.** The owner chose the Play closed test. The research suggested sideloading the owner's devices first, to avoid the 12-tester, 14-day gate. Both can run in parallel.
4. **Signing key location.** `cicd_repo_licensing.md` puts the certificate in a private repo's secrets. CLAUDE.md describes only the public repo. SignPath would hold the key instead.
5. **Rollback versus the append-only log.** A database restore discards events written after the backup, which breaks "append-only, never purged" (CLAUDE.md) unless the owner accepts the loss window or doc 33 provides replay.
6. **SignPath versus Avalonia paid controls.** Rich Text Editor, Tree DataGrid, Markdown and PDF viewers, and Accelerate's WebView all block a SignPath build. The Monaco path may need Accelerate.
7. **Linux Monaco.** No source shows Monaco working in an Avalonia WebView on Linux, so Linux scope may be smaller than Windows.

**Verification status.** Verified: Velopack overview; Play closed-test rule; Android foreground service types; SignPath free for OSS (vendor page); Azure quota tiers; Avalonia Linux tiers, X11 and Wayland; Avalonia paid tiers. Vendor claim: AT-SPI2. Secondary: SignPath conditions; Azure eligibility. Unverified: Velopack on Linux and services; WiX; MSIX; sd_notify; AppImage and Flatpak; SmartScreen; winget; EV prices; Play fee; AAB packaging.

## Questions for the owner
1. **Velopack.** Confirm it as the Windows client updater, since CLAUDE.md does not list it. Default: yes, after a spike.
2. **Terminal location.** Server-side PTY with the client rendering, or a local shell on the client? Default: server-side, which moves the native PTY work to the Linux server host.
3. **Linux v1 scope.** Default: chat, Kanban, Constellation, Vault, palette and theme. Terminal follows Question 2. Monaco only if its spike passes.
4. **Linux distributions.** Default: Tier 1 only (Ubuntu 25.x, Fedora 43, Debian 13).
5. **Linux format.** Default: tarball and AppImage now; Flatpak after its spike; deb and rpm later.
6. **Owner-only sideload APK.** Keep it alongside the Play closed test? Default: yes.
7. **Windows server installer.** Default: MSI via WiX. MSIX is out of v1 until spiked.
8. **Linux server package.** Default: tarball with install script and systemd unit first; deb and rpm later.
9. **Signing route.** Default: unsigned betas now; SignPath after the first public release and a licence review; Azure only if eligible (needs counsel; depends on whether you sign as an individual or an organisation, and your country).
10. **Release gate.** Default: owner-only trigger, required reviewer on the environment, drafts first.
11. **Rollback policy.** Default: forward-only migrations and binary rollback. A database restore needs your acceptance of the loss window, or doc 33 provides replay.
12. **Versioning.** Default: SemVer per component, one tag per release, protocol integer with N-1 support.
13. **Server auto-update.** Default: off; operator-run upgrades only.
14. **Winget.** Default: publish after the first stable release.
