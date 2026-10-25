# Contributing to Bazaar

## What Bazaar is

Bazaar is an open-source platform for a persistent AI software company: agents
with their own identity, memory and accounts coordinate in group chats, with no
master orchestrator. The server is a .NET 10 program, and the clients are
Avalonia applications that render what the server computes.

The project is pre-alpha. Most of what exists is design, and the code
scaffolding is being added now.

## Design docs

The design lives in `docs/deep-dives/`. Start with `docs/deep-dives/Bazaar.md`,
then read the document for the area you plan to touch. Research behind the
decisions is in `research_notes/` and `reports/`.

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

You need the .NET 10 SDK. The exact version is pinned in `global.json`.

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

## Sign-off (DCO)

Bazaar uses the [Developer Certificate of Origin](https://developercertificate.org/)
(DCO). Human contributors sign off their own commits with `git commit -s`, which
adds a `Signed-off-by:` line.

Pull requests are squash-merged. At merge, the owner adds a `Signed-off-by:`
line to the squashed commit. Do not add that line on someone else's behalf.

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

By contributing, you agree that your contribution is licensed under the licence
of the directory it changes: GNU AGPL-3.0 for most of the repository, and
Apache-2.0 for `src/Bazaar.Sdk/` and `src/Bazaar.Contracts/`. See
[LICENSE](LICENSE) and [NOTICE](NOTICE). The licensing is pending counsel
review.
