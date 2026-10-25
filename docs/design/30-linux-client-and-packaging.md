# Linux client, packaging, updates and signing

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 6
Depends on: 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md, 21-github-integration.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md · Used by: 32-remote-access-auth-and-privacy.md, 33-backup-dr-and-export.md, 34-testing-strategy.md, 35-spikes-plan.md

## Purpose
Defines the Linux desktop client (`Bazaar.Client.Linux`), built last on the shared Avalonia UI, and how every Bazaar component is packaged, versioned, updated and signed. Server installs (Windows Service and systemd) are included because they share the upgrade and rollback rules.

## Decided inputs
- .NET 10 and one C# codebase; Avalonia clients on `Bazaar.Client.Core` (CLAUDE.md). Avalonia stays conditional on the Monaco hosting gate (round 5, C26).
- Server is one OS-agnostic build, run as a Windows Service or systemd unit; Windows is the first supported host (CLAUDE.md).
- The server does all compute; clients render. Ratified client-side exceptions: NPU inference, the human's local terminal, the Roblox Studio bridge relay, and syncing the viewed branch to a local folder. Layout stays server-side (round 4, C17).
- Terminals: `Bazaar.Server` and `Bazaar.Linux` are separate. Agent terminals run in VMs and are streamed by the server. The client's own terminal is a local, built-in shell (round 4, C31; doc 28 owns the Windows role).
- OSI-only signed build: no paid Avalonia components (no Accelerate or Pro controls), Postgres full-text search on Windows, and the free SignPath route for signing (round 4, C23).
- Monaco hosting is a gate on the Avalonia decision. Spike with free routes only: Windows WebView2 first, then Linux WebKitGTK, then native Android, then AvaloniaEdit (round 5, C26; research note).
- Push is a bare wake hint with no chat id or sequence number; content is fetched over the authenticated connection (round 5, C41).
- Protocol version and wire/status format are defined by doc 02 (`major.minor`); live and ready probes by doc 03 (round 12, C40).
- Migrations run only by an explicit `migrate` command; the server at startup checks the schema version and refuses a mismatch (round 11, C44).
- Owner merges happen in the Bazaar clients; the server performs the merge on the owner's signed approval, using either a dedicated owner GitHub App with merge rights or the owner's linked GitHub credential, chosen in setup (rounds 8 and 12, C25).
- Provider and GitHub App keys live in the Identity Service; the server bootstrap holds only the KEK and database credential (round 2, C05).
- No inbound connections to the home host by default; the host polls (round 8, C30).
- Versioning and channels (owner, 2026-10-25): versions are SemVer; pre-release builds are `v0.x`; release channels are LTSC, Stable, Beta, Alpha and an owner-only Latest-Commit channel, chosen by the operator. Stable, Beta and Alpha are git tags on one history; LTSC is a real branch. The full rules belong in `docs/VERSIONING.md`.
- Licences: AGPL-3.0 at the root; Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts`. Licensing is pending counsel (CLAUDE.md).
- Human sign-off at merge through the DCO line; agents open draft PRs only; no secrets or keys in git (CLAUDE.md).

## Design

### 1. Linux client scope [PROPOSED]

| Area | Windows (doc 28) | Linux v1 proposal |
|---|---|---|
| GC chat, Kanban, Constellation, Vault, palette, theme | Yes | Yes, shared `Bazaar.Client.Core` |
| Local terminal | ConPTY, embedded and detached | Native PTY (section 2), local shell only |
| Monaco IDE | WebView2 | Gated on the Monaco spike (C26), free route only. No paid WebView (C23) |
| Default-app registration | Windows default-app APIs | `.desktop` entries and `xdg-mime`; default-terminal mechanism unverified |
| Updates | Velopack | Per packaging format (section 3) |
| Accessibility | UI Automation (unverified) | AT-SPI2 backend in Avalonia 12.0 (vendor claim) |
| In-app PR review and merge | Yes (C25) | [PROPOSED] Yes, same flow (setting `linux.pr_merge`) |

Avalonia supports X11 by default, Wayland as opt-in from 12.1.0, and lists Ubuntu 25.x, Fedora 43 and Debian 13 as Tier 1 (vendor docs). **[PROPOSED]** v1 supports Tier 1 only (setting `linux.supported_distros`).

### 2. PTY terminal backend [PROPOSED]
- One interface, `ITerminalBackend`, on the client: ConPTY on Windows (doc 28), PTY on Linux.
- The human's local terminal runs on the Linux client (C31). The Linux server host needs no PTY for this client.
- Agent shells run in VMs and are streamed by the server. Their transport is not part of this doc (see docs 16 and 17).
- Linux implementation: `openpty`/`forkpty` through P/Invoke, or a NuGet PTY package. Licences and maintenance are unverified and must pass the OSI-only rule (C23).

```csharp
public interface ITerminalBackend {
    Task<ITerminalSession> OpenAsync(TerminalOptions options, CancellationToken ct);
}
```

### 3. Client packaging [PROPOSED]

| Format | For | Against |
|---|---|---|
| Self-contained tarball | Baseline; no sandbox | No update channel |
| AppImage | One file, no root | Update tool (AppImageUpdate) unverified |
| Flatpak | Sandbox; Flathub updates | Terminal reach into the host needs sandbox holes; unverified |
| deb / rpm | Native installs | We would host and sign our own repositories |

**[PROPOSED]** Ship self-contained builds (no Native AOT in v1, per Bazaar.Server.md) as a tarball and an AppImage. Spike Flatpak. Defer deb and rpm. Whether Velopack covers Linux is unverified.

### 4. Installers and update paths [PROPOSED]
- **Windows client:** Velopack, approved as the client updater and packaging tool (DECISIONS-LOG round 10, C37). Per-user install under `%LocalAppData%`, delta updates from GitHub Releases (verified in Velopack docs). Velopack does not sign, does not cover Windows Services and does not document machine-wide installs. `VelopackApp.Build().Run()` must be the first call in the entry point.
- **Windows server:** Worker Service with `AddWindowsService()`. Installer as **MSI built with WiX v5** (unverified), which registers the service, keeps data under `%ProgramData%` and upgrades with MajorUpgrade. MSIX is an open gap.
- **Linux server:** tarball plus install script: dedicated `bazaar` user, `/etc/bazaar/` config holding only the bootstrap secrets (KEK and database credential, C05), `/var/lib/bazaar/` state, systemd unit with `Type=notify` (unverified) and `Restart=on-failure`. Releases sit in `/opt/bazaar/releases/<version>` behind a `current` symlink, so switching or rolling back is one atomic step. The installer opens no inbound port (C30). Bundling `pg_search` is setting `packaging.pg_search_bundling`; counsel reviews it before any bundled build (C23).
- **Android:** Play closed test (verified: 12 testers, 14 continuous days, personal accounts created after 13 Nov 2023). Play needs an AAB (unverified). Keys live in Play App Signing. Push is a bare wake (C41), so Android uses no keep-alive foreground service (doc 29).
- **Winget:** publication timing is setting `packaging.winget_publish`. Manifests point at a released installer URL and SHA-256. Publication rules unverified.

### 5. Versioning and channels
- **[DECIDED (owner, 2026-10-25)]** SemVer versions; pre-release builds are `v0.x`; channels LTSC, Stable, Beta, Alpha and an owner-only Latest-Commit channel, chosen by the operator; Stable, Beta and Alpha are git tags on one history; LTSC is a real branch. Rules live in `docs/VERSIONING.md` (not yet written).
- **[PROPOSED]** One tag `vX.Y.Z` builds all components together.
- **[PROPOSED]** The operator's channel is setting `update.channel` (default Stable). Latest-Commit is never offered to operators. Server upgrades are always operator-run; automatic server updates are off (`packaging.server_auto_update`).
- The protocol version scheme is doc 02's (`major.minor`, current and previous major). This doc does not define an integer. `ReleaseManifest` carries the range as strings.

### 6. Server upgrade and rollback [PROPOSED]
1. The installer reads the installed app and schema versions and refuses a downgrade.
2. Stop the service and drain agent turns (doc 06). Wait for the process to exit before replacing files; a hard kill can skip `StopAsync`.
3. Take a `pg_dump` custom-format backup and verify it restores (doc 33). Backup retention follows the erasure window (C01).
4. Run migrations as an explicit step (`Bazaar.Server.Host migrate`), under an advisory lock. Expand-then-contract only. Startup never migrates; it refuses a schema mismatch (C44).
5. Start the new binary. Pass the ready probe (doc 03, `/health/ready`) or roll back.

Rollback switches `current` back. An old binary refuses to start if the schema is newer than it knows. A database restore is a last resort (Conflict 3).

### 7. Signing [PROPOSED; counsel review before first public release]

| Option | Cost | Status |
|---|---|---|
| Unsigned | None | Owner-only betas; SmartScreen warnings |
| SignPath Foundation | Free for OSS (verified, vendor page) | **Chosen route** (C23). Conditions (secondary): OSI-only components, already released in signed form, automated build |
| Azure Artifact Signing | Quota tiers verified; USD prices did not render | Not chosen. Eligibility is secondary (individuals in US/Canada, or organisations in EU/UK) |
| OV or EV certificate | Not researched | Not chosen |

Scope: Windows executables, MSI and Velopack packages. Linux: SHA-256 sums plus a detached signature (GPG or Sigstore, unverified). Android: Play App Signing. SignPath holds its own key, so no certificate sits in the repository. Timing is setting `packaging.signing_mode`.

### 8. Release pipeline [PROPOSED]
- Exists: `ci.yml` (build and test on `ubuntu-latest` and `windows-latest`, gitleaks) and `pr-sponsor.yml`. No release workflow yet.
- New `release.yml`, started only by the owner (`workflow_dispatch` with a version input). Its publish job runs in a GitHub environment with the owner as required reviewer, and is the only job with `contents: write`. Actions stay pinned to commit SHAs.
- Stages: build and test, pack, sign, SBOM and NOTICE, checksum, then a **draft** release that the owner promotes. Runners stay GitHub-hosted.

## Data and interfaces (sketches)

```csharp
public sealed record ReleaseManifest(
    string AppVersion, string MinProtocol, string MaxProtocol,
    string SchemaVersion, IReadOnlyList<ReleaseArtifact> Artifacts);

