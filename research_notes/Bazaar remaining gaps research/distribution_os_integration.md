# Distribution, Updates, Signing and OS Integration (as of late 2026)

Scope: packaging, updates, signing, OS integration and server upgrades for the Bazaar server (headless, Windows Service or systemd) and the Avalonia clients (Windows, Android, Linux). Research date: 2026-10-24. Tool budget was about 20 calls, so several points are marked **unverified** or **gap** rather than researched in depth.

Labels used: **verified** = primary source read in this session; **secondary** = aggregator, vendor blog, search snippet, or news report; **unverified** = knowledge only, no source read; **gap** = not answered.

Repo inputs read: `/home/user/Bazaar/docs/deep-dives/Bazaar.Windows.md`, `/home/user/Bazaar/docs/deep-dives/Bazaar.Android.md`, `/home/user/Bazaar/docs/deep-dives/Bazaar.Linux.md`, `/home/user/Bazaar/docs/implementation-plan.md`, `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/cicd_repo_licensing.md`.

---

## Key Question 1: Windows installers, updates, runtime packaging and service install

### Takeaway
For the Windows client, Velopack is the lightest-weight fit found (per-user install, delta updates, GitHub Releases feed, .NET-native), but its page says nothing about Windows Services or code signing, so it cannot cover the server. For the server, a Windows Service needs a separate installer path (MSI via WiX is the usual route, **unverified** here). MSIX was not researched in this session and is a gap. A short spike is needed before choosing.

