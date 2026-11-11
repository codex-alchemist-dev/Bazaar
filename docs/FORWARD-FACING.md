# Forward-facing dependency tracking

Status: PROPOSED (tracking doc and mechanisms; adoption items are follow-up issues).
Owner: Bazaar maintainers (per-row owner to be confirmed by the owner).
Last audit: 2026-11-11.

## Why this exists

CLAUDE.md sets the forward-facing rule: write for the newest supported .NET and what
comes next, and move to each new release soon after it ships. That applies to .NET
itself and to every dependency whose new release changes what Bazaar can do well.

The target is day-0 support. When a dependency reaches stable, Bazaar already runs on
it, and it uses the new features and improvements. "It still runs" is not the goal.

This page does four things:

1. Inventories every dependency Bazaar pins, with where the pin lives.
2. Records the next or upcoming version we found, with its status and source quality.
3. Lists the features that would help Bazaar, and the adoption step for each.
4. Says which mechanism tracks each one: canary, Dependabot, or Renovate.

Verification levels used below:

- **Confirmed**: an official release page, changelog, or package listing was read.
- **Reported**: secondary coverage only (blog, news site). Check before acting.
- **Not found**: searched, nothing usable. Treat as unknown, not as "does not exist".

Facts about upcoming versions change quickly. Re-check this page at each wave start and
update the "Last audit" date when you do.

## Mechanisms

| Mechanism | Covers | Blocks merges? | Where |
|---|---|---|---|
| **Canary** (weekly plus on demand) | Build and test against the newest .NET 11 preview SDK on Linux; report outdated and prerelease NuGet packages in the job summary | No (non-required, `continue-on-error`) | `.github/workflows/canary.yml` |
| **Dependabot** (weekly, existing) | Stable NuGet and GitHub Actions updates, security updates | Only through normal PR review | `.github/dependabot.yml` |
| **Renovate** (proposed, not installed) | Prerelease tracking for a named list of NuGet packages, which Dependabot cannot follow | No | `renovate.json` |

Known limits:

- Dependabot has no documented option to opt in to prerelease versions. It proposes only
  the newest version that carries the same pre-release label as the current pin. So it
  cannot move Bazaar onto Npgsql 11 previews, for example.
- **Renovate install status: blocked, fallback in use.** The Renovate GitHub App install was started through a Cowork browser-control thread, and a permission check stopped the final Install click. We did not route around that check. Until an owner-approved install happens, the active mechanisms are the weekly Dependabot config and the canary workflow. Dependabot still cannot follow prereleases, so prerelease tracking is manual (the canary report and this page). `renovate.json` stays in the repo, inert, for when the install is approved.
- Renovate needs the Renovate GitHub App installed on `codex-alchemist-dev/Bazaar`.
  Installing an app is a GitHub settings change, so the owner's Cowork session does it
  (see the GitHub permissions note in project memory). Until then `renovate.json` does
  nothing.
- Once Renovate is installed, its tracked list overlaps Dependabot's stable updates for
  the same packages. The fix is to add `ignore` entries in `dependabot.yml` for exactly
  the packages Renovate tracks, while security updates stay on Dependabot. Do this in
  the same change that installs Renovate, not before.
- The canary does not yet cover PostgreSQL 19, Avalonia 12.2 preview, or Npgsql 11
  preview packages. Each needs its own canary job, added when its artifact exists. See
  the follow-up list.

## Inventory and status

Pins are read from `global.json`, `Directory.Packages.props`, `packaging/postgres/versions.txt`,
`packaging/oci/Dockerfile`, `packaging/oci/docker-compose.yml`, `.github/workflows/*.yml`,
and the `net11.0`/`net11.0-android` target frameworks in the project files.

### Runtime, language and SDK

