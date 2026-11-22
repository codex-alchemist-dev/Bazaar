# Pull request flow: drafts, sign-off, required checks

Status: DECIDED (owner, 2026-10-24 and 2026-10-25). The workflow files are the source of truth:
`.github/workflows/ci.yml` and `.github/workflows/pr-sponsor.yml`.

## How the pieces fit

- Agents open PRs as drafts. A draft cannot be merged, so it does not need a sign-off yet.
- The sign-off check (`pr-sponsor.yml`) skips on drafts. A skipped job counts as a pass
  for a required check, so a draft is not blocked by it.
- Marking the PR ready for review fires `ready_for_review`, which reruns the sign-off check
  with the PR no longer a draft. The check then needs a `Signed-off-by:` line in the description.
- The human sponsor adds that line. The owner squash-merges with "Pull request title and
  description" as the squash message, so the line reaches the commit on `main`.
- CI load rules (owner, 2026-11-19, card "Dedupe + skip docs-only"):
  - A branch is built once, not twice. `ci.yml` runs on `pull_request` and on `push` to `main` only.
    `codeql.yml` keeps `push` on every branch (that run is the one GitHub links to the PR) and runs
    its `pull_request` trigger only for fork PRs.
  - Docs-only PRs skip build, test and CodeQL. A first job (`changes`) lists the files changed against the
    base; the heavy jobs have `if: needs.changes.outputs.code == 'true'`. The jobs still exist, so the
    required checks report as skipped, which counts as a pass. There is still no `paths:` filter on any
    trigger, so a required check is never missing. Runs on `main` and the weekly CodeQL run always do the
    full work.
  - "Code" means anything outside `docs/**`, `**/*.md` and `.github/ISSUE_TEMPLATE/**`. A change to
    `.github/workflows/**` counts as code.
- CodeQL scans only ready pull requests (owner, 2026-11-22). A branch push is scanned when its branch has an open,
  non-draft PR. A draft, or a branch with no PR yet, reports no Analyze check at all, so it can never show a green
  one. When a PR is opened ready or marked ready for review, `codeql-on-ready.yml` dispatches `codeql.yml` on the
  branch; that run is the scan the required Analyze checks come from. If the dispatch fails the checks stay missing
  and the PR cannot merge. Main, the weekly run, manual runs and fork PRs always scan.
- Runner choice (2026-11-22): the repository is public, so GitHub-hosted runners are free. The `changes` job,
  Secret scan, CodeQL, PR sign-off and the Android and Linux start-up gates always run hosted, so the cheap
  required checks never queue behind the owner's PC. Build and test (and the PostgreSQL 19 leg) use the PC only for
  the owner's own same-repo runs, and only while the repository has at most `CI_OVERFLOW_QUEUE` queued runs
  (default 8); otherwise they overflow to hosted. Repository variable `CI_RUNNER_MODE` forces `self`, `hosted`
  or `auto`. The default is `hybrid` (owner 2026-11-22, PC runners back on): Linux legs run on the PC's Linux runners for the owner's own same-repo runs whatever the queue, and Windows legs run on GitHub, until the PC's Windows workspaces are cleaned and the variable is set to `auto` or `self`. Set `hosted` to keep every job off the PC. Publish smoke runs on pull requests (not drafts) that touch code, and on main.
- Idle-runner routing (2026-11-22, owner: whichever pool is free takes the job): the default mode is `idle`. A job
  cannot race both pools, so the `changes` job asks GitHub which PC runners are online and not busy at that moment. A
  free PC Linux runner means the Linux leg (and the PostgreSQL 19 leg) goes to the PC, otherwise to GitHub. The Windows
  leg goes to the PC only when repository variable `CI_PC_WINDOWS` is `true`, otherwise it stays hosted. Listing runners
  needs repository administration read access, which the workflow token lacks, so the owner adds a fine-grained token
  (Administration: read, this repository only) as repository secret `CI_RUNNERS_READ_TOKEN`. Without it, or on any API
  error, `idle` behaves like `hybrid`. Known limits: the check is a snapshot, so several runs starting together can all
  pick the same free runner; and a job already sent to the PC waits there if the PC goes off (set `CI_RUNNER_MODE` to
  `hosted`). A watchdog that moves a stuck job is not built (a queued job cannot be moved; the run would have to be
  cancelled and dispatched again, which risks the required checks).
- Faster pull request builds (2026-11-22, owner: do everything to speed up CI): a pull request builds, tests and
  format-checks only the affected projects, through one temporary solution (`scripts/ci/build-affected.sh` and
  `.ps1`, which write `Bazaar.affected.slnx`, not tracked). That is one restore, one parallel build, parallel tests
  and a format check that loads only those projects. The Windows leg of Build and test works only when a
  Windows-specific project can be affected (`windows` output of `scripts/ci/affected.py`: a project named
  Windows or HyperV, or something it depends on, changed); otherwise it skips every step on an Ubuntu runner and
  still reports green, so the check name stays. `main` always runs the full Windows build, test and format check.
