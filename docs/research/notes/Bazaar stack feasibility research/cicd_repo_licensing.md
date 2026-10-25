# Bazaar CI/CD, Repository Structure and Open-Source Licensing (as of late 2026)

Scope: CI engine, .NET monorepo layout, Android and Avalonia testing in CI, open-source licence strategy, secret handling across the two-repo split, and release engineering. Sources are dated as of the search session (2026-10-24). Not legal advice. Items marked **[unverified]** were not confirmed against a primary source in this session.

Input docs read: `Bazaar.Server.md` and `Bazaar.Windows.md` (scratchpad `files-zip-new/`).

---

## Key Question 1: CI engine on a Windows-only host, and fit for agent-opened PRs

### Takeaway
Use **GitHub Actions** as the engine, with a split: public `bazaar` repo builds and tests on **GitHub-hosted runners only** (never on the NUC), and the private repo plus the auto-ship/deploy step uses a **self-hosted Windows runner on the NUC** that only ever runs trusted refs. Woodpecker is a poor fit for a Windows-only host, and a custom .NET runner is a cost with no upside at this stage.

### Cited Findings
- GitHub's changelog (Dec 2025) announced a $0.002/min platform charge for self-hosted runner usage from 1 March 2026, and says public repositories stay free. GitHub has since **postponed** that billing change "to take time to re-evaluate our approach," with no new date — [GitHub changelog](https://github.blog/changelog/2025-12-16-coming-soon-simpler-pricing-and-a-better-experience-for-github-actions/). Some third-party pages (Aug 2026 comparison, Northflank) still describe the fee as live, and I could not confirm which is current — [Northflank](https://northflank.com/blog/github-pricing-change-self-hosted-alternatives-github-actions). Confirm on docs.github.com billing before budgeting.
- Hosted-runner prices were cut up to 39% from 1 Jan 2026; free minute quotas unchanged — [GitHub changelog](https://github.blog/changelog/2025-12-16-coming-soon-simpler-pricing-and-a-better-experience-for-github-actions/).
- Private-repo free allowance is listed as 2,000 min/month by third-party sources only — [thepricer comparison](https://www.thepricer.org/how-much-does-github-actions-cost/). **[unverified against GitHub docs]**. Windows runner minutes are believed to be billed at a multiplier over Linux **[unverified]**.
- GitHub's self-hosted runner doc states self-hosted runners "are free to use with GitHub Actions, but you are responsible for the cost of maintaining your runner machines" — [GitHub docs](https://docs.github.com/en/actions/hosting-your-own-runners/managing-self-hosted-runners/about-self-hosted-runners). The security section of that doc was not retrievable in this session.
- Self-hosted runners on **public** repositories are a documented risk: fork PRs can run attacker-controlled code on the runner. Community and vendor sources describe this and recommend hosted runners, disabling "allow public repositories" on runner groups, ephemeral/isolated runners, and not sharing runners with sensitive work — [GitHub Community thread](https://github.community/t/self-hosted-runner-security-with-public-repositories/17860); [Legit Security](https://www.legitsecurity.com/blog/securing-your-ci/cd-pipeline-exploring-the-dangers-of-self-hosted-agents); [StepSecurity](https://stepsecurity.io/blog/defend-your-github-actions-ci-cd-environment-in-public-repositories). A job-level `if:` guard excluding forks was described as fragile on its own by commenters in the same thread.
- Woodpecker official platform matrix: **server** on Windows is amd64 binary only; **agent** on Windows is amd64 binary only, and only the **Local** backend is listed for Windows. The Docker backend on Windows "works through WSL2 with Docker Desktop, and with native Windows containers"; Kubernetes is Linux only — [Woodpecker supported platforms](https://woodpecker-ci.org/docs/administration/installation/supported-platforms). Version 3.19 added custom shell paths for the Local backend — [Linuxiac](https://linuxiac.com/?p=220838).
- Avalonia's headless test stack (`Avalonia.Headless`) renders to an in-memory surface with no display — as stated in the design doc (`Bazaar.Windows.md`, Section 2). Not independently re-verified in this session.

### Inferences
- **Woodpecker on a Windows-only NUC means the Local backend**: jobs run directly on the host with no container isolation. That is the wrong property for an engine that will execute agent-written code from PRs. Woodpecker's Docker path on Windows needs WSL2 or Windows containers, which adds a second moving part. Verdict: poor fit for this constraint.
- **Gitea/Forgejo Actions** reuses the GitHub Actions workflow syntax, but its runner is the same class of host-executed runner unless paired with Docker/Linux. It is only worth considering if the project later moves off GitHub. No Windows-specific maturity claim was verified.
- **Jenkins/TeamCity** would work on Windows and are heavier to operate. They add operational load for a solo-plus-agents team and do not give agents a simple PR-status loop without extra plugins. Not recommended.
- **Custom .NET runner** would reimplement required status checks, PR comments, artifact storage, and log streaming. Not justified; the build logic itself should instead live in a plain script (see Section 2) so any engine can call it.
- **Agent-friendly CI** requirements that point to GitHub Actions: required status checks gated by rulesets, merge queue, job summaries, test results in a machine-readable form (TRX/JUnit), and PR comments from bots. A single entry-point script (`build.ps1` plus `build.sh`) called by workflows keeps the logic engine-independent.
- **Fork-PR threat model for agents**: agents open PRs from forks inside the org. Those are fork PRs, so the public repo should (a) require approval for workflow runs from first-time/fork contributors, (b) never give secrets to `pull_request` workflows from forks (GitHub's default), and (c) run only on hosted runners.
- **Recommended split**:
  - `bazaar` (public): hosted `windows-latest` and `ubuntu-latest` for build, unit tests, Avalonia headless tests, Android build (see Section 3), Godot/Luau static checks. No self-hosted runners.
  - private company repo: self-hosted Windows runner on the NUC, ephemeral, dedicated, for deploy/auto-ship and for integration tests that need real credentials. Runs only on `main` and protected release branches, via a GitHub Environment with required reviewers or branch restriction.
  - Auto-ship on pass: CI on the public repo produces signed-or-unsigned artifacts; the private repo's deploy job pulls the released artifact and deploys. Keeps untrusted PR code away from the deploy host.
- **Windows self-hosted runner hardening** (for the private repo): run with `--ephemeral` (verify flag in current runner docs **[unverified]**), in a dedicated low-privilege account, no interactive admin, no secrets in the runner's environment outside GitHub Environments.

### Gaps
- Current GitHub billing status for self-hosted runners (postponed vs live) — not confirmed from a GitHub primary page after the changelog.
- Whether GitHub's free plan for private repos is 2,000 min/month — not confirmed from GitHub docs.
- Whether Woodpecker's Local backend on Windows is production-grade (no evidence beyond a third-party blog).
- Exact current Windows-runner minute multiplier **[unverified]**.

---

## Key Question 2: .NET monorepo layout, naming, and build infrastructure

### Takeaway
One solution per repo (`bazaar.sln` in public, the private repo keeps only private projects), with `Directory.Build.props`, `Directory.Packages.props` (Central Package Management), `.editorconfig`, and `global.json` at the root. Rename the colliding projects so the platform suffix only appears on client heads, and the server stays one platform-neutral library with a thin Windows Service host.

### Cited Findings
- Central Package Management: create `Directory.Packages.props` at root with `ManagePackageVersionsCentrally=true`, define `<PackageVersion>` items, and remove `Version` from `<PackageReference>`. Global package references go in the same file. Nested `Directory.Packages.props` files replace the root one unless explicitly imported, and `VersionOverride` is available per project — [Microsoft Learn: Central Package Management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management) (page updated March 2026).
- The same page warns that multiple package sources require source mapping (NU1507) when CPM is enabled — same source.
- Design docs (`Bazaar.Server.md`, `Bazaar.Windows.md`) already fix these shapes: `/shared` for Protobuf contracts; a new `Bazaar.UI.Core` Avalonia library with thin head projects; Protobuf schemas generating C# bindings; the top-level folders `/server`, `/client`, `/sdk`, `/plugins`, `/docs`.

### Proposed layout (public `bazaar` repo)

```
bazaar/
  bazaar.sln
  global.json                  # pin .NET SDK version
  Directory.Build.props        # shared properties, analyzers, nullable, warnings policy
  Directory.Build.targets      # optional shared targets
  Directory.Packages.props     # Central Package Management
  .editorconfig                # style + analyzer severities
  NuGet.config                 # single source (nuget.org) to avoid NU1507
  proto/                       # Protobuf schemas (source of truth for wire contracts)
  src/
    Bazaar.Contracts/          # generated C# from proto + hand-written contract helpers
    Bazaar.Server/             # platform-neutral server library (domain, agents, GC, DB)
    Bazaar.Server.WindowsService/  # thin host: Windows Service entry point, nothing else
    Bazaar.Sdk/                # public plugin/agent SDK
    Bazaar.Client.Core/        # Avalonia shared UI (views, view models, theme) [doc name: Bazaar.UI.Core]
    Bazaar.Client.Windows/     # head: ConPTY terminal, Windows notifications, installer hooks
    Bazaar.Client.Android/     # head: Android activity, WindowManager bindings, NPU selection
    Bazaar.Client.Linux/       # head: Linux PTY, packaging
  tests/
    Bazaar.Contracts.Tests/
    Bazaar.Server.Tests/
    Bazaar.Client.Core.Tests/  # Avalonia.Headless
  plugins/
  docs/
  build/                       # build.ps1 / build.sh entry points, CI helpers
  .github/workflows/
  LICENSE  NOTICE  CONTRIBUTING.md  SECURITY.md
```

### Naming decision (resolves the doc collision)
- `Bazaar.Server.Windows` (doc) vs `Bazaar.Windows` (client head) collide. Proposal: **the platform suffix only appears on client heads**; the server is one library, and the Windows-specific code is `Bazaar.Server.WindowsService`.
- Client heads become `Bazaar.Client.Windows`, `Bazaar.Client.Android`, `Bazaar.Client.Linux`, and the shared library `Bazaar.Client.Core` (or keep `Bazaar.UI.Core` if the doc name is preferred; pick one before the first commit).
- Private repo naming: `bazaar-company` (or similar) with `company/` projects that reference `bazaar` as a git submodule or a NuGet package, never the reverse.

### Build and quality infrastructure (inferences from standard .NET practice, not from sources in this session)
- `Directory.Build.props`: `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>` enabled only when `ContinuousIntegrationBuild=true` (set by CI), `<AnalysisLevel>` and `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`, `<Deterministic>true</Deterministic>`, shared `<LangVersion>`/`<TargetFramework>` defaults, and the Roslyn NetAnalyzers.
- Analyzers: built-in .NET analyzers plus a community set (e.g. Meziantou.Analyzer) **[unverified version/fit]**. Severity lives in `.editorconfig`, so agents and humans see the same rules.
- Source generators: System.Text.Json source generation for wire DTOs; Protobuf code generation from `proto/`; Avalonia compiled bindings for XAML **[unverified in this session]**.
- A single `build.ps1` and `build.sh` that run `dotnet restore`, `dotnet build -warnaserror`, `dotnet test --logger trx`, and format checks (`dotnet format --verify-no-changes`). CI workflows only call these scripts. Agents run the same script locally.

### Gaps
- Whether a NUKE-style C# build tool is worth the added dependency — not researched.
- Concrete Avalonia compiled-binding and project template details for Avalonia 12 — not verified.

---

## Key Question 3: Android build in CI, Avalonia headless tests, Godot/Luau checks

### Takeaway
Avalonia headless tests and the Android build can both run on **hosted** GitHub runners (no self-hosted hardware, no secrets). Emulator/device testing belongs in a nightly or manual job, not in every PR. Signing keys exist only in the private repo.

### Cited Findings
- Avalonia's free core is MIT-licensed — [Avalonia license page](https://avaloniaui.net/legal/1C) (the page-to-licence mapping was read from a search listing, **[unverified which page is the MIT text]**).
- The Avalonia Android backend was rebuilt in Avalonia 12, per the design doc (`Bazaar.Windows.md`, Section 2). Not independently verified in this session; no Avalonia 12 release date or licence text was retrieved.
- Android SDK and Java availability on hosted runners: **[unverified in this session]**. My expectation is that GitHub's hosted Ubuntu and Windows images include the Android SDK, but this needs a check against the runner-images README before relying on it.
- Azure Artifact Signing (formerly Trusted Signing) is generally available in the USA, Canada and Europe per a January 2026 report — [Microsoft Trusted Signing product page](https://azure.microsoft.com/products/trusted-signing) (this is the vendor page, not a verified pricing source).

### Inferences
- **Per-PR**: `dotnet build` of the Android head plus `Avalonia.Headless` tests of `Bazaar.Client.Core` on hosted Windows. This catches compile errors and UI logic regressions without any device.
- **Nightly / on release branch**: Android emulator run (hosted Linux with KVM, or a device farm such as Firebase Test Lab **[unverified availability/pricing]**) for install-and-launch smoke tests and one or two screens.
- **Godot and Luau**: Godot can run headless for scripted checks (e.g. a project load or a `--check-only` style step **[unverified flag names for the target Godot version]**). Luau static checks via `luau-analyze` and a linter such as Selene **[unverified]**. These run as hosted jobs on Linux; no Roblox credentials are needed for static checks.
- **Signing**: Android keystore and Windows signing certificate are secrets in the private repo's GitHub Environment (protected to `main`). The public repo's CI produces **unsigned** artifacts that the private release job signs.

### Gaps
- Hosted-runner Android SDK/NDK preinstallation and exact versions — not verified.
- Device-farm options and costs — not researched.
- Whether Avalonia.Headless tests can run unattended on a hosted Windows runner with no interactive desktop session — expected but not confirmed in this session.

---

## Key Question 4: Open-source licence choice

### Takeaway
Two defensible options: **AGPLv3** for the platform (blocks closed hosted forks, matches the AGPL pg_search dependency, and is compatible with MIT dependencies such as Avalonia core), or **Apache-2.0** (maximum adoption, patent grant, no network copyleft). Avoid shipping XPF-dependent code in the public repo until its licence terms are checked. Use a **DCO sign-off** rather than a CLA unless relicensing flexibility is wanted.

### Cited Findings
- ParadeDB's `pg_search` is licensed under AGPL-3.0 and also offered under a commercial licence (sales contact listed) — [PGXN pg_search](https://pgxn.org/dist/pg_search); ParadeDB has used AGPL "from day one" per its own statement — [ParadeDB llms-full](https://www.paradedb.com/llms-full.txt).
- Avalonia's free core is MIT — [Avalonia license page](https://avaloniaui.net/legal/1C).
- Avalonia XPF is a separate commercial product. The official XPF page lists **Business €19,500** — [Avalonia XPF pricing](https://avaloniaui.net/xpf/pricing). Enterprise is listed at **€55,000** on the official page but **€89,000** on a third-party aggregator ([fitgap](https://us.fitgap.com/products/avalonia)); the sources conflict and the official figure should be confirmed with Avalonia sales. The Internal tier is described as internal use only, not for applications distributed outside the organisation (official page snippet).
- XPF trial terms prohibit production or commercial use during the trial — [Avalonia legal Schedule 2C](https://avaloniaui.net/legal/2C). The Accelerate Business schedule grants a perpetual licence with 12 months of support for an annual purchase — [Avalonia legal Schedule 1B](https://avaloniaui.net/legal/1B) (mapping to the URL **[unverified]**).
- The design doc notes that the most polished WebView2/Monaco integration sits under XPF; a free community package exists but is less mature (`Bazaar.Windows.md`, Section 2).
- Redis's 2024/2025 licence history (SSPL/RSAL, back to AGPLv3) and Valkey's BSD-3 licence are cited in `Bazaar.Server.md` — not re-verified in this session.

### Option comparison (knowledge-based, not sourced in this session)
| Licence | Closed hosted fork | Patent grant | Adoption | Fit with pg_search (AGPL) | Notes |
|---|---|---|---|---|---|
| MIT | Allowed | No explicit | Highest | Fine, pg_search is separate | Simplest; no protection |
| Apache-2.0 | Allowed | Yes | Very high | Fine | Patent grant useful for a platform |
| MPL-2.0 | Allowed (file-level copyleft only) | Yes | High | Fine | Protects modified files only |
| AGPL-3.0 | Blocked in practice (network clause) | Yes | Lower for enterprise legal teams | Consistent with pg_search | Corporate policies often forbid AGPL; check the company's own rules |
| Source-available (e.g. FSL, BSL) | Blocked | Varies | Low (not OSI open source) | Mixed | Does not meet "open-source" wording in the brief; the FSL converts to Apache-2.0 after 2 years **[unverified]** |

### Inferences
- **Dependency interaction**: distributing pg_search inside a Bazaar installer or as a bundled database triggers AGPL obligations for pg_search itself (source availability for the modified/distributed database). Using it as a separately-run service via SQL, unmodified, is a narrower case. This needs counsel, but the practical option is to buy a ParadeDB commercial licence if an installer will bundle it.
- **Avalonia XPF in an open-source repo**: if XPF packages are referenced in the public repo, anyone building from source may need a paid licence. Recommendation: keep an abstraction (`ITerminalHost`, `IWebEditorHost`) in the public repo with a free implementation, and put the XPF-backed head in the private repo, until the XPF licence is confirmed to permit it. **[needs legal/licence check]**.
- **Stated goal ("prevent closed-source hosted forks, but maximise adoption")**: AGPLv3 meets the first goal. Adoption cost is real: some companies ban AGPL internally. Apache-2.0 with a clear trademark policy for the "Bazaar" name is the adoption-first path; trademark control is a separate tool from the code licence.
- **Recommended pairing**: AGPL-3.0 for the server and client code if the hosted-fork concern is central; Apache-2.0 for `Bazaar.Sdk` and `Bazaar.Contracts` so plugin authors and client vendors are not blocked by AGPL.
- **CLA vs DCO**: a DCO (`Signed-off-by:` on each commit) certifies the contributor's right to submit and keeps copyright with contributors. A CLA allows future relicensing or dual licensing (e.g. a commercial licence for enterprises). For a solo project that may want a commercial track, a CLA (or copyright assignment) gives the most flexibility; for adoption-first, DCO is simpler. Not legal advice.
- **AI-agent-authored commits**: copyright in purely machine-generated output is uncertain under current US practice **[unverified in this session]**. Practical approach: a human sponsor signs off every agent commit (DCO), and agent authorship is recorded in `Co-Authored-By` trailers. GitHub App bots cannot legally certify the DCO themselves, so the human sponsor must be the signer. Keep a log of which human approved which agent PR.
- **Attribution**: `NOTICE` file for Apache-2.0 or third-party notices for the MIT/AGPL dependencies; generate it from the NuGet dependency list in CI.

### Gaps
- Confirmed current XPF licence terms for open-source redistribution — not retrieved.
- Enterprise XPF price conflict (€55,000 official vs €89,000 aggregator) — unresolved.
- Current US Copyright Office guidance on AI-generated code — not retrieved in this session.
- Whether a commercial ParadeDB licence fits a hobby budget — pricing not found.

---

## Key Question 5: Secret handling across the two-repo split

### Takeaway
Keep credentials and Soul/Vault data out of the public repo by construction: the public repo never has them, CI on forks never receives them, and every commit is scanned before it leaves a developer machine or agent sandbox.

### Cited Findings
- Fork PR workflows do not receive repository secrets by default — [GitHub community and vendor guidance](https://stepsecurity.io/blog/defend-your-github-actions-ci-cd-environment-in-public-repositories) (vendor blog; primary GitHub docs not retrieved this session).
- Secret scanning and push protection are available for public repositories — **[unverified in this session; expected from GitHub docs]**.

### Inferences
- **Public repo**: (1) pre-commit hook with `gitleaks` or `trufflehog` in both developer and agent workflows; (2) GitHub secret scanning plus push protection enabled; (3) a CI "forbidden paths" check that fails if `soul/`, `vault/`, `*.env`, `*.pfx`, `*.keystore`, or similar appear; (4) `.env.example` and `appsettings.example.json` only, with secrets read from environment or from Windows DPAPI-protected files at runtime.
- **Private repo**: consumes `bazaar` as a submodule or NuGet package. Nothing flows from private to public. Secrets live in GitHub Environments scoped to `main`, plus the NUC's local secret store.
- **Agents**: each agent's GitHub App private key is a secret; keep it in the NUC's secret store, never in a workflow file or the repo. Scope each App to the minimum repositories.
- **Two-repo risk**: the main failure mode is a copy-paste of private config into the public repo. Mitigate with the forbidden-paths CI check and by reviewing the first PR of each new config file.

### Gaps
- Whether GitHub push protection covers custom patterns for Bazaar-specific credentials — not verified.

---

## Key Question 6: Release engineering (installers, auto-update, code signing)

### Takeaway
For the Avalonia desktop clients, **Velopack** is the lowest-effort path to installers plus delta auto-update hosted on GitHub Releases. MSIX is the better choice only if Store distribution or App Installer-based updates are a goal. Code signing is the real cost driver; free or low-cost OSS options exist but eligibility varies.

### Cited Findings
- Velopack: a .NET installer and auto-update framework that takes build output and produces an installer, updates and delta packages in one command; updates can be hosted on any static file storage including GitHub Releases; it migrates Squirrel apps — [Velopack docs](https://docs.velopack.io/) and the NuGet package listing (search results, not fetched in full).
- Velopack's Avalonia support was not confirmed in the search results. **[unverified]**
- Azure Artifact Signing (renamed from Trusted Signing): one report gives $9.99/month for 5,000 signatures (one certificate profile) and $99.99/month for 100,000 signatures — [secondary report via search](https://azure.microsoft.com/products/trusted-signing) (pricing from a secondary source, **[unverified]**). Eligibility is restricted to organisations or individuals in the USA and Canada, or organisations in the EU and UK, per the same report.
- Azure Artifact Signing's open-source fit (discounts or a free tier) was not found.

### Inferences
- **Desktop (Windows)**: Velopack for the installer and auto-update; sign the installer and binaries. Unsigned builds trigger Windows SmartScreen warnings. A signing service costs roughly $10/month if eligible (per the unverified pricing above).
- **Android**: sign with a keystore in the private repo; Play Console registration carries a one-time fee **[unverified]**. Sideload APKs need no store fee.
- **Linux**: AppImage or Flatpak via separate packaging; not researched in this session.
- **Hobby budget**: the cheapest path is unsigned Velopack builds for early testers plus a signing step added once Artifact Signing or an OSS signing programme is confirmed eligible. Free OSS signing programmes exist for some projects **[unverified; not researched]**.
- **MSIX**: stronger integration with Windows identity and the Store, but requires a signing certificate trusted by the target machines and a packaging pipeline. Not recommended as the first installer for a hobby project.

### Gaps
- MSIX vs Velopack comparison for Avalonia — the search returned Velopack material only.
- Current free or discounted code-signing options for open-source projects — not confirmed.
- Exact Avalonia 12 Android packaging steps — not verified.

---

## Overall recommendation summary (derived from the above)
1. **CI engine**: GitHub Actions. Hosted runners for the public repo; a dedicated, ephemeral self-hosted Windows runner on the NUC only for the private repo's trusted deploy job. Revisit the self-hosted runner billing status before committing.
2. **Repo layout**: as in Key Question 2, with `Bazaar.Server` (library) + `Bazaar.Server.WindowsService` (host), and `Bazaar.Client.Core` / `Bazaar.Client.Windows` / `Bazaar.Client.Android` / `Bazaar.Client.Linux`. Central Package Management, `Directory.Build.props`, `.editorconfig`, and a single build script.
3. **Licence**: decide between AGPL-3.0 (server and client) with Apache-2.0 SDK/contracts, or Apache-2.0 throughout for maximum adoption. Keep XPF out of the public repo until its licence is checked. Use DCO with human sponsors for agent commits, or a CLA if a commercial track is planned.
4. **Secrets**: public repo never holds private config; forbidden-paths CI check plus secret scanning and pre-commit.
5. **Release**: Velopack for Windows; signing deferred until an eligible option is confirmed.

## Items to verify before the architecture is locked
- GitHub self-hosted runner billing status (postponed vs live) and private-repo free minutes.
- Hosted-runner Android SDK availability and Avalonia.Headless behaviour on hosted Windows.
- XPF licence terms for open-source distribution, and the Enterprise price.
- ParadeDB commercial licence cost and whether an installer bundling pg_search triggers AGPL source obligations (counsel).
- Azure Artifact Signing eligibility and pricing; any OSS signing programme.
- Whether Velopack supports Avalonia apps as documented.
