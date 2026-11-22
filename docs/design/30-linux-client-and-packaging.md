# Linux client, packaging, updates and signing

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 6
Depends on: 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md, 21-github-integration.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md · Used by: 32-remote-access-auth-and-privacy.md, 33-backup-dr-and-export.md, 34-testing-strategy.md, 35-spikes-plan.md

> **Owner decision (2026-11-12):** each company builds its own Linux client; the repo ships server builds only (doc 37).

## Purpose
Defines the Linux desktop client (`Bazaar.Client.Linux`), built last on the shared Avalonia UI, and how every Bazaar component is packaged, versioned, updated and signed. Server installs (Windows Service and systemd) are included because they share the upgrade and rollback rules.

## Decided inputs
- .NET 11 and one C# codebase; Avalonia clients on `Bazaar.Client.Core` (CLAUDE.md). Avalonia stays conditional on the Monaco hosting gate (round 5, C26).
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
| Default-app registration | Windows default-app APIs | `.desktop` entries and `xdg-mime`, in module `Bazaar.Client.DefaultApp.Linux` (see the note below the table); default terminal: see "Default terminal" below (verified 2026-11-22) |
| Updates | Velopack | Per packaging format (section 3) |
| Accessibility | UI Automation (unverified) | AT-SPI2 backend in Avalonia 12.0 (vendor claim) |
| In-app PR review and merge | Yes (C25) | [PROPOSED] Yes, same flow (setting `linux.pr_merge`) |

**Default-app registration, implemented (module `Bazaar.Client.DefaultApp.Linux`, with `.Abstractions`).** Same rule as Windows (CLAUDE.md, doc 37): register a handler and guide the user, never claim a silent default. `IDefaultAppRegistrar` has `RegisterAsync`, `QueryAsync` and `UnregisterAsync`. Register writes a per-user entry at `$XDG_DATA_HOME/applications/bazaar.desktop` (default `~/.local/share`), written to a temporary name and renamed, and left untouched when the text is already right. It runs `xdg-mime default <id> <type>` and `update-desktop-database <dir>` through an injectable `IProcessRunner` (argument list, no shell), then reads the result back with `xdg-mime query default <type>`. The result is `Registered` only when every type reports Bazaar's entry, `NotDefault` otherwise (with guidance text naming the GNOME or KDE settings page, or a generic one, plus the `xdg-mime` command), and `Unavailable` when `xdg-mime` is missing, fails or times out. A missing `update-desktop-database` is ignored. Unregister deletes the entry only if it carries the `X-Bazaar-Managed=true` key, so a user's own file is never removed; `xdg-mime` has no unset command, so a stale `mimeapps.list` line may remain and is ignored by desktops once the entry is gone. Fail closed: `ExecPath` must be absolute with no quotes, newlines or control characters; it is quoted per the Desktop Entry spec when it holds reserved characters, `%` is doubled, then backslashes are escaped at the string level. The desktop file ID and MIME types are validated. **Theme, implemented (doc 93 Themes).** `LinuxTheme` applies a bundled theme (Void default, Graphite, Paper from `Bazaar.Client.Theme`) at start: `BAZAAR_THEME` overrides `--theme <name>` (or `--theme=<name>`), which overrides a stored preference; an unknown name logs and falls back to Void. The validated tokens become one `ResourceDictionary` in `Application.Resources.MergedDictionaries` under the keys `AvaloniaThemeGenerator` emits (`Bazaar.<token>`, `Bazaar.brush.<token>`), so `DynamicResource` consumers follow a switch. `LinuxThemeCommands.All` lists the palette commands `theme.void`, `theme.graphite` and `theme.paper`, and `Run` switches at runtime; no palette view lists them yet, and the preference store is not wired. Tests: `tests/Bazaar.Client.Linux.Tests/LinuxThemeHeadlessTests.cs`.

**[PROPOSED]** The registered types are a setting (`MimeTypes`, default `x-scheme-handler/bazaar`, the sign-in hand-off of doc 45); no doc names a file type, so none is registered by default. The default terminal is a separate interface, see **Default terminal** below. **The head wires it:** `Bazaar.Client.Linux` takes `--register-default-app`, `--default-app-status` and `--unregister-default-app` (`DefaultAppCommandLine`), composes `XdgDefaultAppRegistrar` with `Environment.ProcessPath`, prints the status text (`Registered`, `Not the default` with the guidance, or `Unavailable`) and exits without starting the UI: 0 when the desktop reports Bazaar as the default (for removal, when the entry is gone), 1 otherwise. A process path that is not absolute, or is the `dotnet` host, is refused with guidance and exit 1. `DefaultAppCommands.All` lists the matching local palette commands (`defaultapp.register`, `defaultapp.status`, `defaultapp.unregister`) for the palette to load. Tests: `tests/Bazaar.Client.Linux.Tests` (fake registrar) and `tests/Bazaar.Client.DefaultApp.Linux.Tests` (fake runner and a temp data directory; the real `xdg-mime` test skips off Linux or without the tool).