### Cited Findings
- Velopack must be initialised at the very top of `Main()` via `VelopackApp.Build().Run()`, and `Run()` must be called exactly once. Update and install processes run the main binary with special arguments, and the app must exit quickly — [Velopack integration overview](https://docs.velopack.io/integrating/overview) (**verified**, primary docs). For Avalonia this means the call goes in the entry point, not `App.axaml.cs` — same source (**verified**, the guide's wording; the Avalonia-specific placement is the search snippet's inference, **secondary**).
- Velopack's installs and updates live under `%LocalAppData%`; during an update only the current folder is replaced, so user settings must live outside the install folder — [Velopack integration overview](https://docs.velopack.io/integrating/overview) (**verified**).
- Update feeds can be any host: local directory, HTTP server, S3, Azure Storage, or GitHub Releases through a built-in `GithubSource`; custom sources via `IUpdateSource` — [Velopack integration overview](https://docs.velopack.io/integrating/overview) (**verified**).
- Delta updates are supported; `DownloadUpdatesAsync` attempts deltas and rebuilds the full release, falling back to a full download when deltas are too many or too large — [Velopack integration overview](https://docs.velopack.io/integrating/overview) (**verified**).
- Channel switching is supported; auto-apply will not downgrade or switch channels unless `AllowVersionDowngrade` is set — [Velopack integration overview](https://docs.velopack.io/integrating/overview) (**verified**).
- The Velopack overview page does **not** mention code signing or signature verification (only size and hash checks through `ChecksumFailedException`), does **not** say whether machine-wide installs are supported, and does **not** cover Windows Services — [Velopack integration overview](https://docs.velopack.io/integrating/overview) (**verified** as an absence on the page).
- Search results surfaced Velopack NuGet versions in the 0.0.5xx–0.0.1053 range, which suggests a very frequent release cadence. The exact current version was not read — [Velopack NuGet](https://nuget.org/packages/Velopack) (**secondary**, version number not confirmed).
- Microsoft Learn's default-terminal page returned HTTP 404 when fetched (`https://learn.microsoft.com/en-us/windows/apps/develop/settings/default-terminal`); no Windows Service or MSIX pages were read in this session.

### Inferences
- **Client (Windows):** Velopack per-user install is a plausible default for `Bazaar.Client.Windows`. It needs a code-signing step before publishing (the page doesn't cover it) and a test of whether the Avalonia head's startup works with the required early `Run()`.
- **Server (Windows):** A Windows Service is a machine-level component, and Velopack's documented model is a per-user, folder-replacement update. Using Velopack to update a running service is **unverified** and probably needs a stop/replace/start sequence. Treat the server installer as a separate track (WiX/MSI or MSIX, **unverified**).
- **Self-contained vs framework-dependent:** not researched. Self-contained output is a common choice for desktop distribution, but AOT and trimming compatibility with Avalonia and Velopack was not verified. Keep AOT out of the first release unless a spike shows it works for the Avalonia head.
- **Winget:** publication requirements not researched (**gap**). Winget manifests are usually generated from a released installer URL and hash; unverified here.

### Gaps
- MSIX vs MSI (WiX) vs Velopack trade-offs for both client and service: no primary Microsoft Learn source was read (MSIX/packaged-app pages, Windows Service pages, WiX v5 docs).
- Whether Velopack can host or update a Windows Service, and whether it supports machine-wide install: not stated on the overview page.
- Self-contained, AOT and trimming support for Avalonia 12 and for Velopack: not verified.
- Winget submission rules: not researched.
- Avalonia-specific Velopack packaging (the `vpk pack` invocation for an Avalonia app): the search snippet said the WPF pattern "should apply" to Avalonia, which is **unverified**.

---

## Key Question 2: Windows default-app registration (file types, default terminal) and Windows 11 limits

### Takeaway
A desktop app cannot silently make itself the default handler for text/code file types or the default terminal on Windows 11. Microsoft restricts writes to the UserChoice registry key, and Windows 11 protects these choices with a driver-level guard. The realistic route is to register the app's capabilities and send the user to the Settings page to choose it. The "Default terminal application" setting is the mechanism for the terminal. The design doc's "replacing Notepad" goal therefore depends on the user choosing it. This is a conflict to report, not to argue against.

### Cited Findings
- Microsoft's guidance is that applications must not write to the UserChoice key, because the user must stay in control; Windows validates the ProgID with a hash, and hand-edited values without a valid hash are ignored — [dedoimedo: Windows 11 file associations](https://www.dedoimedo.com/computers/windows-11-file-assoc.html) (**secondary**, aggregator; the Microsoft policy statement itself was not read).
- The UserChoice Protection Driver (UCPD) blocks modification of selected UserChoice keys; reporting in 2024 said http(s) and .pdf are covered. Mozilla's documentation says all ancestors of UserChoice are locked and that setting defaults through UserChoice is "no longer viable" on modern Windows 11 — [Mezha: new Windows driver blocks changing default browser](https://mezha.media/en/2024/04/08/new-windows-driver-blocks-changing-the-default-browser) (**secondary**, news report); [Mozilla Set Default docs](https://firefox-source-docs.mozilla.org/widget/windows/shell/set-default.html) (**secondary**, search snippet, not read in full).
- Mozilla's approach uses a Windows API that can set defaults without a system confirmation prompt, and says it must only be used with user consent — [Mozilla Set Default docs](https://firefox-source-docs.mozilla.org/widget/windows/shell/set-default.html) (**secondary**, search snippet).
- Reverse-engineered hashing tools (SetUserFTA) exist; treat as a last resort because they defeat the consent mechanism — [dedoimedo](https://www.dedoimedo.com/computers/windows-11-file-assoc.html) (**secondary**).
- Default terminal setting: in Windows Terminal, Startup > "Default terminal application" offers "Let Windows decide", "Windows Console Host" and "Windows Terminal". On Windows 11 it appears under Settings > Privacy & Security > For developers (or System > For developers, per another guide) — [Microsoft Learn: Windows Terminal get started](https://learn.microsoft.com/en-us/windows/terminal/get-started) and [winhelponline: set default terminal](https://www.winhelponline.com/blog/set-default-terminal-windows-11/) (**secondary**, search snippets; the Microsoft Startup page was not read in full).
- The setting is stored under `HKCU\Console\%%Startup` as `DelegationConsole` and `DelegationTerminal` REG_SZ GUIDs (all-zero = automatic), and can be set by Group Policy — [winhelponline](https://www.winhelponline.com/blog/set-default-terminal-windows-11/) (**secondary**). Users can select it, but a third-party app should not write these values directly; this is an inference from the UserChoice findings, not a sourced rule.
- The all-zero GUID means "Let Windows decide". Per a Microsoft PR, conhost only hands connections off when the installed terminal signals it accepts them, via a marker interface — [microsoft/terminal PR #13160](https://github.com/microsoft/terminal/pull/13160) (**secondary**, search snippet, not read).
- Availability: the default-terminal feature is in all Windows 11 versions and in Windows 10 22H2 after KB5026435 (May 2023 update) — [Microsoft Learn: Windows Terminal get started](https://learn.microsoft.com/en-us/windows/terminal/get-started) (**secondary**, search snippet).
- **Discrepancy:** Microsoft's Startup page lists Windows Console Host as the default value, while winhelponline and the PR describe "Let Windows decide" as the automatic default. Current behaviour is probably "Let Windows decide", but no primary source confirmed it (**unverified** which is current).

### Inferences
- **Terminal handoff:** For Bazaar's terminal to be the default, the Windows client must implement the console handoff to the COM server that Windows Terminal uses (the handoff interface and its CLSID registration). The exact interface, CLSID registration and packaging requirements were **not** read in this session (**gap**). A spike against the Windows Terminal handoff code and docs is required before claiming this works.
- **File types:** Practical approach is to declare file-type and "Open with" capability in the packaging manifest (MSIX or installer registration; **unverified** mechanics) and offer an in-app "Make Bazaar the default" button that opens `ms-settings:defaultapps` (**unverified** URI, knowledge only). Do not write UserChoice.
- **Replacing Notepad:** The Bazaar.Windows.md "replacing Notepad" goal can only be met if the owner chooses it. This is a limit of the platform, not a design choice to argue against. Report it to the owner (see Conflicts below).
- **Detached windows as default handler:** Windows can launch an app for a file type via its file association with arguments (e.g. `"%1"`). Using the same handler for both the terminal and text files is not a conflict in the platform, but the detached-window launch path needs a spike.

### Gaps
- The exact Microsoft Learn text on Windows 11 default-app requirements (the "For developers" setting, handoff COM interface, packaging needed for the terminal option): not read.
- Whether MSIX packaging is required for the terminal option, or whether an unpackaged Win32 app can be selected: **gap**.
- Official Microsoft statement on UserChoice policy: not read; only secondary sources.
- Whether a Windows 11 Settings page deep link (`ms-settings:defaultapps`) is current: **unverified**.

---

## Key Question 3: Detachable windows, process model and startup budgets

### Takeaway
Not researched in depth (**gap**). Avalonia's own documentation was not read in this session. The sensible default is one client process with multiple top-level windows, plus a small separate launcher for file-association and terminal launches that forwards the request to the running instance. This is an inference that needs a spike.

### Cited Findings
- None verified in this session. The design doc requires detachable lightweight windows that register as OS default handlers and run alongside the server (`/home/user/Bazaar/docs/deep-dives/Bazaar.Windows.md`, Section 1) — **verified** as a requirement in the repo.
- Startup-time figure from the Avalonia Android rebuild (1,960 ms to 460 ms with NativeAOT) is a vendor claim already flagged in `/home/user/Bazaar/docs/deep-dives/Bazaar.Windows.md` Section 2 as having no stated method. Not re-verified here.

### Inferences
- A launcher process is needed because Windows starts a new process for each file-association or terminal handoff. Forwarding to a single-instance IPC endpoint (named pipe) avoids multiple full shells. This is **unverified** and needs a spike.
- Startup budget targets were not set in any source read; propose a budget and measure it in the spike rather than quote one.

### Gaps
- Avalonia single-instance and multi-window guidance: not read.
- Named-pipe IPC and single-instance patterns for .NET desktop apps: not read.
- Cold-start measurements for Avalonia on Windows with and without NativeAOT: not found.

---

## Key Question 4: Code signing in 2026 for an open-source project

### Takeaway
SignPath Foundation's free programme for OSS is the best-supported option found, but its requirements (OSI licence without commercial dual-licensing for all components, released and maintained project, automated build) must be checked against Bazaar's AGPL/Apache split and the XPF question. Azure Artifact Signing pricing could not be read (prices did not render), and its eligibility for an individual is unknown. EV/OV certificate prices were not researched (**gap**).

### Cited Findings
- SignPath Foundation: "For OSS projects, our services are free of charge." The Foundation is the named publisher and "will vouch for that with our name" after verifying that the binary was built from the open-source repository. The page does not say whether the certificate is OV or EV and does not mention SmartScreen by name; it says the goal is to remove installation warnings — [SignPath OSS programme](https://about.signpath.io/product/open-source) and [signpath.org](https://signpath.org/) (**verified** primary pages, read via fetch; the vendor page's wording as read).
- SignPath Foundation's conditions (from search snippets of the SignPath terms and third-party copies, **secondary**): all components under an OSI-approved open-source licence with no commercial dual-licensing; no proprietary components (system libraries allowed); actively maintained and already released in the form to be signed; no malware; fully automated build from the checked-in source; a code-signing policy published on the project home page; the Foundation may pause or revoke the subscription for Code of Conduct violations — [SignPath terms](https://signpath.org/terms) (**secondary**, search snippet, not read in full; third-party copies not authoritative).
- Azure Artifact Signing pricing page: tiers are Basic (5,000 signatures per month, 1 of each Certificate Profile type) and Premium (100,000 per month, 10 of each); prices are calculated in US dollars; billing starts when an Artifact Signing account is created and is not pro-rated — [Azure Artifact Signing pricing](https://azure.microsoft.com/en-us/pricing/details/artifact-signing/) (**verified** for quotas and billing terms; **the monthly base prices and overage prices did not render**, so no USD figure is confirmed).
- A 2026 third-party blog says the service is "priced from $9.99/month", and that Japanese entities and residents cannot use it as of July 2026 — [codenote](https://codenote.net/en/posts/windows-code-signing-ci-cost-per-signing-volume-2026/) (**secondary**, unverified; contradicts the quota-based model shown on the Azure page, so do not rely on it).
- The Azure page does not list eligible countries or say whether individuals or only organisations can sign up (**verified** absence).

### Inferences
- **Licence fit:** SignPath requires every component to be OSI-licensed without commercial dual-licensing. Bazaar's Avalonia dependency is MIT for the free core (per `cicd_repo_licensing.md`, **secondary**), but any build that includes Avalonia XPF (commercial) or ParadeDB `pg_search` under its commercial licence would not qualify. Keep signed artifacts to the public repo's OSI-licensed components, and keep private company code out of the signed build.
- **CI fit:** SignPath's model (build on the public repo, submit the artifact for signing) matches the split in `cicd_repo_licensing.md`, where public CI produces unsigned artifacts and the private repo signs. Note the conflict: `cicd_repo_licensing.md` puts the Windows signing certificate in the private repo's GitHub Environment as a secret; SignPath would hold the certificate on its side. The owner should choose one.
- **Timing:** SignPath requires the project to be already released in the form being signed, so the first public release may need an unsigned or Azure-signed beta before SignPath can apply. This is inferred from the conditions above and is **secondary** only.
- **SmartScreen:** Reputation behaviour (how new publishers and new binaries are treated) is not sourced in this session. Whatever certificate is used, expect SmartScreen warnings on early builds until reputation builds. **unverified**.

### Gaps
- EV and OV certificate prices and vendor list (e.g. DigiCert, Sectigo, SSL.com): not researched.
- Azure Artifact Signing actual USD monthly prices and per-signature overage: not rendered; needs the Azure pricing calculator or a manual check.
- Azure Artifact Signing eligibility for individuals and for specific countries: not confirmed by a primary source (the Azure page is silent, and the codenote claim is unverified).
- SmartScreen reputation mechanics for Authenticode-signed binaries and for MSIX: not sourced.
- SignPath's current application page and the actual signing policy requirements: not read directly.

---

## Key Question 5: Android distribution, signing, Play rules, foreground services and packaging

### Takeaway
Sideloaded APK is the simplest first distribution path. Google Play requires a closed test with at least 12 testers opted in for 14 continuous days before production access for personal accounts created after 13 November 2023. Android 14+ requires a declared foreground service type for the socket keep-alive, and `dataSync` is the closest fit. Android 16 forces edge-to-edge and has no foreground-service change confirmed in the page read. F-Droid and the AAB/APK packaging of Avalonia were not verified.

### Cited Findings
- Personal developer accounts created after 13 November 2023 must run a closed test with at least 12 testers, opted in continuously for at least 14 days, before applying for production access — [Google Play Console Help: personal account requirements](https://support.google.com/googleplay/android-developer/answer/14151465) (**verified**, primary). The page does not state the registration fee or verification requirements; it links to a separate device-verification article that was not read.
- Android 14 (API 34) and later require declaring a foreground service type in the manifest, with the matching `FOREGROUND_SERVICE_<TYPE>` permission in addition to `FOREGROUND_SERVICE`. Play Console app-content declaration is required for apps targeting Android 14 or higher — [Android foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types) (**verified**, primary, page last updated 2026-10-23 UTC).
- For a socket keep-alive, the candidate types are `dataSync` ("Transfer data between a device and the cloud over a network"), `remoteMessaging` (cross-device message handoff, a poor fit), and `specialUse` (requires a justification property reviewed in Play Console) — [Android foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types) (**verified**). `dataSync` cannot be started from `BOOT_COMPLETED` on Android 15+, per the same page's summary (**verified** as summarised by the fetch; re-read the page before relying on it).
- The Android 16 behaviour-change page confirms that apps targeting Android 16 cannot opt out of edge-to-edge (`windowOptOutEdgeToEdgeEnforcement` is disabled), and that predictive back is enabled by default — [Android 16 behaviour changes](https://developer.android.com/about/versions/16/behavior-changes-16) (**verified**, primary, page last updated 2026-10-23 UTC).
- The same Android 16 page, as read, did **not** describe foreground-service timeouts, background-start restrictions or `POST_NOTIFICATIONS` changes (**verified** absence on that page). The `dataSync` 6-hour-style timeout premise was not confirmed by any source (**unverified**). Check the Android 16 "all apps" behaviour pages and the foreground service pages separately.

### Inferences
- **Distribution order:** Start with signed APK sideloading for the owner's own devices, which avoids Play's closed-test requirement. Add Play only after the 12-tester, 14-day closed test is planned.
- **Foreground service:** Use `dataSync` for the WebSocket keep-alive, with a permission-and-manifest entry in the Android head. `specialUse` would add a Play review step. Notification permission and channel behaviour were not sourced.
- **Edge-to-edge:** Avalonia's Android layout must handle edge-to-edge insets on Android 16 targets; this is a compatibility item to test on the foldable.

### Gaps
- Google Play one-time registration fee and currency: not stated on the page read; the commonly cited figure is USD 25 but this is **unverified** knowledge.
- Google Play target API level requirement for new apps and updates for 2026: not read.
- F-Droid constraints for .NET apps (reproducible builds, anti-features, Google-free dependencies, .NET-for-Android build support): **not researched**. Treat F-Droid as unverified.
- Avalonia Android packaging (AAB vs APK, keystore setup, .NET for Android build properties): not read. Knowledge: .NET for Android produces both; Play requires AAB. **unverified**.
- Android keystore management and Play App Signing enrolment: not read.
- `POST_NOTIFICATIONS` runtime permission on Android 13+ and notification channel rules: not read.

---

## Key Question 6: Linux packaging and the systemd server unit

### Takeaway
Not researched in this session (**gap**). Both points are knowledge-only: AppImage or Flatpak for the Linux client is a common choice, and a systemd unit is the usual way to run a .NET headless server. Sources were not fetched; the Flatpak documentation URL returned HTTP 502 when attempted, though no retry was made.

### Cited Findings
- None verified in this session.
- The Linux client doc notes that ConPTY does not apply and a native PTY backend is needed (`/home/user/Bazaar/docs/deep-dives/Bazaar.Linux.md`, Open Questions) — **verified** repo fact.
- The implementation plan puts "installers and updates, signing" in Wave 5 (`/home/user/Bazaar/docs/implementation-plan.md`, Wave 5) — **verified** repo fact.

### Inferences
- **Client:** Flatpak is probably the best default for desktop distribution of a self-contained .NET GUI app, because it sandboxes the app and has a central update channel. AppImage is simpler but has no update channel of its own without extra tooling. deb/rpm add distro-specific maintenance. All **unverified** here.
- **Server:** A systemd unit (`Type=notify` through `sd_notify` or `Type=simple`, `Restart=on-failure`, a dedicated user, and an `EnvironmentFile`) is the expected pattern for a .NET Worker Service on Linux. **unverified** in this session.

### Gaps
- Flatpak, AppImage, deb/rpm and systemd specifics for .NET 10 apps: no sources read.
- Whether Velopack covers Linux (its overview says Windows, macOS and Linux are supported per the search summary, but this was not confirmed from the page read): **unverified**.

---

## Key Question 7: Server upgrades, database migrations and rollback

### Takeaway
Not researched from external sources (**gap**). The implementation plan already fixes the database approach (embedded, versioned SQL migrations with a small runner, PostgreSQL 18). The following is an inference to be checked in a spike, not a sourced practice.

### Cited Findings
- The database choice and embedded versioned SQL migration runner are fixed in `/home/user/Bazaar/docs/implementation-plan.md` (Fixed choices table) — **verified** repo fact.
- The GC log is append-only and "never purged" (`/home/user/Bazaar/CLAUDE.md`, Decisions in force) — **verified** repo fact. This constrains rollback: a schema change that rewrites or drops the log cannot be rolled back by simply restarting an older binary.

### Inferences
- **Upgrade sequence (proposed):** stop the service, take a verified Postgres backup, run migrations in a dedicated step (not at process start), start the new binary, and check health before accepting clients.
- **Migrations:** prefer expand-then-contract changes so an older binary can still run against the newer schema for one version. Avoid destructive migrations in a release that might be rolled back.
- **Rollback:** restore the previous binary and, if the migration was not backward compatible, restore the pre-upgrade database backup. Down-migrations should not be relied on. Note that restoring a backup discards GC events written after the backup, which conflicts with "append-only, never purged" unless the restore is done as a replay; this is a design question for the owner.
- **Velopack and services:** the client update path does not cover the server (see Key Question 1). The server's update path (installer or systemd package) must have its own rollback step.

### Gaps
- Postgres major-version upgrade and migration tooling best practice (e.g. transactional DDL limits, `CREATE INDEX CONCURRENTLY`): not researched.
- How other self-hosted .NET services handle rollback: not researched.

---

## Conflicts and items for the owner

1. **Replacing Notepad (DECIDED in `/home/user/Bazaar/docs/deep-dives/Bazaar.Windows.md`, Section 1).** Windows 11 does not let an app set itself as the default for text/code file types or the default terminal without the user choosing it in Settings. The "replace Notepad" outcome depends on the user; the app can only register and deep-link. Report to the owner, do not argue with the DECIDED item.
2. **Default terminal (Bazaar.Windows.md, Section 4).** The terminal must implement the Windows Terminal handoff to be selectable as the default. This is not yet spiked, and the terminal library is still OPEN in the same doc.
3. **Signing secrets (cicd_repo_licensing.md, Key Question 3 Inferences).** That note puts the Windows signing certificate in the private repo as a secret. SignPath would hold the key on its side. Decide which model to use.
4. **SignPath licence fit.** SignPath needs OSI-only components. Avalonia XPF and any commercial `pg_search` licence would block signing the affected build (`/home/user/Bazaar/docs/deep-dives/Bazaar.Windows.md` Open Questions 2 and 4; cicd_repo_licensing.md Key Question 4).
5. **Server Windows update path.** Velopack's documented model does not cover a Windows Service. Plan a separate server installer track.

## Recommended next steps (for the owner)

1. Spike Velopack on the Avalonia Windows head (confirm early `Run()` placement, `%LocalAppData%` install, GitHub Releases feed, signing step).
2. Spike a Windows Service installer (WiX/MSI or MSIX) on the NUC and check whether the Worker Service can be installed and updated without breaking the running server.
3. Spike the default-terminal handoff against Windows Terminal's docs and sources; read the Microsoft Learn Windows 11 default-app pages before implementing.
4. Apply for SignPath Foundation (after reviewing the terms and the public-repo licence fit); get a current Azure Artifact Signing quote from the Azure calculator as the fallback.
5. Decide the Android path: signed APK sideload for the owner first; plan the Play closed test (12 testers, 14 days) only if Play is needed.
6. Read F-Droid inclusion rules and Avalonia Android AAB packaging before any public Android release.

## Could not verify

- Azure Artifact Signing USD prices and individual eligibility (page prices did not render).
- EV/OV certificate prices and SmartScreen reputation behaviour.
- MSIX, WiX, winget, self-contained/AOT/trimming for Avalonia and Velopack, and Velopack's support for Windows Services.
- Microsoft's own default-app and default-terminal pages (the default-terminal URL returned 404; the Startup page was not read in full).
- Android 16 foreground-service timeout details; Google Play registration fee (commonly cited as USD 25, unverified); F-Droid rules; Avalonia Android AAB packaging.
- Linux packaging (Flatpak, AppImage, deb/rpm) and the systemd unit pattern; server upgrade and rollback practice.