public sealed record ReleaseArtifact(string Os, string Format, string Sha256, string SignatureRef);
```

Handshake (format per doc 02): `{ "component": "client-linux", "appVersion": "0.0.0", "protocolVersion": "<major.minor per doc 02>" }`.

### Settings

Registry owned by doc 03 (round 13). Each change is audited (doc 15 `audit_log`); surfaced in the setup wizard and advanced settings (docs 27, 32).

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `update.channel` | enum: LTSC, Stable, Beta, Alpha, Latest-Commit | Stable [PROPOSED] | install | operator; Latest-Commit owner-only | No |
| `packaging.server_auto_update` | bool | false | install | operator | No; upgrades stay operator-run |
| `packaging.sideload_apk` | bool | true | install | owner | No |
| `packaging.signing_mode` | enum: unsigned-beta, signpath | unsigned-beta | install | owner | Needs counsel before signpath is used for public builds |
| `packaging.pg_search_bundling` | enum: operator-installed, bundled | operator-installed | install | owner | Needs counsel before `bundled` |
| `packaging.winget_publish` | enum: after-first-stable, manual | after-first-stable | install | owner | No |
| `linux.supported_distros` | list | Ubuntu 25.x, Fedora 43, Debian 13 | install | operator | No; other distros are unsupported |
| `linux.pr_merge` | bool | true | install | owner | No (server still requires the owner's signed approval) |

## Behaviour and edge cases
- Client updates never touch the server, which must accept the previous protocol major during that window.
- Installers must not replace files while the service runs. A failed migration leaves the old binary in place. `CREATE INDEX CONCURRENTLY` needs its own step.
- Keys and signing tokens never reach logs or release notes.
- The Linux client holds no GitHub credential; PR actions go through the server (doc 21).

## Testing
- Version negotiation: table tests for accept, upgrade-required and refuse-downgrade, using doc 02's `major.minor` rule.
- Migrations on real Postgres: upgrade N-1 to N with data; roll back to N-1 on an expand-only schema; startup refuses a mismatched schema.
- Service install: fake host in CI; real install on the NUC is a spike. Linux unit: `systemd-analyze verify`.
- PTY on `ubuntu-latest` with a real PTY; Linux client via Avalonia.Headless; packaged builds smoke-tested headless.
- Release gate: a script verifies every checksum and signature before the draft is created.
- Channel selection: table tests that each `update.channel` value maps to the right source and that Latest-Commit is refused for non-owner installs.

## Work packages
1. `ReleaseManifest` and handshake records in `Bazaar.Contracts`, with tests.
2. Server `migrate` subcommand and startup schema guard (needs doc 04).
3. `ITerminalBackend` and the Linux local PTY (no blocking conflict now).
4. Linux server package and smoke test (needs 2).
5. Windows server MSI spike, then build (needs 2).
6. Velopack pack spike for the Windows client (needs doc 28's head).
7. `release.yml` draft: environment gate, checksums, draft release, no signing yet.
8. SignPath integration (route chosen; timing per setting `packaging.signing_mode`).
9. Linux client head on the shared UI (needs docs 27 and 28); `.desktop` and `xdg-mime`; Monaco only after the WebKitGTK spike; Flatpak spike.
10. SBOM and NOTICE from NuGet in CI.
11. In-app PR review and merge on Linux, if setting `linux.pr_merge` is on (needs doc 21).
12. `docs/VERSIONING.md` (owner's versioning rules; not yet written).

## Conflicts
1. **Monaco on Linux and the Avalonia gate (C26).** If the free-route Monaco spike fails on Windows or Linux, the Avalonia decision itself is reopened, and Linux scope may shrink. Evidence: no source shows Monaco in an Avalonia WebView (unverified).
2. **Velopack and the server.** Velopack does not cover Windows Services, machine-wide installs or signing, so the server needs its own installer track.
3. **Rollback versus the append-only log (C10, C01).** A database restore discards events written after the backup. The restore policy in doc 33 (sequence floor plus recovery entry) is PROPOSED and not yet confirmed. Doc 33 must confirm it or the owner must accept the loss window.
4. **Linux `pg_search` and the OSI-only build (C23).** The Linux server may need `pg_search`, whose bundling needs counsel. Until counsel answers, the default is operator-installed (`packaging.pg_search_bundling`).
5. **KEK custody on a new host (C05; doc 33 C1).** The Linux server's bootstrap secret has the same open recovery question as Windows.
6. **Android distribution.** The owner chose the Play closed test. The research suggested sideloading the owner's devices first. Setting `packaging.sideload_apk` (default on) keeps both.

**Verification status.** Verified: Velopack overview; Play closed-test rule; Avalonia Linux tiers, X11 and Wayland; Avalonia paid tiers; SignPath free for OSS (vendor page); Azure quota tiers. Vendor claim: AT-SPI2. Secondary: SignPath conditions; Azure eligibility. Unverified: Velopack on Linux and services; WiX; MSIX; sd_notify; AppImage and Flatpak; SmartScreen; winget; EV prices; Play fee; AAB packaging. Owner decisions are recorded as decided in DECISIONS-LOG and are not verified claims.

## Questions for the owner

ANSWERED: Velopack is approved as the Windows client updater and packaging tool; its spike still gates adoption (DECISIONS-LOG round 10, C37).
ANSWERED: Terminal location: the human's local terminal runs in the client; agent terminals are streamed by the server (DECISIONS-LOG round 4, C31).
ANSWERED: Signing route is SignPath on an OSI-only build (DECISIONS-LOG round 4, C23); timing is setting `packaging.signing_mode`.
ANSWERED: Protocol version scheme belongs to doc 02, so this doc no longer proposes an integer (DECISIONS-LOG round 12, C40).
ANSWERED: Versioning is SemVer, with the channel set and tag/branch model in the owner's 2026-10-25 note; see `docs/VERSIONING.md` (section 5).

1. **Linux v1 scope.** Default: chat, Kanban, Constellation, Vault, palette, theme and the local terminal (C31 decided the Windows terminal only, so Linux needs confirming). Monaco only if its free-route spike passes (C26).
2. **Linux format.** Default: tarball and AppImage now; Flatpak after its spike; deb and rpm later.
3. **Windows server installer.** Default: MSI via WiX. MSIX is out of v1 until spiked.
4. **Linux server package format.** Default: tarball with install script and systemd unit first; deb and rpm later.
5. **Release gate.** Default: owner-only trigger, required reviewer on the environment, drafts first.
6. **Rollback policy.** Default: forward-only migrations and binary rollback. A database restore needs your acceptance of the loss window, or doc 33 provides replay (C10 restore policy is PROPOSED).

MOVED TO SETTINGS: linux.supported_distros, packaging.sideload_apk, packaging.signing_mode, packaging.pg_search_bundling, packaging.winget_publish, packaging.server_auto_update, linux.pr_merge (DECISIONS-LOG round 13)