**Session persistence, implemented (head `Bazaar.Client.Linux`: `LinuxClientSessions`, `Secrets/LinuxSecretProtector`).** The first-run sign-in (doc 45 section 2.6) keeps its session across restarts through `Bazaar.Client.SignIn.Storage`'s `ProtectedFileSessionStore` (`session.bin` under the XDG state directory, mode 0600). `LinuxSecretProtector` is the head's `ISecretProtector`: AES-256-GCM over the session JSON (version byte, nonce, tag, ciphertext, purpose bound as associated data). The 32-byte key lives in the desktop keyring through `secret-tool` (attributes `service bazaar kind session-key`; arguments are an argument list, the key goes on stdin only, never on a command line). When `secret-tool` or a Secret Service is missing or refuses, the head falls back to a separate key file `session.key` (mode 0600, directory 0700) and says so on stderr (`Session store: no keyring found; key held in a private file ...`); this matches the doc 14 key-custody default `file`. If neither can hold a key the session stays in memory and the head says so. A wrong key, an altered or short file, or a file sealed under another custody reads as signed out and the file is removed (fail closed). Nothing secret is logged. On start, with `BAZAAR_SERVER` and `BAZAAR_GC` set and no `BAZAAR_TOKEN`, the head takes the saved access token (refreshing it first when expired; a failed refresh leaves the token unset as before); a token in the environment always wins. With no settings, no saved session and no first-run switch, the head adds `--sign-in` itself (never on a warm preload start). The group chat id is not part of the session, so it still comes from `BAZAAR_GC`. Tests: `tests/Bazaar.Client.Linux.Tests` (`LinuxClientSessionsTests`: fake runner and temp directories).

**Default terminal (verified 2026-11-22; module `Bazaar.Client.DefaultApp.Linux`, interface `IDefaultTerminalRegistrar`).** Linux has no single default-terminal setting. Findings, checked in the build container where a tool exists (container had `update-alternatives`, `x-terminal-emulator` and `gsettings`, but no `xdg-terminal-exec`, `kreadconfig5/6` or `xdg-mime`, and no GNOME schema) and otherwise from primary sources:

| Mechanism | Read without root | Set per user without root | Desktops | Source |
|---|---|---|---|---|
| `xdg-terminals.list` (xdg-terminal-exec spec; also `<desktop>-xdg-terminals.list`, desktop from `$XDG_CURRENT_DESKTOP`) in `$XDG_CONFIG_HOME`, then `$XDG_CONFIG_DIRS`, then `$XDG_DATA_DIRS/xdg-terminal-exec` | Yes, plain files; first explicit entry ID wins; `-id` excludes, `+id` protects, `#` comments, `id.desktop:action` selects an action | Yes, edit `$XDG_CONFIG_HOME/xdg-terminals.list`. The spec defines no tool that writes it. A desktop-specific file in the same directory ranks above the plain one | Any desktop whose launchers use xdg-terminal-exec; GLib tries it first (per GNOME Discourse and Ubuntu bug reports, secondary). The spec is still a proposal, not adopted freedesktop.org | https://github.com/Vladimir-csp/xdg-terminal-exec (README); https://man.archlinux.org/man/extra/xdg-terminal-exec/xdg-terminal-exec.1.en |
| Terminal desktop entry | Entry must carry `Categories=TerminalEmulator` | Yes, a file under `$XDG_DATA_HOME/applications` | Same as above | xdg-terminal-exec README |
| Debian `x-terminal-emulator` (update-alternatives) | Yes, `update-alternatives --query x-terminal-emulator` | No. The links live in `/etc/alternatives`, so `--set` needs root. Options `--altdir` and `--admindir` redirect directories, but that changes nothing for other programs | Debian, Ubuntu and derivatives | https://manpages.debian.org/unstable/dpkg/update-alternatives.1.en.html |
| GNOME `org.gnome.desktop.default-applications.terminal exec` | Yes, `gsettings get`, only where the schema is installed (it was absent in the container) | Technically `gsettings set`, but current GNOME ignores the key (it is labelled deprecated; terminal choice moved to GIO and xdg-terminal-exec). Not verified against a GNOME release note | GNOME | https://discourse.gnome.org/t/default-terminal/1307; https://bbs.archlinux.org/viewtopic.php?id=254020 (secondary) |
| KDE `kdeglobals` `[General]` `TerminalApplication` and `TerminalService` | Yes, `kreadconfig6` or `kreadconfig5` (not in the container) | Technically `kwriteconfig6/5`, but KDE bug reports say a stale `TerminalService` makes Konsole win regardless. No KDE documentation page spells out the semantics | KDE Plasma | https://bugs.kde.org/show_bug.cgi?id=421311; https://bugs.kde.org/show_bug.cgi?id=461998 (secondary). KIO `kterminallauncherjob.cpp` could not be fetched, so the key semantics are unverified against source |
| `x-scheme-handler/terminal` | `xdg-mime query default` | `xdg-mime default` | None. A proposal that never became a spec; only third-party tools such as handlr use it | https://lists.freedesktop.org/archives/xdg/2015-October/013591.html; https://github.com/chmln/handlr (secondary) |

