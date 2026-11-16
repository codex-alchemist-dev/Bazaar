# Native git and PR system (sub-doc of 21)

Status: PROPOSED (all four open questions resolved, section 9; owner to mark DECIDED). Written 2026-11-16 from the owner's git-model statement (recorded as DECIDED in doc 21, "Decided inputs") and docs 05, 06, 07, 14, 15, 17, 18 and 21. Not built. Items below marked OPEN need the owner's answer before code. Doc 21 keeps GitHub-specific detail; this sub-doc holds the provider-neutral design so the two do not conflict.

## 1. The owner's statement (source of this design)

Bazaar's own git and PR system is the single endpoint and the single GUI for repositories, branches, PRs and repository settings. GitHub and GitLab are provider modules behind it, and the owner controls their repository settings through the same endpoint and GUI, extensively. The server and agents keep their own internal git flow. The GUI is for the client and the owner, and it also shows what agents do.

## 2. Layers

| Layer | What it is | Where it lives |
|---|---|---|
| Git engine | Local repository operations: worktrees, branches, commits, merges, diffs. One `IGitProvider` (docs 17, 18; LibGit2Sharp unless S23 fails) | Host side, `Bazaar.Server` |
| Native forge | Bazaar's own repositories, branches, PRs, reviews, checks and settings. The only API that agents, clients and the owner call | `Bazaar.Server`, a `Git` component (doc 21, "Decided inputs") |
| Forge providers | GitHub (doc 21) and GitLab. Each maps the native model onto one remote host and holds that host's credentials through the Identity Service (doc 14) | Provider modules, one per host |
| Client GUI | Lists PRs, shows hunks by head SHA and checks, takes the owner's review and merge. Clients never call a provider (doc 21, "Client review and merge") | Clients (docs 27 to 29) |

The engine is local. The forge is the contract. A provider is a mirror and an endpoint for the remote host, never the source of truth for Bazaar's own PR state.

## 3. Repositories and mirrors

- A **repository** record belongs to the native forge. It names its provider (`native`, `github`, `gitlab`) and, for a remote one, the provider's repository id.
- A repository may be **native-only** (no remote), **mirrored** (Bazaar holds the authoritative branches, pushed to a remote) or **remote-hosted** (the remote is authoritative and the forge reads and writes through the provider). The default for agent work is **mirrored**: the native forge is authoritative, and a remote is an optional provider copy (question 1, resolved).
- Repository settings (default branch, protection, required checks, collaborators, webhooks) are read and written through the native forge. For a provider repository, the forge calls the provider. Settings the provider cannot express are refused with a clear reason, never silently dropped.
- Credentials for each provider are held by the Identity Service (doc 14, `ISecretStore`). Git pushes to a remote run host-side, so no token enters a VM (doc 21, "Decided inputs").

## 4. Pull requests

- A **PR** is a native record: `pr(id, repository_id, base, head, state, title, body, draft, provider, provider_pr_id)`. The provider pair is nullable, so a native-only PR has no remote id. Doc 21 says the PR GC and `pr` / `pr_event` are keyed by `(provider, provider_pr_id)`; a native-only PR is keyed by its own id with provider `native`.
- A PR is always opened as a **draft** by an agent (CLAUDE.md, doc 21). A draft cannot be merged (doc 21, "PR lifecycle").
- Each PR has a PR GC (doc 21). State changes are `pr_event` entries (structured kinds, doc 01). The native forge writes them, so a provider event and a native event use the same record.
- A PR linked to a Kanban card (doc 12) moves the card to Done only when the PR is merged (doc 21, "Decided inputs").
- Reviews and comments are native records. A reviewer agent posts under its own identity. Its review is data and a gate flag, never merge authority (doc 21, "PR lifecycle"; doc 17).
- **Checks** are recorded from the local test run and from the provider's check runs. A failed check wakes the author (doc 21, "Event mapping").

## 5. Agent flow (end to end)

1. A Kanban task moves to In progress. The agent works in its own worktree on `agent/<task>` through the git engine (doc 17).
2. The agent commits with `Co-Authored-By` and pushes to the native forge, which pushes on to the remote if the repository is mirrored or remote-hosted (host side).
3. The agent calls `pr.open` (below) to open a draft PR against the default branch. The forge creates the PR GC and links the task.
4. Checks run. A reviewer agent is requested. Fixes are new commits on the same branch.
5. The owner reviews in the client and merges with the native merge action (below). The forge performs the squash merge, using the owner's credential (doc 21, "Decided inputs").
6. The merge writes a `pr_event` entry. The task moves to Done. The PR GC is archived, never deleted (doc 21).

No agent can merge. No agent can change repository settings.

## 6. Tools agents get (these are the grants of doc 06 and doc 07)

Each tool is a `ToolRegistration` plus a `ToolCapability` and a `ModelToolDefinition` (doc 06, "Registering a tool"). Each is granted per agent through `agent_tool_grant` (`agent_turns/0002`). Capability classes come from doc 15 (`CapabilityClass`).

