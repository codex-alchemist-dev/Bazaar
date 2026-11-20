# 52. Git modules: Bazaar.Server.Git and Bazaar.Client.GitGui

Status: PROPOSED (2026-11-18). Details below the DECIDED inputs are the orchestrating Claude's most robust choice where docs 21 and 41 leave a gap; the owner can change any of them by asking. Implements doc 41 section 4.14 (module split, owner 2026-11-16) and the provider scope in doc 21.
Depends on: 14, 15, 18, 21, 27, 28, 29, 32, 41 (4.14, 4.15), 40, 41b · Used by: 12, 17, 19, 28, 29

## Purpose
One provider-neutral git and pull-request layer, so the owner reads diffs, reviews PRs and approves merges inside the Bazaar clients, on any GitHub or GitLab repository. Bazaar has no git server and no forge of its own.

## Decided inputs (do not change here)
- Four modules, each with its own manifest, version and metadata, side-loadable and replaceable on its own: `Bazaar.Server.Git`, `Bazaar.Client.GitGui`, `Bazaar.Client.GitGui.Github`, `Bazaar.Client.GitGui.Gitlab` (doc 41 4.14, owner 2026-11-16). GitLab is in v1.
- The server performs a squash merge only on the owner's signed approval, pinned to one head sha; agent messages never merge (C25, doc 21 "Merge request path").
- Clients never call a forge and never hold a forge credential (doc 27; C05). Pushes run host-side; tokens never enter a VM (doc 21).
- Git engine: one `IGitProvider`, LibGit2Sharp by default (C12, spike S23: one writer per checkout, a single merge lane, startup recovery).
- The diff view is Monaco's diff editor after the hosting gate S12; Android has no Monaco and needs a native diff surface (docs 21, 29, 41 4.15).
- A module references another only through its `.Abstractions` project; no module references `Bazaar.Server.csproj`; nothing is added to Server or Client.Core (CLAUDE.md, doc 40).

## Design

### Projects [PROPOSED]
| Project | Role | References |
|---|---|---|
| `Bazaar.Server.Git.Abstractions` | `IGitProvider`, `IForgeProvider`, `IForgeRegistry`, `IGitCredentialSource`, the ref and request records, the read model (`PrSummary`, `PrDetail`, `CheckRun`). Contracts only. | nothing |
| `Bazaar.Server.Git` | The git engine (`LibGit2GitProvider`, per-repo lock, startup recovery), the forge registry, the provider-neutral owner-signed merge gate, the server-proxied read service and its HTTP routes. | `Bazaar.Core`, `Bazaar.Contracts`, `Bazaar.Server.Git.Abstractions`, `Bazaar.Approvals.Abstractions`, `Bazaar.Audit.Abstractions` |
| `Bazaar.Server.Git.GitLab` | The GitLab `IForgeProvider` (REST v4): draft MR, files, review, squash merge, checks (pipelines), keyed by `sha`. A provider module with its own manifest. | `Bazaar.Server.Git.Abstractions` |
| GitHub provider | Stays in the GitHub module (`Bazaar.GitHub`, rename per doc 40 section 4a, PR 733). It gains an `IForgeProvider` bridge in a follow-up once 733 merges. Not part of this doc's first PRs. | `Bazaar.Server.Git.Abstractions` |
| `Bazaar.Client.GitGui` | Provider-neutral panes, view models, the diff surface seam and the Git view. | `Bazaar.Client.Core`, `Bazaar.Contracts` |
| `Bazaar.Client.GitGui.Github` | `IGitProviderUi` for `github`: wording, links, check and review state mapping. | `Bazaar.Client.GitGui` |
| `Bazaar.Client.GitGui.Gitlab` | `IGitProviderUi` for `gitlab`: merge-request wording, pipeline states, links. | `Bazaar.Client.GitGui` |