| Dependency | Pinned at | Next or upcoming | Verification | Features that help Bazaar | Adoption step | Mechanism |
|---|---|---|---|---|---|---|
| .NET SDK and runtime | `global.json`: `11.0.100-rc.2.26504.105`, `rollForward: latestPatch`, `allowPrerelease: true`. Target `net11.0` in 49 projects. | .NET 11 GA. RC1 in Sept 2026, RC2 in Oct 2026, go-live license. GA tied to .NET Conf (starting 2026-11-10). | Reported (InfoQ, Visual Studio Magazine). The GA build number is not verified here. | Current supported APIs; C# 15 default for `net11.0` (RC1). | (1) Pin `global.json` to the GA SDK once it is on the SDK feed, and drop `allowPrerelease`. (2) Remove the RC pins of `Microsoft.Extensions.*` (see next row). | Canary (preview), Dependabot does not track the SDK pin. Owner action: confirm GA. |
| Microsoft.Extensions libraries (shared framework) | `11.0.0-rc.2.26504.105` for Hosting, Hosting.WindowsServices, Hosting.Systemd, Configuration, DependencyInjection, Options.ConfigurationExtensions | GA in .NET 11. Nine Microsoft.Extensions libraries moved into the shared framework in .NET 11 Preview 4 (Caching, Configuration, DependencyInjection, Diagnostics, FileProviders, Hosting, Logging Abstractions, Options, Primitives). | Confirmed (.NET 11 breaking change list, per search result). | Smaller app size, one servicing path. | Check the .NET 11 "extensions in shared framework" note. For each shared-framework assembly, decide whether the `PackageReference` stays (for net11.0 apps) or goes. Confirm with a build and a test run; do not guess. | Dependabot (stable), Renovate (tracked, prerelease). |
| C# language | `LangVersion=latest` in `Directory.Build.props` | C# 15 default for net11. Features reported: collection expression arguments, extension indexers (Preview 6), extended layout support. | Reported | Clearer code in the server and clients. | Adopt each feature with a test-backed change. Do not rewrite working code for style alone. | Canary (build warnings) |

### UI, rendering and graphics

| Dependency | Pinned at | Next or upcoming | Verification | Features that help Bazaar | Adoption step | Mechanism |
|---|---|---|---|---|---|---|
| Avalonia (`Avalonia`, `.Desktop`, `.Android`, `.Skia`, `.Themes.Fluent`, `.Fonts.Inter`, `.Headless.XUnit`) | `12.1.4` | Avalonia 12.1 is the newest release found with notes: native Wayland backend (graduated from private preview), rendering performance work, first release with an SBOM. 12.2 preview: not found. | Confirmed for 12.1 (Avalonia blog). Not found for 12.2. | Wayland backend for the Linux client. SBOM supports the OSS-only public build. | (1) Track Avalonia prereleases. (2) Keep `Avalonia.Headless.XUnit` on the same line as `Avalonia`. (3) Check the Wayland backend on the Linux head when the client exists. | Renovate (tracked, prerelease). Canary for 12.2 once published. |
| SkiaSharp (transitive through `Avalonia.Skia`; not pinned directly) | Not pinned by Bazaar | SkiaSharp 4.0 stable: 4.148.0 on 2026-06-29, with Skia m148 and the Graphite GPU backend (contributed by Uno Platform). Later 4.150.0 and a 4.151.0 prerelease line are reported. Breaking: obsolete `SKPaint` text APIs are errors; use `SKFont`. | Reported (Microsoft DevBlogs, InfoQ, third-party coverage). | Graphite is the GPU backend that matters for Constellation. Reported 24% GPU speedup is a vendor-side claim, not measured here. | Find out which SkiaSharp version `Avalonia.Skia` 12.1.4 resolves. If it is 3.x, the Graphite path is not available yet. Decide the pin with a benchmark, not a guess. | Follow-up issue. Canary covers it through the build. |
| Vulkan (API) | Used through Vortice (see next row) | Vulkan 1.4 is the newest core version found. Routine spec updates continue (1.4.361 on 2026-08-28, 1.4.363 later). No Vulkan 1.5 found. | Confirmed (Khronos docs, Phoronix). | Constellation renderer baseline: target Vulkan 1.4 core features on hardware that supports them, with fallback. | Set the minimum Vulkan API to 1.4 in the renderer probe, with a capability check and a fallback to Skia (`IConstellationRenderer`). Verify the feature list against the 1.4 spec before use. | Follow-up issue. |
| Vortice.Vulkan | `3.3.0` | Newer version reported on NuGet; the number and changes are not verified. | Not found (the search returned a 1.5.0 listing with .NET 6 targets, which does not settle it). | Binding updates for newer Vulkan. | Check NuGet and the Vortice release notes, then decide whether a bump is needed for the renderer. | Renovate (tracked, prerelease). |