| Tool | Capability | Default grant | Tier (doc 15) |
|---|---|---|---|
| `pr.open` | Post | author agents on their own branches | Autonomous with notice (draft only) |
| `pr.update` (title, body, mark ready) | Post | the PR's author | Autonomous with notice. Mark ready needs the sign-off line (doc 21, "Practical steps") |
| `pr.comment`, `pr.review` | Post | any agent in the PR's GC | Autonomous with notice |
| `pr.read`, `repo.read` | Read | agents in the repository's project GC | Autonomous |
| `pr.merge` | Merge | **no agent** | Owner only (approval in the client) |
| `repo.settings.write` | Grant-scope | **no agent** | Owner only |

Hard taint (reader sessions, doc 06) blocks every Post and Merge call. Soft taint does not (doc 15). Every call is logged with its arguments hash (doc 06).

## 7. Providers

A provider module implements one `IForgeProvider` contract:

- `Repositories` (read, create where the host allows, settings read and write)
- `Branches` (list, protect, delete)
- `PullRequests` (open, update, review, comment, check runs, merge with the approved credential)
- `Events` (webhook intake or polling, mapped to native events)

The GitHub module keeps the App-per-agent identity (doc 21, "Identity"). A GitLab module uses the same contract with its own credentials. Provider-specific names stay inside the module (doc 21, "Decided inputs"). A provider that cannot do an operation says so, and the forge reports it as unsupported to the owner.

## 8. Safety

- Taint and approvals follow docs 15 and 21: external comments are hard-tainted and read only by a reader session that hands over a schema-checked summary. Actor sessions never read them directly.
- Merge needs the owner's signed approval (doc 21, "Decided inputs"). An agent message is never a merge approval (CLAUDE.md).
- Forks: the fork-and-PR flow of doc 21 applies to GitHub only and stays gated on spike S22.
- Quotas: per-agent write caps come from doc 15's table (`security.quota.*`). Doc 21 keeps the GitHub-specific numbers.

## 9. Open questions

Each question was checked against the existing docs and the project principles (CLAUDE.md; the owner's statements of 2026-11-16) before it was kept here.

1. **Default repository mode for an agent's work. RESOLVED: mirrored.** The owner's statement makes the native forge the single endpoint and says "the server and agents keep their own internal git flow", so the native forge is authoritative. GitHub and GitLab are "providers behind it" (doc 21, "Decided inputs"). A remote is therefore an optional copy, which is the mirrored mode in section 3.
2. **Where native branches live. RESOLVED: each project's code lives in its own repository.** The Vault is "Markdown files in a git repo" holding company knowledge only (doc 18; doc 33; DECISIONS-LOG C38). Doc 25 names "the project repos" as a separate source from the Vault. The Vault repo is therefore not used for project code. Native branches for a project go in that project's own repository, with one repository per project and no mixing with the Vault.
3. **Which provider operations need the owner's credential. RESOLVED: merge and repository settings.** Owner-class credentials are held by the Identity Service, and "no agent lease can name an owner-class credential"; the server merges "only on signed owner approval" (doc 14, Design, owner-class credentials bullet; doc 21, "Decided inputs"; C25). Repository settings are the owner's controls (doc 15, tier 3 and grant-scope), so they use the same owner-class credential. Agents keep only their own App, which has no merge or settings rights (doc 21, "Identity").
4. **GitLab in v1. RESOLVED: option A.** The owner's 2026-11-16 statement is "support GitHub and gitlab EXTENSIVELY", and the owner asked for both to control repository settings through the same endpoint. GitLab is therefore in v1 scope, as a provider module on the `IForgeProvider` contract (section 7). The v1 scope in `docs/ROADMAP.md` is updated to match.

## 10. Work packages (after DECIDED)

1. `IForgeProvider` contract and the native store (`pr`, `pr_event`, repository records). Migration in the `git` namespace.
2. Native PR lifecycle (draft, ready, review, merge) with the tier checks above, on a fake provider.
3. `pr.open` and `pr.read` tools and their grants, against the native store.
4. GitHub module behind `IForgeProvider` (reuse `src/Bazaar.Server.GitHub`).
5. Client PR list and review pane (doc 27 to 29), reading the native forge only.
6. GitLab module (in v1 scope, after the GitHub module is stable).

Each package ships with tests, and the agent flow in section 5 gets one end-to-end test on the fake provider.

## Conflicts with other docs

- Doc 21 "Purpose" describes GitHub as the place where agents work. This sub-doc makes the native forge the place, and GitHub a provider. Doc 21 should say so in its Purpose section when it is next revised. Not edited here, to avoid a conflict with the open GC-model change on PR #605.
- Doc 21 "Components" names `PrService` and `GitWorkspace`. Those map to the native forge (`PrService`) and the git engine (`GitWorkspace`).
