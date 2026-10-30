# CI and required checks

This page explains how the required checks on `main` behave for docs-only and
draft pull requests. The workflows are in `.github/workflows/`.

## Required checks and their behaviour

| Check | Workflow | Docs-only PR | Draft PR |
|---|---|---|---|
| `Build and test (ubuntu-latest)` | `ci.yml` | Runs | Runs |
| `Build and test (windows-latest)` | `ci.yml` | Runs | Runs |
| `Secret scan (full history)` | `ci.yml` | Runs | Runs |
| `Analyze (csharp)` | `codeql.yml` | Runs | Runs |
| `Analyze (actions)` | `codeql.yml` | Runs | Runs |
| `Signed-off-by line in PR description` | `pr-sponsor.yml` | Runs | Skipped by design |

- `ci.yml` and `codeql.yml` have no `paths:` filter, so they run on every
  pull request, whatever files changed. A docs-only change still produces the
  full set of results, so a required check is never left missing.
- `ci.yml` has no draft filter, so drafts build and test too. This keeps
  feedback fast while the work is in progress.
- `pr-sponsor.yml` runs only when `draft == false`. It also runs again on
  `ready_for_review`. A draft cannot be merged, so the sign-off line is not
  needed until the PR is ready. While a PR is a draft, this check shows as
  skipped. That is intended and not a failure.

## CodeQL: switch from default setup to advanced setup

`codeql.yml` replaces CodeQL default setup. GitHub does not allow both to run
at once, and default setup skips docs-only pull requests, which is why the
advanced workflow exists.

Before `codeql.yml` can run, the repository owner must:

1. Open Settings > Code security > Code scanning.
2. Under CodeQL analysis, choose "Switch to advanced" (or disable default
   setup and then add the advanced workflow).
3. Confirm that `.github/workflows/codeql.yml` is the workflow in use.

Until this switch is made, the `Analyze (csharp)` and `Analyze (actions)`
checks will not report on pull requests.

## Local checks

Before pushing, run `./build.sh` (Linux) or `./build.ps1` (Windows), and
`dotnet format --verify-no-changes` on the solution when C# changed. CodeQL
and secret scanning run in CI only.

## Security notes

- Third-party actions are pinned to full commit SHAs, with the version in a
  trailing comment. Do not switch them to tags.
- Workflows use `permissions: contents: read` by default. `codeql.yml` adds
  `security-events: write` only to its analysis job, because CodeQL needs it
  to upload results.
- No workflow needs a secret for the checks above.