### Data and storage

| Dependency | Pinned at | Next or upcoming | Verification | Features that help Bazaar | Adoption step | Mechanism |
|---|---|---|---|---|---|---|
| PostgreSQL | `packaging/postgres/versions.txt`: `PG_MAJOR=18`, `PG_VERSION=18.0`, `PG_EDB_BUILD=1`. CI and tests use `pgvector/pgvector:pg18`. Compose: `pgvector/pgvector:pg18`. | PostgreSQL 19: Beta 1 on 2026-06-04, Beta 2 on 2026-07-16. Final expected around Sept or Oct 2026. The GA date is not verified here. | Confirmed for Beta 1 and Beta 2 (postgresql.org news). GA not verified. | Reported: `pg_plan_advice`; SQL graph queries (SQL/PGQ, a possible fit for the Vault and Constellation link graph); `REPACK CONCURRENTLY`; parallel autovacuum workers; `io_method=worker` scales automatically; `ON CONFLICT DO SELECT` (may suit idempotent GC log writes); server-side SNI. | (1) Bump `PG_VERSION` to the latest 18.x patch release now, because `18.0` is the first point release. Needs a new SHA-256 in `versions.txt` and a passing bundle check. (2) Add a PG 19 canary (`pgvector` image when it exists, plus the bundle check). (3) Evaluate the graph query and `ON CONFLICT DO SELECT` features against the design docs before use. | Follow-up issue. Canary job to be added. |
| pgvector | `packaging/postgres/versions.txt`: `PGVECTOR_VERSION=0.8.1`. Image `pgvector/pgvector:pg18`. | `0.8.2` released Feb 2026 (EDB release notes). `0.9` is reported by one secondary blog only. | Confirmed for 0.8.2. Reported for 0.9. Not found on an official changelog. | Hybrid search and binary vectors are reported for 0.9. Not verified. | (1) Bump to 0.8.2 (needs a new SHA-256 and a bundle check). (2) Check the official `CHANGELOG.md` before any 0.9 work. | Follow-up issue. |
| Npgsql | `10.0.3` | Npgsql 11.0 previews: `11.0.0-preview.1-ci` builds on the `npgsql-vnext` MyGet feed (latest seen 2026-10-26). EF Core provider `11.0.0-preview.6` on NuGet. Reported features: failover, load balancing, multi-host. The EF provider notes PG 18 temporal constraints. | Reported (MyGet, NuGet listings). The Npgsql 11 release notes page is still a stub. | Multi-host and failover matter for the server's Postgres connection when it runs on Linux and Windows. | Track 11.0 previews through Renovate. Run a canary build against the preview when it is on NuGet.org. Do not adopt before GA. | Renovate (tracked, prerelease). Follow-up issue for the canary. |
| Testcontainers.PostgreSql | `4.16.0` | A newer line is listed on the NuGet feed (a `2.1.0-beta` build was seen). The version scheme and changes are not verified. | Not found (only listings). | Test container lifecycle. | Check the official Testcontainers .NET releases before any bump. | Renovate (tracked, prerelease). |
| Valkey (cache and presence on Linux) | Not pinned in the repo. Design uses Valkey on Linux and Garnet on Windows (`docs/design/03`). | Valkey 9.1 GA on 2026-05-19 (Linux Foundation). Adds TLS certificate reload, database-level ACLs, client-side caching support, and Valkey GLIDE 2.4 with C# and PHP. Valkey 9.0.x security patches (CVE fixes in TLS and streams) followed. | Confirmed (Linux Foundation, Valkey release pages). | Client-side caching for GC presence. Security patches must be deployed. | When the cache provider is built, pin Valkey at 9.1 or later, with the latest 9.x patch. Compare StackExchange.Redis with Valkey GLIDE for C# before choosing. | Follow-up issue. |
| StackExchange.Redis | `3.4.5` | 4.x not found. | Not found. | Valkey protocol compatibility. | Re-check at the cache provider's build. | Renovate (tracked, prerelease). |
| Garnet (Windows cache) | Not pinned in the repo. | Not searched in this audit. | Not found. | Windows cache provider. | Searched without result; re-check before the Windows cache provider is built. | Follow-up issue. |

