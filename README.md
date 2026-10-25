# Bazaar

Bazaar is an open-source platform for a persistent AI software studio. Agents with their own identity, memory and accounts coordinate in group chats (GCs), with no master orchestrator: the conversation is the coordination system. The server is a .NET 11 program that runs headless on Windows or Linux and does the computation; the clients are Avalonia applications that render what the server provides. Codex Alchemist is the first company built on Bazaar.

**Status: pre-alpha, design and scaffolding.** Most of what this repository describes is design. There is no running software yet.

## Read the design

- [`docs/deep-dives/Bazaar.md`](docs/deep-dives/Bazaar.md) is the entry point. The other documents in `docs/deep-dives/` cover the server, the clients, the agent identity model and the Constellation.
- [`docs/implementation-plan.md`](docs/implementation-plan.md) describes how the build is organised and why.
- Each decision carries a status (DECIDED, PROPOSED, REJECTED / CHANGED, RESEARCHED or OPEN). Only DECIDED items are settled. See [CONTRIBUTING.md](CONTRIBUTING.md#design-docs).

## Build

Requirements: the .NET 11 SDK. The exact version is pinned in `global.json`.

- Linux and macOS: `./build.sh`
- Windows: `./build.ps1`

The solution and build scripts arrive with the scaffolding. Until they do, this repository has no buildable code.

## Licence

- The repository is licensed under the GNU Affero General Public License, version 3 (AGPL-3.0). See [LICENSE](LICENSE).
- The SDK (`src/Bazaar.Sdk/`) and the shared contracts (`src/Bazaar.Contracts/`) are licensed under the Apache License, version 2.0. Each directory has its own `LICENSE` file.
- [NOTICE](NOTICE) explains the split and will hold third-party notices. The licensing is pending counsel review before the first public release.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Contributions use the Developer Certificate of Origin: human contributors sign off with `git commit -s`. Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately, as described in [SECURITY.md](SECURITY.md).