- Required checks are set in the "Protect main" ruleset. Their names must not change:
  renaming a job breaks the match and leaves PRs waiting forever.

## Merge queue (prepared 2026-11-22, not on until the owner enables it)

`ci.yml`, `codeql.yml` and `pr-sponsor.yml` now also start on `merge_group` events, so every required check reports
on a queue run. A queue run is not a pull request, so `changes` treats it like `main`: full build, test and format,
the Windows leg included, and a real CodeQL scan. The sign-off job is skipped there (it was checked on the PR).
Pull requests keep the light affected-only checks. With the queue on, a pull request waits in the queue for the one full
run instead of the whole set repeating each time `main` moves.

Owner steps (GitHub web, Settings > Rules > Rulesets > "Protect main" > Edit):
1. Tick "Require merge queue".
2. Merge method: Squash. Build concurrency 3. Minimum group size 1, maximum 3. Wait time to meet the minimum group size 5 minutes.
3. Status check timeout 60 minutes. "Only merge non-failing pull requests" (all queue entries must pass).
4. Leave the required status checks as they are (same six names). Save.
Auto-merge keeps working: a pull request with auto-merge on joins the queue when its checks pass. Check once that the
squash commit still carries the `Signed-off-by` line from the description. If a group fails, GitHub drops the bad
pull request and re-runs the rest.

## Required checks

| Required check | What runs it | When it reports on a docs-only PR |
|---|---|---|
| Build and test (ubuntu-latest) | `ci.yml`, job `build-test`, matrix leg | Skipped (passes) on docs-only PRs; otherwise runs. |
| Build and test (windows-latest) | `ci.yml`, job `build-test`, matrix leg | Skipped (passes) on docs-only PRs; otherwise runs. |
| Secret scan (full history) | `ci.yml`, job `secret-scan` (gitleaks CLI) | Always. Scans full history, not just the diff. |
| Analyze (csharp) | CodeQL default setup (not a workflow file in this repo) | Not reported. Default setup skips docs-only PRs, so the check never appears and the PR waits on it. |
| Analyze (actions) | CodeQL default setup (not a workflow file in this repo) | Not reported. Same as above. |
| Signed-off-by line in PR description | `pr-sponsor.yml`, job `signed-off-by` | On non-draft PRs only. Skipped on drafts, which counts as a pass. |

## Automation (keeps PRs from being orphaned)

- `.github/workflows/pr-automerge.yml` enables squash auto-merge on non-draft PRs into `main`
  (or merges directly when GitHub reports CLEAN and auto-merge is unavailable). It never uses
  `--admin`. Logic: `.github/scripts/pr-automerge.sh`.
- `.github/workflows/pr-maintenance.yml` runs hourly and only reports PRs that are behind
  `main`; it never pushes to a PR branch. A GITHUB_TOKEN push starts no pull_request workflows,
  and the CodeQL run on that head ends as action_required, so the Analyze checks never report
  and the PR stays blocked. Main does not require branches to be up to date
  (`strict_required_status_checks_policy` is false), so a behind PR can merge without an
  update. Logic: `.github/scripts/pr-keep-current.sh`. It runs every 30 minutes to re-run a
  failed Windows job once when the failure is the WebSocket flake.
- `.github/workflows/pr-stale.yml` runs daily. Idle for 24 hours means no commit, review or
  non-bot comment. It labels the PR `stale` and comments once. A PR with the `duplicate` label
  that is also idle is closed.
- The owning thread still owns each PR until it is merged or closed (see CONTRIBUTING.md).

## Known gap: CodeQL on docs-only PRs

The two `Analyze` checks come from CodeQL default setup, which skips docs-only PRs. On such
a PR the required checks never report, so the PR shows them as missing. This is a known gap.
The fix is on branch `agent/ci-codeql`, a separate change. Until that merges, a docs-only PR
may need the owner to act on the missing checks, or CodeQL must be configured to run on it.

## Practical steps

1. Open the PR as a draft. CI and sign-off are skipped on drafts (CodeQL still runs on the branch push). Run CI by hand with `workflow_dispatch` if the draft needs a build.
2. Fix any failing build or secret-scan leg. Do not weaken either check.
3. Ask the owner to add the `Signed-off-by:` line to the description.
4. Mark the PR ready. CI starts its first full run (`ready_for_review`) and sign-off reruns; both must pass.
5. Check that the Analyze checks reported. On a docs-only PR, see the gap above.
6. Merge only when every required check is green, with no conflict and no open blocking review thread.

## Related

- `.github/workflows/ci.yml`: build, test, secret scan.
- `.github/workflows/pr-sponsor.yml`: sign-off check.
- `docs/VERSIONING.md`: release channels.