### AI, inference and tests

| Dependency | Pinned at | Next or upcoming | Verification | Features that help Bazaar | Adoption step | Mechanism |
|---|---|---|---|---|---|---|
| Microsoft.ML.OnnxRuntime | `1.31.1` (repo). | 1.31.0 is the newest stable found in search; 1.32 has only nightly builds (Nov 2026). The 1.31.1 pin is not verified against the search result. | Reported for 1.31.0. Not found for 1.31.1 and 1.32. | 1.31: stabilized model-package and EPContext data APIs; improved CPU model loading; quantized MoE inference; device-based execution-provider selection. | Confirm the pin on NuGet. Adopt the 1.31 APIs only where the local CPU tier (doc 31) needs them. Track 1.32 GA. | Renovate (tracked, prerelease). |
| Microsoft.Extensions.AI.Abstractions | `10.11.0` | No 11.x release found. 10.8.0 is the newest seen in coverage. | Not found for 11.x. | Provider-neutral AI surface. | Keep on 10.x until an 11.x release is found on NuGet. | Renovate (tracked, prerelease). |
| xunit.v3.mtp-v2 | `3.2.2` | 4.x is blocked by `Avalonia.Headless.XUnit 12.1.4` (binary incompatibility). | Confirmed by the pin comment in `Directory.Packages.props`. | Current test platform. | Re-check when Avalonia.Headless moves. | Dependabot (`xunit.v3*` ≥ 4 ignored). |
| Microsoft.Testing.Platform | `global.json` `test.runner` | Current runner. | Confirmed by repo config. | Current test runner. | None now. | Canary (through the test run). |

### Mobile and Android

| Dependency | Pinned at | Next or upcoming | Verification | Features that help Bazaar | Adoption step | Mechanism |
|---|---|---|---|---|---|---|
| .NET for Android (`net11.0-android`) | `SupportedOSPlatformVersion` 35. | Android 17 (API 37) preview support in .NET 11 Preview 3, stable by Preview 5. RC2 adds API 37.2 bindings, JDK 25 support and R8 changes. | Reported (InfoQ, DevBlogs, Visual Studio Magazine). | API 37 platform features; CoreCLR as the default release runtime for Android (reported). | (1) Move `TargetSdkVersion` to 37 once the Android client builds on it. (2) Test the emulator matrix on API 37. (3) Re-check the minimum OS level. | Canary (build). Follow-up issue. |
| Xamarin.AndroidX.Window | `1.5.1.3` (fold posture, doc 29) | Not searched in this audit. The package name is the legacy Xamarin binding. | Not found. | Fold and posture APIs. | Check whether a current .NET binding for AndroidX Window targets API 37. Replace the legacy package if one exists. | Renovate does not track it (legacy). Follow-up issue. |
| Gradle or Kotlin | None in the repo. Android is built by `dotnet build`. | Not applicable. | Confirmed (no Gradle or Kotlin files). | Not applicable. | None. | None. |

### Build, CI and container images