**[PROPOSED] Approach.** Bazaar's terminal ships a separate `bazaar-terminal.desktop` (`Categories=TerminalEmulator;System;`, `X-Bazaar-Managed=true`, no MIME types), written with the same validation and escaping as the handler entry. `RegisterAsync` (only when the user asks) writes that file and puts its ID first in `$XDG_CONFIG_HOME/xdg-terminals.list`, keeping the user's other lines and comments, then reads the configuration back. `QueryAsync` reads the lists in spec priority order and reports `Registered` only when Bazaar's ID is first; a desktop-specific list naming another terminal is reported as winning. It also reads (never writes) the GNOME key and KDE `TerminalService` and mentions them. `UnregisterAsync` removes only Bazaar's lines and its own managed entry. Bazaar never sets the GNOME or KDE keys and never uses root; for Debian alternatives the guidance prints the `sudo update-alternatives --install` and `--set` commands. All tools run through `IProcessRunner` with an argument list. The head takes `--register-default-terminal`, `--default-terminal-status` and `--unregister-default-terminal` (`DefaultTerminalCommandLine`, same exit codes as the default-app flags) and lists `DefaultTerminalCommands.All` for the palette. Open: how xdg-terminal-exec launchers pass a command to Bazaar's terminal (`ExecArg` handling) is not verified, so the entry's `Exec` has no arguments yet. Tests: `tests/Bazaar.Client.DefaultApp.Linux.Tests` (fake runner, temp dirs; the real `gsettings` test skips when missing) and `tests/Bazaar.Client.Linux.Tests`.

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

**[DECIDED (owner card, 2026-11-19)]** Build all four Linux packaging items, each as its own unit (doc 37 lists them as company-build outputs, built by the company server, one click): AppImage (`packaging/linux-appimage/`), Flatpak (`packaging/linux-flatpak/`), deb and rpm (`packaging/linux-deb/`, `packaging/linux-rpm/`), and in-place updates (client module `Bazaar.Client.Updater.Linux` with `Bazaar.Client.Updater.Linux.Abstractions`, per the server and client module split). The packaging outputs are build scripts and manifests, not .NET modules; only the updater is code. Each ships with tests and a CI smoke step, and builds only from a tool the operator supplies (the scripts never download `appimagetool`, `flatpak-builder` or `dpkg-deb`). This supersedes the earlier "defer deb and rpm" line for this release train; Snap and store pushes stay parked (doc 37).

**Implemented:** `packaging/linux/build-tarball.sh` publishes the self-contained client (`linux-x64` or `linux-arm64`) and writes a reproducible `Bazaar-<version>-linux-<arch>.tar.gz` with a `.sha256` file (sorted names, fixed owner and times, `gzip -n`; two builds of one commit give the same bytes). `--verify` extracts the archive and starts the client on Xvfb. The Flatpak (`packaging/linux-flatpak/`) packages that same self-contained tarball: its manifest compiles nothing and installs the prebuilt archive (by `sha256`) into `/app/lib/bazaar-client`, so no .NET SDK extension is needed (none exists for .NET 11) and only `org.freedesktop.Platform` and `org.freedesktop.Sdk` at the pinned branch are required. `build-flatpak.sh` builds the tarball for `x86_64` or `aarch64`, renders the manifest template with its path and checksum into a scratch directory, and runs `flatpak-builder` only if the operator has installed it. The AppImage script is `packaging/linux-appimage/build-appimage.sh`; it needs an `appimagetool` the operator supplies. The AppImage script is `packaging/linux-appimage/build-appimage.sh`; it needs an `appimagetool` the operator supplies.