Tests live in `tests/<Project>.Tests`. Migrations: none in v1 (state is in the forge, the GC mirror and doc 21's tables).

### Server contract [PROPOSED]
- `IGitProvider` keeps doc 21's push and doc 18's worktree, merge and diff methods, and adds `FetchAsync`, `DiffAsync(repo, oldSha, newSha)` and `RecoverAsync()` (abort unfinished merges, prune stale worktrees at start). Every call takes the per-repo lock; one merge lane per repo.
- `IForgeProvider` is doc 21's `IGitHubAdapter`, neutral: `ProviderId`, `OpenDraftPrAsync` (search by head first), `ListPrsAsync`, `GetPrAsync`, `GetPrFilesAsync(pr, headSha)` (hunks keyed by head sha), `GetChecksAsync`, `GetHeadShaAsync`, `PostReviewAsync`. `SquashMergeAsync` is not on the public interface: it is on an internal executor reachable only through the merge gate, as in the GitHub adapter (`OwnerSignedMerge`).
- `IGitCredentialSource` is the token seam. `LeaseAsync(RepoRef, GitOperation, ct)` returns a short-lived `GitCredential` (disposable, memory only, never logged). The Identity Service implements it in host composition (doc 14); until the GitHub App registration exists, tests and the dev host use a fake. No forge adapter reads an environment variable or a file for a token.
- The merge gate checks, in order and refusing on the first failure: approval and request name the same head sha; the approval's action and argument hash match; the owner signature verifies (`IOwnerSignatureVerifier`, doc 32); the PR head is still that sha; every check on that head passes. Only then does it call the executor. Results map to doc 21's route results (`merge_not_approved` with a short reason). Each outcome is audited.
- `IForgeRegistry` resolves a provider by id. An unknown provider id is a typed refusal, never a fallback.

### Repository directory [PROPOSED]
`IGitRepositoryDirectory` (in `Bazaar.Server.Git.Abstractions`) maps a Bazaar repository id to `GitRepositoryInfo`: the `RepoRef` (provider and full name), the host-side clone path, the provider installation id (null when the provider has none) and the provider's own stable repo id. It is read-only. The module that stores repos implements it; the coding harness (doc 17) binds to it, so it needs no provider-specific type. The GitHub module supplies the GitHub rows after #733.

### Wire and routes [PROPOSED]
DTOs live in `Bazaar.Contracts/Git/`, JSON through `IWireCodec` (source-generated; no reflection). Routes sit on the guarded HTTP surface (doc 02): `GET /git/{provider}/repos/{owner}/{repo}/prs`, `GET .../prs/{n}`, `GET .../prs/{n}/files?head={sha}`, `GET .../prs/{n}/checks`. A request whose `head` no longer matches returns `409 head_moved` with the current sha. Merge keeps the route from PR 723 until a neutral `POST /git/merge` replaces it in a later docs PR. Responses are capped (`git.max_diff_bytes`); a larger file returns metadata and `truncated: true`.

Nested owners [PROPOSED]: a GitLab project in a subgroup (`group/sub/sub2/project`) is addressed by the same four shapes under one catch-all, `GET /git/gitlab/repos/{**path}`, where the fixed tail (`prs`, `prs/{n}`, `prs/{n}/files`, `prs/{n}/checks`) is parsed off the end and the repo is everything before it, so `RepoRef.FullName` is the full path. Only providers that allow nested owners (`gitlab`) may name a repo with more than two segments; for others such a path is a 404, and the one-segment GitHub shape is unchanged. A repo has at most 21 segments (GitLab's twenty groups plus the project). Malformed tails (a non-numeric PR number, an unknown suffix) are 404; a numeric PR number of zero or out of range, bad segments and a bad `head` keep the 400 answers above. The GitLab adapter sends the full path as one URL-encoded segment (`group%2Fsub%2Fproject`), so no slash in a path ever splits the project id.

### Client [PROPOSED]
- Pane kinds registered by `Bazaar.Client.GitGui`: `git.view` (repos, branches, recent commits), `git.prs` (PR list), `git.pr` (one PR: description, files, checks, reviews, the merge button), `git.diff`. Each has a mnemonic command and settings through the registry (doc 41 section 5).
- `IDiffSurface` is the seam. Two implementations: a native text diff (Avalonia, works everywhere, the Android surface) and a Monaco adapter that the Athanor module supplies once S12 passes. `Bazaar.Client.GitGui` ships the native one only, so it never waits on S12.
- `IGitGuiClient` is the seam to the server routes (a fake in tests). The merge button builds the owner-approval payload and asks the device signer (doc 32); the client never holds a forge credential.
- `IGitProviderUi` (id, display name, noun "pull request" or "merge request", check and review state mapping, deep-link builder) is registered per provider module. A repo whose provider has no UI module renders the neutral labels.
- `ServerGitGuiClient` is the server-backed `IGitGuiClient` [PROPOSED, 2026-11-18]. Client.Core has no generic authenticated request seam (`IWireRequester` is the socket; `VaultApi` is vault-only and adds the bearer itself), so GitGui defines `IGitHttpTransport` (GET path to status and capped body bytes) and ships `HttpGitTransport` over an `HttpClient` plus a per-request credential function; the head passes `ServerEndpoint.CreateHttpClient()` and the credential, so GitGui holds no credential and references no forge. Provider, owner and repo are checked against the routes' allowlist before use. Mapping: 200 to the DTO; 409 to `HeadMovedException`; 404 to `GitNotFoundException`; other 4xx to `GitRequestException` (short error code only); 5xx and connection failure to `GitServerUnavailableException`; malformed JSON, an odd status or a body over 16 MiB to `GitResponseException`.
- Constellation link (doc 19): a commit or branch row exposes the commands `git.open-in-constellation` and accepts `git.open-commit` with a sha. The link goes through a command, so neither module references the other.

### Settings [PROPOSED] (DECISIONS-LOG round 13)
| Key | Type | Default | Scope | Change needs approval? |
|---|---|---|---|---|
| `git.max_diff_bytes` | int | 2097152 | install | No; audited. Floor 65536 |
| `git.pr_page_size` | int | 30 | install | No; max 100 |
| `git.repo_lock_timeout` | duration | 30 s | install | No |
| `git.merge_requires_green_checks` | bool | true | install | Yes, owner. May not be turned off for repos with branch protection |
| `git.diff_context_lines` | int | 3 | user | No |

## Behaviour and edge cases
- A force-push changes the head sha; a diff or approval for the old sha is stale and refused with `head_moved`.
- A forge outage returns `502` with the last cached list marked stale; nothing is invented.
- Rate limits and ETag use are each adapter's job; the neutral layer surfaces `retry_after`.
- The engine never runs `git` hooks from a cloned repo. Submodules and LFS are not followed in v1 (S23 did not cover them).
- GitLab self-managed instances are reached by a configured base URL; the default is `gitlab.com`.

## Testing
- Engine: temp repos and a local bare remote, per-repo lock contention, recovery of a half-finished merge, run against every `IGitProvider`.
- Merge gate: stale head, failed check, bad signature, wrong action, wrong argument hash (each refuses before any forge call), and the happy path.
- GitLab adapter: recorded REST fixtures through a fake `HttpMessageHandler`: draft MR search-then-create, files, 401, 403, 404, 429 with `Retry-After`, 5xx.
- Client: Avalonia.Headless for each pane and the native diff surface, from fixtures; no network.
- Architecture tests: no new files in Server or Client.Core; no module references `Bazaar.Server.csproj`.

## Work packages
1. Abstractions and wire DTOs. 2. Engine with lock and recovery. 3. Merge gate and read service. 4. GitLab adapter. 5. Client panes and diff surface. 6. Provider UI modules. 7. Follow-up, after 733: GitHub `IForgeProvider` bridge and the neutral merge route. 1 first; 2 to 6 in parallel against the contracts.

## Open Questions
- Neutral merge route name and when the `/github/merge` route retires (PROPOSED: `POST /git/merge`, retire after the bridge).
- Submodules, LFS and hooks (S23 left them open).
- GitLab App-equivalent identity per agent (project access tokens versus a bot user): the Identity Service decides in doc 14; the token seam hides it.