| Dependency | Pinned at | Next or upcoming | Verification | Features that help Bazaar | Adoption step | Mechanism |
|---|---|---|---|---|---|---|
| GitHub Actions | Commit SHAs in `.github/workflows`: `actions/checkout` v7.0.1, `actions/setup-dotnet` v6.1.0, `actions/cache` v6.1.0, `actions/upload-artifact` v7.0.2, `actions/download-artifact`, `github/codeql-action` | Dependabot follows releases. Runner image changes are not tracked here. | Confirmed by workflow pins. | Newer action runtimes. | None now. Keep Dependabot's weekly PRs reviewed. | Dependabot (github-actions). |
| CodeQL | `github/codeql-action` pinned SHA | Action updates and query packs. | Confirmed by workflow pin. | Newer queries. | None now. | Dependabot (github-actions). Weekly scan. |
| .NET container images | `packaging/oci/Dockerfile`: `mcr.microsoft.com/dotnet/sdk:11.0` and `aspnet:11.0` (floating tags). | Follow the .NET 11 GA image. | Confirmed by Dockerfile. | Current runtime images. | Pin both images by digest in a release change, and keep the tags as a comment. | Follow-up issue. |
| PostgreSQL container base | `packaging/postgres/Dockerfile`: `postgres:18-bookworm`. | PostgreSQL 19 image, and a current Debian base. | Confirmed by Dockerfile. Debian base currency not verified in this audit. | Current OS base and PG 19. | When the PG 19 bundle lands, move to the newer Debian base if the PGDG packages exist for it. Pin by digest. | Follow-up issue. |
| cloudflared | Not pinned (design only, `Conduit.Cloudflared`). | Not searched in this audit. | Not found. | Connectivity provider. | Re-check when the Conduit provider is built. | Follow-up issue. |
| MailKit, MimeKit | `4.18.2` | Not searched in this audit. | Not found. | Mail client. | Renovate tracks prereleases. | Renovate (tracked, prerelease). |
| Markdig, YamlDotNet, LibGit2Sharp (native libgit2) | `1.5.0`, `18.1.0`, `0.32.0` | Not searched in this audit. | Not found. | Vault parsing and git engine. | Re-check at the next Vault change. | Dependabot (stable). LibGit2Sharp is in the Renovate list. |
| System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData, Microsoft.Extensions.Hosting.Windows/Systemd | Pinned at `11.0.0-rc.2` | Follow the .NET 11 GA packages. | Confirmed by pins. | Platform services. | Move to GA with the SDK pin. | Dependabot and Renovate (`Microsoft.Extensions.*`). |

## Follow-up work items

These are tracked as issues. Each one is a separate adoption step, and none of them is done
in the audit PR.

1. **Pin the .NET 11 GA SDK and drop the RC pins.** Covers `global.json` and the
   `Microsoft.Extensions.*` RC packages. Also review the shared-framework note for those
   libraries.
2. **Android API 37 target.** Move the Android client's target to API 37 after it builds
   and passes the emulator run.
3. **PostgreSQL 18 patch bump, pgvector 0.8.2, and a PG 19 canary.** Needs new SHA-256 values
   and a passing bundle check.
4. **Npgsql 11 canary.** Build against the preview packages once they are on NuGet.org.
5. **Avalonia 12.2 preview canary, and Wayland and SkiaSharp 4 Graphite alignment.**
6. **Vulkan 1.4 baseline and Vortice update check** for the Constellation renderer.
7. **Valkey 9.1 pin and cache client choice** (StackExchange.Redis or Valkey GLIDE) when the
   cache provider is built.
8. **Container images pinned by digest**, with the PG 19 base reviewed.

## Adoption rule

A new version is adopted when all of these are true:

- It is stable (GA or stable release) on its official channel, not only on a mirror.
- Its breaking changes are read and the build passes with `TreatWarningsAsErrors`.
- The use of its new features has a test, per CLAUDE.md ("every behaviour change comes
  with a test").
- The pin, this page, and the canary are updated in the same change.

Preview versions are tracked and tested through the canary and Renovate. They are not
adopted into the production pin until they are stable.
