# Contributing to Bazaar

## What Bazaar is

Bazaar is an open-source platform for a persistent AI software company: agents
with their own identity, memory and accounts coordinate in group chats, with no
master orchestrator. The server is a .NET 11 program, and the clients are
Avalonia applications that render what the server computes.

The project is pre-alpha. Most of what exists is design, and the code
scaffolding is being added now.

## Design docs

The design lives in `docs/deep-dives/`. Start with `docs/deep-dives/Bazaar.md`,
then read the document for the area you plan to touch. Research behind the
decisions is in `docs/research/notes/` and `docs/research/reports/`.

Each decision carries one of these status tags:

- **DECIDED**: settled. Build on it.
- **PROPOSED**: suggested but not yet confirmed. Do not build on it without asking.
- **REJECTED / CHANGED**: pushed back on or revised. Do not build on the old version.
- **RESEARCHED**: a factual claim checked against sources.
- **OPEN**: unresolved. Do not build on it without asking.

Only DECIDED items are built on. If your change needs a PROPOSED or OPEN item,
raise it in an issue or pull request first. The project owner has the final
say. When a design doc and the code disagree, fix the doc in the same change or
ask first.

Changes under `docs/deep-dives/`, `CLAUDE.md` and `.claude/` need the owner's
review (see `.github/CODEOWNERS`).

## Building and testing

You need the .NET 11 SDK. The exact version is pinned in `global.json`.

Forward-facing compatibility: write for the newest supported .NET. Use current
APIs, avoid obsolete or deprecated ones (warnings are errors), and never pin to
or add code paths for an older runtime. See `CLAUDE.md` and `docs/design/DECISIONS-LOG.md` round 15.

- Linux and macOS: `./build.sh`
- Windows: `./build.ps1`

Every behaviour change comes with a test. Run the build and the tests before
you commit. UI changes need an Avalonia.Headless test, because the UI can be
verified without a display.

## Branches and pull requests

- Work on a feature branch. Never push to `main`.
- Open pull requests as drafts first. Mark one ready for review once the build
  and tests pass.
- Keep pull requests small and focused, one change per PR. Split large work into
  a series of PRs.
- Use the pull request template. Link the design doc your change relies on, and
  say which of its decisions are DECIDED.
- Update the docs in the same PR when behaviour or design changes.

## Pull request ownership

A thread owns its pull request until it is merged or closed. The owning thread
answers review comments, fixes failing checks, keeps the branch current with
`main`, and merges or closes the PR. Nobody else has to chase it.

The automation in `.github/workflows/` backs this up:

- `pr-automerge.yml` enables squash auto-merge on non-draft PRs into `main`. GitHub
  merges a PR once every required check passes, including the sign-off check.
- `pr-maintenance.yml` merges `main` into PR branches that fall behind, and re-runs
  the Windows WebSocket test flake once.
- `pr-stale.yml` labels a PR `stale` and comments once after 24 hours with no
  activity. A PR labelled `duplicate` that is also idle is closed. Nothing else is
  closed automatically.

None of this weakens branch protection or a required check. Do not add
gitleaks allowlist entries to get a PR through.

## Sign-off (DCO)

Bazaar uses the [Developer Certificate of Origin](https://developercertificate.org/)
(DCO). Human contributors sign off their own commits with `git commit -s`, which
adds a `Signed-off-by:` line.

Pull requests are squash-merged. The sponsor adds a `Signed-off-by:` line to the
pull request description, and the squash-merge commit carries it onto the default
branch. The repository setting "Default commit message for squash merges" must be
"Pull request title and description" for this to work. The sign-off check in
`.github/workflows/pr-sponsor.yml` runs on non-draft pull requests. Do not add
that line on someone else's behalf.

## Contributor licence agreement (planned)

Modules and small fixes need only the DCO sign-off above. Core code will also need a
contributor licence agreement (CLA), so the project can keep its option to offer
commercial licences. Bazaar is AGPL-3.0 with paid commercial licences available by
agreement. Modules that use the public module API may be under any licence. The CLA
process is planned and not yet live: there is no signing tool yet. Until it is,
contact the project before sending a core change. The full policy is in
`docs/design/47-licensing-and-trademarks.md`, and counsel review is pending.

## AI-agent contributions

Bazaar is built with AI agents. Agent-authored commits carry a `Co-Authored-By:`
trailer that names the agent's model and session. A named human sponsor reviews
each agent-authored pull request and signs it off at merge. The sponsor is
responsible for the change, and the same review and test rules apply to it.

Do not present agent work as human-written, and do not remove agent attribution.

## Secrets and private data

Never commit secrets, credentials, API keys or tokens, Soul data, or private
company material. Keep local secrets in `.env` files or `*.local.json` files,
which are gitignored.

If a secret reaches a commit, tell the owner at once and treat the secret as
compromised. Rotating it matters more than removing it from history.

## Security issues

Do not report security problems in issues or pull requests. Follow
[SECURITY.md](SECURITY.md).

## Code of conduct

Participation is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## Licence

By contributing, you agree that your contribution is licensed under GNU AGPL-3.0,
the licence of the whole repository, including `src/Bazaar.Sdk/` and
`src/Bazaar.Contracts/` (owner decision, 2026-11-13). See [LICENSE](LICENSE) and
[NOTICE](NOTICE). The licensing is pending counsel review.