**[PROPOSED (2026-11-22)]** Flatpak application id: `io.github.codex_alchemist_dev.BazaarClient` (reverse DNS of the GitHub org; Flatpak requires at least two periods, so the earlier `TheBazaarAuthors.BazaarClient` was invalid). Applies to the manifest, metainfo, desktop file, icon and bundle names. The owner may replace it with an id on a domain the project owns before the first public release.

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

**Implemented (Linux):** `packaging/linux/sign-tarball.sh` writes a detached ASCII-armoured OpenPGP signature (`<tarball>.asc`) with the key the operator names in `BAZAAR_SIGNING_GPG_KEY`; it refuses to run without one and the key never sits in the repository. `packaging/linux/verify-tarball.sh` checks the SHA-256 file and then the signature against a public key the verifier already trusts; the release gate calls it. Sigstore is not built; OpenPGP is the verified-available route.

### 8. Release pipeline [PROPOSED]
- Exists: `ci.yml` (build and test on `ubuntu-latest` and `windows-latest`, gitleaks) and `pr-sponsor.yml`. No release workflow yet.
- New `release.yml`, started only by the owner (`workflow_dispatch` with a version input). Its publish job runs in a GitHub environment with the owner as required reviewer, and is the only job with `contents: write`. Actions stay pinned to commit SHAs.
- Stages: build and test, pack, sign, SBOM and NOTICE, checksum, then a **draft** release that the owner promotes. Runners stay GitHub-hosted.

## Data and interfaces (sketches)

```csharp
// Bazaar.Contracts.Releases. Immutable; validated on construction (ArgumentException); decoded through IWireCodec.
public sealed record ReleaseManifest(
    string Channel, string AppVersion, string MinProtocol, string MaxProtocol,
    string SchemaVersion, string SigningKeyFingerprint, IReadOnlyList<ReleaseArtifact> Artifacts);

public sealed record ReleaseArtifact(
    string Os, string Architecture, string Format, string Url, long SizeBytes,
    string Sha256, string? Sha256Url, string? SignatureUrl, string? Signature);
```

Final field set. `ReleaseManifest` is the one shared release contract: the Linux updater (module `Bazaar.Client.Updater.Linux`) and the Windows update feed client (doc 37 section 4b) both read it. The Linux artifacts are described here; the Windows ones and the `UpdateManifest` field mapping are in doc 37 section 4b.

- `Channel` is one of the exact `update.channel` values (`LTSC`, `Stable`, `Beta`, `Alpha`, `Latest-Commit`); the version's pre-release suffix must match it (Stable and LTSC none, Beta `-beta.N` or `-rc.N`, Alpha `-alpha.N`, Latest-Commit `-dev.N`), so a feed cannot mislabel a build.
- `AppVersion` is a SemVer string with no leading `v`, optionally with `+commit` build metadata.
- `MinProtocol` and `MaxProtocol` are doc 02 `major.minor` strings, min not above max.
- `SchemaVersion` is the database schema the build needs (doc 04), either the schema digest of doc 04 (`sha256:` plus 64 lowercase hex characters, from `SchemaDigest.Compute`) or a legacy short token of 1 to 32 letters, digits, `.`, `_` and `-`; it is compared for equality only; the updater uses it with the downgrade refusal of section 6.
- `SigningKeyFingerprint` is the lowercase hex identity (40 or 64 characters) of the key behind every signature in the manifest: the OpenPGP fingerprint for Linux (the updater accepts a `.asc` only from a key it already trusts with that fingerprint, section 7), the pinned certificate thumbprint for Windows (doc 37 section 4b).
- `Artifacts` holds 1 to 64 builds, all for one `Os` (one signing scheme and key per manifest, so a feed serves one manifest per OS), at most one per architecture and format.
- `Os` is `linux` or `windows`.
- `Architecture` is the .NET runtime identifier: `linux-x64`, `linux-arm64`, `windows-x64` or `windows-arm64`; it must belong to `Os`.
- `Format` is `tar.gz` or `appimage` for Linux (section 3; deb and rpm stay deferred) and `msix` or `velopack` for Windows; it must belong to `Os`.
- `Url` is where the package downloads from: absolute `https`, or relative to the manifest's own address; no credentials, query or fragment. Plain `http` is refused.
- `SizeBytes` is the package size (1 byte to 4 GiB); the updater aborts a download that exceeds it.
- `Sha256` is the lowercase 64-character hex SHA-256 of the package bytes.
- `Sha256Url` points at the `.sha256` file that `build-tarball.sh` writes; the updater checks it against the inline `Sha256` and refuses on any difference. Required for Linux, optional for Windows.
- `SignatureUrl` points at the detached OpenPGP `.asc` over the package bytes. It replaces the earlier `SignatureRef` placeholder. Required for Linux, optional for Windows (where it is not the trust path).
- `Signature` is the base64 in-band signature of the Windows path (doc 37 section 4b). Required for Windows, forbidden for Linux, so each OS has exactly one trust path.

