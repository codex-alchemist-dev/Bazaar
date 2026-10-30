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
- CI (`ci.yml`) runs on every push and pull request, drafts included. It has no paths filter,
  so docs-only and YAML-only PRs still run the full build and report.
- Required checks are set in the "Protect main" ruleset. Their names must not change:
  renaming a job breaks the match and leaves PRs waiting forever.

## Required checks

| Required check | What runs it | When it reports on a docs-only PR |
|---|---|---|
| Build and test (ubuntu-latest) | `ci.yml`, job `build-test`, matrix leg | Always. No paths filter. |
| Build and test (windows-latest) | `ci.yml`, job `build-test`, matrix leg | Always. No paths filter. |
| Secret scan (full history) | `ci.yml`, job `secret-scan` (gitleaks CLI) | Always. Scans full history, not just the diff. |
| Analyze (csharp) | CodeQL default setup (not a workflow file in this repo) | Not reported. Default setup skips docs-only PRs, so the check never appears and the PR waits on it. |
| Analyze (actions) | CodeQL default setup (not a workflow file in this repo) | Not reported. Same as above. |
| Signed-off-by line in PR description | `pr-sponsor.yml`, job `signed-off-by` | On non-draft PRs only. Skipped on drafts, which counts as a pass. |

## Known gap: CodeQL on docs-only PRs

The two `Analyze` checks come from CodeQL default setup, which skips docs-only PRs. On such
a PR the required checks never report, so the PR shows them as missing. This is a known gap.
The fix is on branch `agent/ci-codeql`, a separate change. Until that merges, a docs-only PR
may need the owner to act on the missing checks, or CodeQL must be configured to run on it.

## Practical steps

1. Open the PR as a draft. CI runs; sign-off is skipped.
2. Fix any failing build or secret-scan leg. Do not weaken either check.
3. Ask the owner to add the `Signed-off-by:` line to the description.
4. Mark the PR ready. Sign-off reruns and must pass.
5. Check that the Analyze checks reported. On a docs-only PR, see the gap above.
6. Merge only when every required check is green, with no conflict and no open blocking review thread.

## Related

- `.github/workflows/ci.yml`: build, test, secret scan.
- `.github/workflows/pr-sponsor.yml`: sign-off check.
- `docs/VERSIONING.md`: release channels.
