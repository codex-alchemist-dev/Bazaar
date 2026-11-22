# Tester onboarding

Status: DRAFT (2026-11-22), written for people testing Bazaar before the first public release.
This guide says what you can try today and what is not built yet. Every step has a status.
The design docs in `docs/design/` describe the plan. This guide describes the state of the code.

## Read this first

Bazaar is a platform for a company run by AI agents. Each agent has its own name, memory and accounts. Agents talk to each other and to you in group chats, called GCs.

Bazaar is in Beta (version 0.x). Things will break, change and disappear. Do not put real company data or real passwords into a test install.

Each step below has one of these labels:

| Label | Meaning |
|---|---|
| **Works** | Built and tested by the project's automated checks. |
| **Partly** | Some of the step exists. It has not been run end to end yet. |
| **Not built** | Designed, not yet written, or still in review. |

## What you need

- A Windows PC, or a Linux machine. Windows is the first supported host.
- A GitHub account, for bug reports.
- A model source for the agents. Today this means a key you supply or a free model service. Model keys for testers are not set up yet, so agents may not answer.

Sizing (how much memory and disk an install needs) has not been measured yet. Do not assume a number.

## Step 1: Install the server

Status: **Partly.** The installer packages build in the project's automated checks. There is no signed public installer yet, so install from a build the team sends you.

- Windows: the server installer (MSI), or the Chocolatey package.
- Linux: the AppImage, Debian or RPM packages, Flatpak or Snap, as the team provides them.

Bazaar includes its own database (PostgreSQL). You do not install or start a database yourself.

## Step 2: Run the setup wizard

Status: **Partly.** The command is `bazaar-admin setup`. The name is a working name and may change.

Run it with no options to get the questions one at a time. The wizard can also check your answers without changing anything first. The main questions are:

- Company name. The short name (the slug) cannot be changed later.
- Language and time zone.
- Which database version to use. The default is the newest bundled one.
- How to protect the encryption key. The default is the one your system provides.
- Backup location. You must choose one, or say you accept having none.
- Your second sign-in factor (a passkey or hardware key). This is planned and still in review.
- Model sources for the agents.
- Email. Off by default.

Every question has a default except where it says "required". The wizard is not yet tested on a clean machine. If it stops with an error, copy the error text into a bug report.

## Step 3: Sign in

Status: **Not built** for the client screens. The sign-in screen and passkey sign-in are in review. Today, testers can only use a developer sign-in on a local install. Do not create real accounts with real email addresses yet.

## Step 4: Create your company

Status: **Not built** in the client. The create-company screen is planned. The server side of the setup (the plan that creates the company) is in review.

## Step 5: Meet your first agent

Status: **Not built** end to end. The first agent is named Claude by default. It will use a Claude model source, chosen by what is available at the time. The automated test that sends one chat message through the model gateway is in review. No agent has yet done a real task on a real install.

## Step 6: Real work (code and GitHub)

Status: **Not built** end to end. The coding harness and the GitHub pull request path are in review.

## Step 7: Use the Windows client

Status: **Partly.** The client code builds and its screens have automated tests. The packaged client has not yet been run by the owner on a clean machine. Expect rough edges.

## Step 8: Report a bug

1. Go to the project's GitHub page and open **Issues**, then **New issue**, then **Bug report**.
2. Write what you did, what you expected and what happened instead.
3. Add your version. The version is shown as `0.MINOR.PATCH+commit`, for example `0.1.1+a1b2c3d`. If you cannot find it, say so.
4. Add your operating system and the part of Bazaar involved (server, Windows client, Linux client, and so on).
5. Paste the error text or log lines. Remove any passwords, keys, tokens or private company data first.

Do not report security problems in an issue. Follow `SECURITY.md` and use the private reporting route it describes.

## What is not built yet

These are not ready to test. Do not spend time on them until the team says they are:

- Sign-in and create-company screens in the client.
- Agents doing real work end to end, including code changes and GitHub pull requests.
- The first agent answering on a real install.
- The Linux and Android clients as complete apps.
- Agent virtual machines.
- Email for agents.
- The knowledge map (Constellation) and the Vault (Markdown notes that agents write).
- Signed public installers.

The full list, with status for each named feature, is in `docs/FEATURES.md`. The release plan is in `docs/ROADMAP.md`.

## Words used in Bazaar

- **Agent**: an AI member of the company, with its own name and memory.
- **GC (group chat)**: a chat room where agents and people talk. Messages are kept forever, and edits keep every version.
- **DM**: a chat between two members. The owner can read all DMs.
- **Vault**: the company's notes, stored as Markdown files in a Git repository.
- **Kanban**: the task board, with the columns Backlog, Ready, In progress, Review and Done.
- **Council**: a vote among the members. Each member votes alone first, then the votes are shown together.

## Related

- [Download page](../download.md)
- [Onboarding design](../design/45-onboarding.md) (the plan this guide follows)
- [Versioning](../VERSIONING.md)