The records are registered in `WireJsonContext`; a payload that breaks these rules decodes as a bad frame (`WireFormatException`). The manifest is untrusted until both the hash and the signature verify.

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
| `update.skip_versions` | list of SemVer | empty | install | operator | No |
| `update.max_package_bytes` | bytes | 4 GiB (the contract maximum) | install | operator | No; may tighten, never exceeds the manifest's own bound |
| `update.max_extracted_bytes` | bytes | 4 GiB | install | operator | No |
| `update.max_archive_entries` | int | 100000 | install | operator | No |
| `update.gpg_path` | path | `gpg` on the PATH | install | operator | No |
| `update.gpg_timeout_seconds` | int | 60 | install | operator | No |

### Linux client updater (module `Bazaar.Client.Updater.Linux`)

Implements sections 4, 5 and 7 for the tarball (owner card 2026-11-19; the company server serves the build, doc 37 sections 4a and 4b). Velopack is not used on Linux. The module is `Bazaar.Client.Updater.Linux.Abstractions` (`ILinuxUpdater`, `IReleaseSource`, `IUpdateFileSystem`, `IProcessRunner`, `IClientCompatibility`, `LinuxUpdaterSettings`, `UpdateResult`) plus `Bazaar.Client.Updater.Linux`. It references `Bazaar.Contracts` only. The head runs it.

- **Decision order.** Channel (the configured `update.channel` must be a known name; Latest-Commit is refused unless the install is the owner's), the rollback record (an unreadable `rolled-back.json` refuses everything, `SkipRecordInvalid`), manifest (`Channel` must equal the configured channel), SemVer precedence against the running build (build metadata ignored; older is refused as a downgrade, equal is up to date), the skip rule, compatibility, a `tar.gz` for this architecture, the size against `update.max_package_bytes`, and `SigningKeyFingerprint` against the key pinned in the build. Only then does it download.
- **Skip rule.** A version listed in `update.skip_versions` is never offered or installed (`VersionSkipped`). `Rollback` first writes the version it rolls back from to `rolled-back.json` under the install root (owner-only file, written to a temporary name and renamed, so it is never torn; the highest version rolled back from is kept). Any version at or below the recorded one is refused until a newer release appears. The record survives restarts. A damaged record fails closed with a message naming the file; a later `Rollback` replaces it.
- **Compatibility.** The caller supplies an `IClientCompatibility`: the protocol the running build speaks, the server's current protocol, whether the server accepts the previous major (`wire.previous_major_supported`, doc 02) and the server's schema version. Values are read on every check. If one is missing or malformed the check fails closed (`CompatibilityUnknown`). Rules, all from the decided text: doc 02 versions are `major.minor`, minors only add, and the server accepts its current major and, if the setting allows, the previous one; doc 04 has the server refuse to run on a schema older or newer than its binary expects. So a build is refused when its highest protocol is below the running build's (`ProtocolDowngradeRefused`), when every major it speaks is below what the server accepts and the server would answer `upgrade_required` with 4426 (`ProtocolBuildTooOld`), when its lowest major is above the server's, so the operator must upgrade the server first (`ProtocolBuildTooNew`), and when its `SchemaVersion` is not exactly the server's (`SchemaMismatch`). Within an accepted major a lower or higher minor is accepted. Doc 06 adds no client rule. The schema comparison is exact string equality because doc 02 and the manifest both treat the schema as an opaque token.
- **Verification.** The download is cut off at the declared size. The inline `Sha256`, the `.sha256` file and the bytes must all agree. Then `gpg` verifies the detached `.asc` in a fresh, empty, owner-only `GNUPGHOME` under the temp directory (short path, because the agent socket path is limited), after importing only the key file baked into the build. The check needs a `GOODSIG` and a `VALIDSIG` whose primary-key fingerprint is the pinned one, and no `BADSIG`, `ERRSIG`, `EXPSIG`, `EXPKEYSIG`, `REVKEYSIG` or `NO_PUBKEY`. The user's own keyring is never read. The key file and fingerprint are part of the company identity of the build (doc 37 section 4a), not settings.
- **Source.** `HttpReleaseSource` fetches `GET /updates/latest` over the head's pinned `HttpClient`. A relative URL resolves against the manifest address. An absolute URL, or a redirect, to another scheme, host or port is refused: the client never fetches code from anywhere except its own server.
- **Extraction.** Into `releases/.partial-<id>`, then one directory rename to `releases/<version>`. Directories, regular files and relative symbolic links only. The extractor refuses absolute names, `..`, backslash and NUL in names, hard links, devices, duplicates, a link that climbs above the release or climbs after a name, any entry written through a link the archive created, and archives above `update.max_archive_entries` or `update.max_extracted_bytes`. Modes keep the read and execute bits and the owner write bit; setuid, setgid, sticky and group or other write are dropped.
- **Switch and rollback.** Layout under the install root: `releases/<version>`, symlinks `current` and `previous` (relative targets `releases/<version>`), and a `staging` directory emptied after each run. Each link is replaced by creating a temporary link and renaming it over the old one with `rename(2)`, so `current` never disappears. A new install sets `previous` to the old `current` first, then `current`. Only the releases `current` and `previous` name are kept. `Rollback` swaps the two links and accepts only a `previous` of the form `releases/<semver>` whose directory exists; rolling back twice rolls forward. A failed check leaves both links alone.
- **Not decided here.** One updater runs per install root at a time; the head guarantees that.

## Behaviour and edge cases
- Client updates never touch the server, which must accept the previous protocol major during that window.
- Installers must not replace files while the service runs. A failed migration leaves the old binary in place. `CREATE INDEX CONCURRENTLY` needs its own step.
- Keys and signing tokens never reach logs or release notes.
- The Linux client holds no GitHub credential; PR actions go through the server (doc 21).

## Testing
- Version negotiation: table tests for accept, upgrade-required and refuse-downgrade, using doc 02's `major.minor` rule.
- Migrations on real Postgres: upgrade N-1 to N with data; roll back to N-1 on an expand-only schema; startup refuses a mismatched schema.
- Service install: fake host in CI; real install on the owner's PC is a spike. Linux unit: `systemd-analyze verify`.
- PTY on `ubuntu-latest` with a real PTY; Linux client via Avalonia.Headless; packaged builds smoke-tested headless.
- Linux client start-up: the `linux-client-startup` CI job runs `scripts/ci/linux-client-startup.sh`, which starts the built head on Xvfb. The headless tests cannot see a start-up that aborts on a real display (missing `UseSkia()` and `UseHarfBuzz()` reached main that way). The default and explicit X11 starts must stay up; an explicit Wayland request on an X-only session must fail, not fall back.
- Release gate: a script verifies every checksum and signature before the draft is created.
- Channel selection: table tests that each `update.channel` value maps to the right source and that Latest-Commit is refused for non-owner installs.

## Work packages
1. `ReleaseManifest` and handshake records in `Bazaar.Contracts`, with tests.
2. Server `migrate` subcommand and startup schema guard (needs doc 04).
3. `ITerminalBackend` and the Linux local PTY (no blocking conflict now).
4. Linux server package and smoke test (needs 2).
5. Windows server MSI spike, then build (needs 2).
6. Velopack pack spike for the Windows client (needs doc 28's head). **Status (2026-11-20): script written, spike still OPEN.** `packaging/velopack/pack-client.ps1` runs `vpk pack` once per channel (`--channel win-x64-<channel>`, so each channel has its own `releases.<channel>.json` feed), signs the produced `*.exe` afterwards through `packaging/signing/signer.ps1`, and writes `FEED-MANIFEST.txt` (channel, ring, version, SHA-256 per file). It never uploads. Verified against Velopack docs: the flag names used and the per-channel `releases.{channel}.json` feed. Not verified: Setup.exe and `.nupkg` naming per channel, legacy `RELEASES-<channel>` output, vpk exit codes, signing of the app binaries inside the package (only the installer is signed today). No real `vpk` run has happened. The dry-run test (`test-pack-client.ps1`) had not been run when this note was written, because `pwsh` was not available in the authoring environment.
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
