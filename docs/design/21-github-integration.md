# GitHub Integration

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 2
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 18-vault.md · Used by: 06-agent-runtime.md, 11-reputation-and-peer-review.md, 12-kanban-and-dependencies.md, 17-coding-agent-harness.md, 19-constellation-and-git-solar-system.md, 28-windows-client.md, 29-android-client.md, 36-community-and-external-platforms.md

## Purpose
Lets agents work on GitHub as disclosed bots: one App per agent, webhooks or polling in, branches, PRs and issues out. Every PR is mirrored into a PR GC, so GitHub records Bazaar's work without becoming a coordination system.

## Git provider scope (owner, 2026-11-16)
Bazaar works on any GitHub or GitLab repository reachable over the internet. It has no git server of its own and no native forge. GitHub and GitLab are supported extensively through provider modules: repository settings, PRs, reviews, checks and merges. One Bazaar endpoint and the client GitGui modules (doc 41 section 4.14) control both. This doc describes the GitHub provider. The GitLab provider follows the same contract, with its own identity and credentials held by the Identity Service (doc 14). GitLab is in v1 scope.

## Decided inputs
- **Git model (owner, 2026-11-16, DECIDED).** Bazaar has no git server of its own. It works on any GitHub or GitLab repository, anywhere on the internet, through provider modules that sit behind one contract. The owner controls repository settings through the Bazaar server endpoint and the client GUI. The git client module is a custom GUI over those providers (diffs, PR panels and so on), built because the owner dislikes GitHub's mobile UI. The PR GC, `pr` and `pr_event` are provider-neutral in shape and keyed by `(provider, provider_pr_id)`; GitHub and GitLab are both provider modules.
- Machine accounts are barred by GitHub's terms. Agents use one GitHub App each, owned by org `codex-alchemist-dev`. Agents never create accounts; a human does (CLAUDE.md; Bazaar.Server.md §2; 14).
- Secrets: the Identity Service holds GitHub App keys and tokens are minted there. One server-side `ISecretStore` (owned by 14). Git pushes run host-side, so tokens never enter a VM (DECISIONS-LOG round 2, C05).
- Taint: external content taints hard; agent text taints softly and only from a tainted source session (round 2, C06). Reader sessions read external input and hand over summaries; actor sessions never read it directly (round 6, C18; Claude's reading, to confirm). Approval gates outward high-impact actions: posting, push, merge (C18, C06).
- Approval tiers use 15's ladder, with 36's stricter community defaults (round 7, C21).
- Git engine: one `IGitProvider`. LibGit2Sharp stays decided unless spike S23 shows it cannot do worktrees, merges or concurrency; the git CLI is an alternative implementation (round 3, C12).
- Fork-and-PR flow is built as decided; the owner accepts the risk that GitHub's docs contradict it (round 3, C13).
- Merge: the owner approves in Bazaar.Windows or Bazaar.Android, which replace GitHub's PR UI. The server performs the squash merge on the owner's signed approval. The credential is a dedicated owner GitHub App with merge rights or the owner's linked GitHub credential, chosen in setup and changeable in settings (rounds 8 and 12, C25).
- Done for a card tied to a PR means the PR is merged; reviewer approval is a flag (round 8, C28).
- Home host: no inbound connections by default. It polls GitHub; a tunnel or overlay is optional for real-time webhooks (round 8, C30).
- Every PR gets a PR GC (owner brief, 2026-10-25). GC types come from the registry in 01, and this doc registers `pr` and `inbox` (round 6, C19), and `issue` for issue GCs (PROPOSED, see Issue GCs; OPEN until the owner confirms the type).
- Commits carry `Co-Authored-By`; the owner signs off at squash-merge with `Signed-off-by` (DCO model, owner, 2026-10-25, CLAUDE.md licences line). CLA is not chosen.
- Agents never push to the default branch; PRs open as drafts; agent messages are never merge approvals (CLAUDE.md; 17).
- Shared quota keys live in 15's table (`security.quota.*`); GitHub-specific limits are owned here (round 10, C36).

## Design

**Identity [PROPOSED].** The owner registers one App per agent (`<agent>-bazaar`) in the org, with its own name, avatar and description. Permissions: contents, pull requests and issues read/write; checks and metadata read. Subscribed events: `pull_request`, `pull_request_review`, `pull_request_review_comment`, `issue_comment`, `issues`, `check_run`, `check_suite`, `push`. The private key lives in the Identity Service (14), never in a VM or a repo. No agent App is granted merge rights (question 1). The owner's merge credential (dedicated App or linked user token) is also held by 14, as an owner-class credential.

**Tokens [PROPOSED].** The Identity Service signs a short-lived App JWT, exchanges it for an installation token narrowed to the repositories and permissions of one lease, and gives the token only to the host-side GitHub adapter. Tokens stay in memory, are never logged, and never enter a VM. Lifetime of one hour is UNVERIFIED (from a summary of GitHub's docs) until live test T4.

**Components [PROPOSED].** `GitHubAdapter` (REST and GraphQL, ETags, rate state, retries), `GitHubIngress` (webhook intake when a tunnel exists, and the Events-API poller), `PrService` (PR-to-GC mapping, lifecycle, task links) and `GitWorkspace` (host-side worktrees, commits and pushes over `IGitProvider`).

**Fork-and-PR flow [DECIDED 2026-10-25, round 3; kept as decided, risk accepted; UNVERIFIED until S22].** Preferred path: fork upstream into the org, push `agent/<task>` to the fork, open a draft PR against upstream. GitHub's docs say the forks endpoint needs the App on the source account with access to the source repository. The owner keeps the decided flow and accepts that risk. Spike S22 tests it first. Live tests pick the behaviour:

| Test result | Behaviour |
|---|---|
| Fork and PR work without an upstream install (T2, T3) | Default flow for any public repo. |
| Fork or PR needs an upstream install | Only repos where the owner or a maintainer installs the App. Elsewhere the agent drafts and the owner posts (Tier 2). |
| Issue on an uninstalled repo refused (T1) | No writes to uninstalled repos at all. |

**PR lifecycle [PROPOSED].**
1. The author agent pushes `agent/<task>` and opens a draft PR. The server creates the PR GC once, keyed by PR node id (type `pr`, parent = project GC).
2. The task moves to Review with a `pr` link (12). A reviewer agent is requested and posts its review under its own App. The reviewer uses a different model family when the setting `github.reviewer_family_rule` requires it.
3. Checks run on GitHub-hosted runners (CI doc). A failed check wakes the author.
4. The author marks the PR ready when checks pass. Fixes are new commits.
5. The owner approves in the client (Tier 2). The server squash-merges with the chosen owner credential. The squash message carries the owner's `Signed-off-by` (logged as a platform event when `github.signoff_event` is on).
6. The server writes a `pr_event` GC entry (structured kind, 01). 12 moves the linked card to Done. 11 records the outcome. The PR GC is archived after `github.pr_gc_archive_days`, never deleted.

Reviewer approval is data for the owner and a gate flag on the card (12). It is not merge authority (17).

**Event mapping [PROPOSED].** Wake reasons belong to 06 (mention, assignment, and so on); this table only maps events to them.

| Event (after verification or poll) | Target | Wakes |
|---|---|---|
| `review_requested` for an agent | PR GC; assignee inbox item, kind `assignment` | Yes |
| Review submitted, changes requested | PR GC; author inbox item | Yes (author) |
| Comment that @-mentions an agent | PR or issue GC; inbox item, kind `mention` | Yes |
| `check_run` or `check_suite` failed | PR GC; author inbox item | Yes (author) |
| `push` to an agent branch | PR GC `pr_event` | No |
| `pull_request` closed or merged | PR GC; owner notice; 11 outcome | Owner only |
| `issues` assigned, assignee is an agent | Issue GC; assignee inbox item, kind `assignment` (PROPOSED: the doc named no issue assignment row; it mirrors `review_requested`) | Yes |
| Issue or comment from a non-member | `inbox` GC (question 2); provenance `external`, hard taint | Never; digest only |

**PR lookup by branch and membership of woken agents [PROPOSED].** (a) `github_pr.head_ref` holds the head branch name, stored from `pull_request.head.ref` only when the head repository is the base repository (a fork PR stores null, so a fork's branch name never matches a same-named branch of the base repository). A `push` with `ref = refs/heads/<name>` (not a tag, not a branch deletion) finds the open PR in the same repository whose `head_ref` is `<name>`, after the number and head SHA rules, so a push that arrives before the `synchronize` event has stored the new `head_sha` still lands in the PR GC. Only `pull_request` events change `head_ref`; a push never does. (b) An agent that an event names (author, assignee, requested reviewer) or wakes on a PR (review requested, @-mention, review feedback, failed check) becomes a PR GC member if it is not one yet. The add is idempotent, covers agents beyond the create cap, and skips external (hard-tainted) effects and owner notices. A stale `pull_request` event names no members.

**Owner membership of PR and issue GCs [PROPOSED, coordinator decision 2026-11-22, owner to be told].** The company owner is a member of every PR GC and issue GC by default, with no setting needed. The owner's principal id is read from the database (the one active `human` row with role `owner`, the same row the first-run seed uses) through the module-local port `IGitHubOwnerResolver`, so no id has to be configured. The PR and issue services add the owner at creation (after the creator, before the agents, not counted in the agent cap) and, when no owner exists yet (fresh install before first run), skip silently and add the owner at the next event for that PR or issue; the add is the same idempotent member add as for agents, so a replay changes nothing. Operator options: `Bazaar:GitHub:PrGc:OwnerIsMember` (bool, default `true`; `IssueGc:OwnerIsMember` overrides it for issues, and falls back to the PR value) lets an operator tighten this so the owner reads only through the implicit `owner-reader` role (01); `PrGc:OwnerPrincipalId` stays an optional override that also makes the owner the creator, and then the owner is a member whatever `OwnerIsMember` says (the creator is always a member). Because the owner is a member, an @-mention of the owner in a PR GC works through membership. GitHub inbox rows stay agent-only (06): an @-mention of the owner's GitHub login produces no inbox row and no wake; the mapper's owner notice (`pull_request` closed or merged) is unchanged.

**Issue GCs [PROPOSED].** An issue is mirrored the way a PR is, so an assignment, an @-mention in an issue or its body, and issue comments have a GC to be written to and a wake to point at. The server creates the issue GC once, keyed by issue node id (type `issue`, registered by the `github` component with `registered_by = github`; the PR GC's parent-link question applies the same way). `github_issue` is the read model: node id, repo, number, `gc_id` (unique), state, title, author login and `updated_at`. Members: the owner (the creator when `OwnerPrincipalId` is set, otherwise a member resolved from the database, see Owner membership above), the bridge principal, the issue author when it is an agent, the assignees that are agents, and agents @-mentioned in a member's text. An agent named by a later event is added then; at most `MaxAgentMembersAtCreate` (7) at creation. Only `issues` events change state, ordered by `updated_at` under a row lock (an older event is stored, never applied over newer state); a row first made by a comment holds the epoch, so the real issue event is not rejected as older. A GC is created only when the event is a member's or the issue itself names an agent (author or assignee); a stranger's issue with no agent involved stays in the `inbox` GC, and a stranger's @-mention never adds a member or wakes (hard taint, unchanged). Entries are written as GC kind `structured` (no new GC event kind is needed). Operator options, `Bazaar:GitHub:IssueGc`: `OwnerPrincipalId` (falls back to `PrGc:OwnerPrincipalId`, then the bridge), `GcTypeKey` (default `issue`; a different key must already be in `gc_type`), `MaxAgentMembersAtCreate`. OPEN: whether `issue` is the type key the owner wants; whether an issue GC is archived like a PR GC (`github.pr_gc_archive_days` covers PRs only); whether closing an issue notifies the owner; labels, milestones and issue-to-task links (doc 12); issue GCs for repos where the App is not installed (T1).

A non-member's text reaches an agent only as a reader-session summary (15). A woken agent that was mentioned by a stranger receives the summary and the hard-taint flag, never the raw text.

**Git operations [DECIDED 2026-10-25, round 3].** Worktrees sit on the host under the agent's mount (16). Every git operation goes through `IGitProvider` behind a per-repo lock. The engine is LibGit2Sharp unless S23 fails, in which case the git CLI is the implementation (18 defines the full interface). Pushes run host-side with a one-shot credential, so the token never reaches the VM. Commit author: the App's bot identity. Committer: the host service. Trailers: `Co-Authored-By` (model), `Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`. Agents never add `Signed-off-by`.

**Branch protection [PROPOSED].** On owner-controlled repos: the default branch requires a PR, one human approval and passing checks; no force-push or direct push. Agent Apps are on no bypass list. `agent/*` branches accept pushes; force-push only with lease.

**Rate limits [PROPOSED].**

| Limit | Source | Bazaar default |
|---|---|---|
| 5,000 requests/hour per installation | Secondary summary | Warn at 70%; defer reads at 90% |
| 80 content-creating requests/min; 500/hour | Secondary summary | `security.quota.github_writes_per_minute` (30 per installation, doc 15); `github.writes_per_hour` (300 per installation) |
| 900 REST / 2,000 GraphQL points/min | Secondary summary | ETag reads; no polling while webhooks are healthy |

On 403 or 429, honour `retry-after` and reset headers. Repeated breaches freeze the agent's GitHub writes and notify the owner (15).

**Ingress [PROPOSED].** Default, with no inbound ports: `GitHubIngress` polls the Events API every `github.poll_interval_minutes` (10) and reads PR and comment state for watched repos. Real-time webhooks are an optional upgrade, through an operator tunnel or overlay. The route `POST /hooks/github/{opaque}` is part of the authenticated HTTP surface owned by 02 (C15). When a webhook arrives: HMAC-SHA256 over the raw body, compared with `X-Hub-Signature-256` in constant time; reject other methods, bad signatures and oversize bodies. Dedupe on `X-GitHub-Delivery` as `dedupe_key = github:<delivery>`. Persist, return 2xx, then process from the table. Polling and webhooks feed the same dedupe table, so a delivery seen twice is applied once.

**Community presence [PROPOSED].** Every post carries the bot label and names the human sponsor. Agents file only where a maintainer asked or agreed (36). New public issues and PRs are Tier 2 (15's names). Caps: `security.quota.public_issue_pr_per_repo_day` (1 per repo per day, one value shared with 36) and `security.quota.public_items_per_platform_day` (doc 15). Repos outside owner-controlled orgs are reachable only if listed in `github.community_repo_allowlist`.

**Merge request path [PROPOSED].** The client sends a merge request for one PR and one head SHA, signed with the device-held owner key (key class and signing rules in doc 32, PROPOSED). The server verifies the signature, that the head SHA is still current, and that checks pass. It then calls `SquashMergeAsync` with the credential named by `github.merge_credential` (owner-class, held by 14). A changed SHA or failed check voids the approval; the owner re-approves. Agent messages never reach this path.

**Merge route [PROPOSED].** The client sends the merge request as `POST /github/merge` on the authenticated HTTP surface (doc 02 WP10), so clients still never call GitHub. The caller must be the human the approval names: the bearer's principal is a human and its id equals the signed human id; any other caller gets 403. The body is one JSON object: `pr` (owner, repo, number; the server supplies the installation of the merge credential, so a client cannot pick one), `merge` (head sha, commit title, commit message) and `approval` (the signed fields of doc 32 section 5a: action `pr.merge`, approval id, args hash, head sha, human id, device id, issued-at and expiry in Unix milliseconds, nonce, and the 64-byte signature; binary values are base64). The args hash covers the PR and the merge request, so the owner signs exactly what the card showed. The server checks, in order and before any call that changes GitHub, that the approval names this head sha and this merge, that the signature verifies and its nonce is unused (5a), that the PR head is still that sha, and that every check on that sha passes (check runs and commit statuses; a commit with no checks cannot be merged). It then merges with the credential named by `github.merge_credential`, on the installation the operator configured for it (`Bazaar:GitHub:Merge:InstallationId` until the `github_app` table carries the owner App). With no installation configured the route answers 502 `github_unavailable`.

| Result | Status | Body |
|---|---|---|
| Merged | 200 | `{"merged": true}` |
| Approval not valid for this merge, signature refused, head moved or checks not passing | 403 | `{"error": "merge_not_approved", "reason": "<short text>"}` |
| Caller is not the human the approval names | 403 | `{"error": "forbidden"}` |
| Malformed body | 400 | `{"error": "bad_request"}` |
| GitHub or the token source failed | 502 | `{"error": "github_unavailable"}` |

A refused merge spends no nonce unless the signature verified, so a forged or stale request cannot burn the owner's nonce. Any 403 or 502 leaves the PR untouched; the owner re-approves after the cause is fixed.

**Client review and merge [PROPOSED].** Bazaar.Windows and Bazaar.Android show server-proxied PR lists, file hunks keyed by head SHA, checks and the in-app review. The diff view uses Monaco's diff-editor mode only after the Monaco hosting gate (S12, round 5, C26). Android has no Monaco, so it needs a native diff surface (29). Clients never call GitHub.

**Client PR wire [PROPOSED].** Implementation detail of the two paragraphs above; no new authority. All messages are JSON through `IWireCodec` on the authenticated client connection, in `Bazaar.Contracts` under `GitHub/`. The server proxies; clients never see a GitHub token.

| Message | Direction | Fields |
|---|---|---|
| `pr.list` | request / reply | repo filter, state, paging cursor; reply rows: `PrId`, repo, number, title, author (agent or human), draft flag, `HeadSha`, checks rollup, `Revision` |
| `pr.summary` | request / reply | `PrId`; reply: `HeadSha`, summary text, changed-file list (path, status, additions, deletions), checks, reviewer-approval flag, linked card id |
| `pr.hunks` | request / reply | `PrId`, `HeadSha`, path, cursor; reply: hunks keyed by that `HeadSha`; a stale `HeadSha` replies `stale` with the current one, never old hunks |
| `pr.review` | request / reply | `PrId`, `HeadSha`, event (`comment` or `request_changes`), body; reply `applied` or `stale` or `refused`. Never `approve`: reviewer approval is data, not merge authority |
| `pr.merge` | request / reply | `PrId`, `HeadSha`, commit title and message, owner signature over the `bazaar.owner.v1` payload (action `pr.merge`, args hash from `OwnerMergeArgs.Hash`); reply `merged`, `stale`, `checks_failed`, `bad_signature` or `refused` |

The merge request carries no new approval kind in doc 15's card decisions. It is an owner-gated action, so it is signed with the shared owner payload (doc 32 section 5a) that `OwnerSignedMerge` already verifies. The client side is a sibling of the approval-card gate: the same biometric prompt and device-key signer, a different payload builder, still one fresh biometric per merge, single use, and dictation can never trigger it. Agent messages never reach `pr.merge`.

## Data and interfaces

```sql
github_app(agent_id pk, app_id bigint, installation_id bigint, account_login text,
           status text,                      -- active|suspended|revoked
           key_ref text)                     -- reference only, never the key
github_pr(pr_node_id text pk, repo_node_id text, number int, gc_id uuid unique,
          task_id uuid null, head_sha char(40), head_ref text null, base_ref text, state text, draft bool,
          updated_at timestamptz)  -- head_ref PROPOSED, migration 0008
github_issue(issue_node_id text pk, repo_node_id text, number int, gc_id uuid unique,
             state text, title text null, author_login text null, updated_at timestamptz)  -- PROPOSED, migration 0006 (0007 registers the issue GC type)
github_delivery(source text, delivery_id text, event text, received_at timestamptz,
                processed_at timestamptz null, primary key (source, delivery_id),
                payload jsonb null, event_type text null,
                process_attempts int, next_attempt_at timestamptz null, process_error text null)
github_rate_state(installation_id bigint, bucket text, remaining int, reset_at timestamptz)
```

Delivery rows keep the raw JSON body (`payload jsonb`, null when the body is not JSON) and the event type (`event_type text`), so the PR service and the event mapper read from the table and never need GitHub again. Retention follows the audit rule: rows are kept forever by default, they are crypto-shredded on erasure, and there is no sweeper. A replay never overwrites the first stored payload. The webhook processor claims unprocessed rows with `FOR UPDATE SKIP LOCKED`, counts attempts, retries a failed sink call after `next_attempt_at`, and marks an unparseable row processed with the reason in `process_error`. Webhook rows (`source = github`, key is the delivery guid) and polled rows (`source = poll`, key is the event id) dedupe separately and are not unified.

**PR read model [PROPOSED].** `pr.list` and `pr.summary` read `github_pr`, filled by the PR service. Two columns are added (migration 0009): `repo_full_name text` (`owner/name`, written from the event, so the read model can name the repo for the adapter) and `revision bigint` (rises by one on every real change to a row, set by a trigger, so a write that changes nothing keeps it). The installation that reads the watched repos is `Bazaar:GitHub:Ingress:InstallationId` (one installation until several are needed). Rows with no repo name or no head yet are not listed. Per row, checks come from the adapter at the stored head (cached by ETag); author kind is `agent` when the login resolves to a registered agent, else `human`; `LinkedCardId` is `github_pr.task_id`; the summary text states the size of the change ("N files changed, +A -D") because no author-written summary is stored; `ReviewerApproved` is false until a reviews source exists. `pr.list` pages newest first by `(updated_at, pr_node_id)` with an opaque cursor, 30 rows by default and 50 at most, and a malformed cursor means the first page. With no database the read model is empty.

GC entries for PR events follow the 01 `gc_event` / `gc_message` model (C09). Inbox items are owned by 06 (C04). The owner's merge credential is stored by 14, not in these tables.

```csharp
public interface IGitHubAdapter {
    Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest r, CancellationToken ct); // search by head before create
    Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken ct);
    Task PostReviewAsync(PrRef pr, ReviewRequest r, CancellationToken ct);
    Task SquashMergeAsync(PrRef pr, MergeRequest r, CancellationToken ct);       // only on signed owner approval
}
public interface IGitProvider {                                                  // C12; 18 defines the full surface
    Task PushAsync(WorkspaceRef ws, string branch, CancellationToken ct);       // host-side; token from Identity Service
}
```

Clients never call GitHub; they read server-proxied PR lists and file hunks keyed by head SHA.

### Settings [PROPOSED] (DECISIONS-LOG round 13)
Registry owned by doc 03. Changes are audited. Surfaced in the setup wizard and advanced settings (docs 27, 32). Shared quota keys owned by doc 15 (`security.quota.github_writes_per_minute`, `security.quota.public_issue_pr_per_repo_day`, `security.quota.public_items_per_platform_day`) are not repeated here. Settings may tighten the floors below; they cannot loosen them (no agent merge rights, no writes to uninstalled repos unless T1 passes).

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `github.merge_credential` | enum: `owner_app`, `linked_user` | `owner_app` [PROPOSED] | install | operator (owner) | Yes, owner |
| `github.install_scope` | enum: `all_repositories`, `selected_repositories` | `all_repositories` | install | operator (owner) | Yes, owner |
| `github.poll_interval_minutes` | int | 10 | install | operator (owner) | No; audited |
| `github.writes_per_hour` | int | 300 | install | operator (owner) | Yes, owner |
| `github.reviewer_family_rule` | enum: `require_different`, `flag_only` | `require_different` | install | operator (owner) | Yes, owner |
| `github.pr_gc_archive_days` | int | 7 | install | operator (owner) | Yes, owner |
| `github.signoff_event` | bool | true | install | operator (owner) | Yes, owner |
| `github.uninstalled_repo_issues` | enum: `off`, `per_issue_approval` | `off` (needs counsel) | install | operator (owner) | Yes, owner |
| `github.community_repo_allowlist` | list of repo node ids | empty (needs counsel) | install | operator (owner) | Yes, owner |

`github.uninstalled_repo_issues` has no effect unless live test T1 passes. Its options are documented for counsel: `off` posts nothing to uninstalled repos; `per_issue_approval` lets an owner approve each issue.

## Behaviour and edge cases
- Out-of-order events: apply by `head_sha` and `updated_at`. An older event is stored, never applied over newer state.
- A PR GC is created once; a replayed delivery creates nothing. Repos are keyed by node id, so renames do not break mappings.
- Suspended or uninstalled App: new leases are denied and open PR GCs are notified. Writes queue while GitHub is unreachable; each retry first checks whether the write already landed.
- A force-push changes `head_sha`; reviews on the old SHA are marked stale.
- Stranger comments are hard-tainted (15). Only a reader session reads them; the actor gets its summary.
- A merge is issued only after a signed Tier 2 owner approval in the client. Agent messages never trigger a merge.
- A minted installation token stays valid up to one hour after revocation (14). This is UNVERIFIED until T4.

## Testing
- Fake GitHub HTTP endpoint with recorded fixtures: 201, 202 (async fork), 403 and 429 with rate headers, and the fork refusal path (degrades to installed repos).
- Webhook fixtures: valid, bad signature, duplicate delivery, out of order, oversize. A poll and a webhook for the same event apply once.
- Stranger @-mention fixture: the woken actor sees only the reader summary and the hard-taint flag.
- Real Postgres: dedupe and PR-GC uniqueness under concurrent deliveries and polls.
- Merge request fixtures: stale head SHA, failed check, bad signature (each voids the merge).
- Git: temporary repos and a local bare remote for worktree, push and lock tests, run against every `IGitProvider` implementation. Avalonia.Headless: PR list and diff view from fixtures.
- Live tests (manual, against a human-created throwaway org, never in CI): T1 issue on an uninstalled repo; T2 fork into the org; T3 draft PR from the fork, with and without an upstream install; T4 token expiry, used at 55 and 61 minutes; T5 content-write burst, recording limit headers; T6 bot commit identity; T7 30-minute outage, then redelivery and sweep.

## Work packages
1. Schema and migrations for the four `github_*` tables (needs 04).
2. Webhook ingress: verify, dedupe, persist, acknowledge (needs 1, 02's HTTP surface; fixtures).
3. Token minting adapter against the Identity Service, with a fake endpoint (needs 14).
4. `GitHubAdapter`: REST, ETag, rate state, retries (needs 3).
5. Events poller and event mapper to GC entries and inbox items (needs 1, 01, 06).
6. `PrService`: PR-GC creation, lifecycle, task links (needs 4 and 5).
7. `GitWorkspace`: worktree, commit, host-side push, per-repo lock over `IGitProvider` (needs 18; S23).
8. Owner merge flow: signed approval, squash merge with the chosen credential, `Signed-off-by` (needs 6, 14, 15, 32).
9. Client PR list and diff view in `Bazaar.Client.Core` (needs 6, 28, 29; Monaco only after S12).
10. Live throwaway-repo harness, manual and gated (needs 3 and 4).

Packages 1, 3 and 7 can run in parallel.

## Conflicts
1. **Fork-and-PR premise (remains).** The owner accepted the risk (round 3), but GitHub's documentation says the forks endpoint needs the App on the source repository. S22 decides. 14 and 36 still say issues and forks need no upstream install; they must carry the same caveat.
2. **Token lifetime (remains, cross-doc).** 14 marks "one hour (verified)". The source is a summary, so it is UNVERIFIED until T4. 14 must downgrade the label.
3. **Install scope (remains).** Fork writes need the destination installation with all repositories. 15 wants each token narrowed per lease. Narrowing works per token; the grant itself is the setting `github.install_scope`, default broad.
4. **Bazaar.Server.md §2 (cross-doc, DECIDED text).** It still says "No installation on the upstream repo required" and "not shelling out to the `git` CLI". Both are now conditional (round 3, C12 and C13). The doc must add the caveats.
5. **Bazaar.md §2 (cross-doc).** It says PR GCs "grow organically". The owner brief sets that every PR gets a PR GC; Bazaar.md must record the rule.
6. **Doc 14 credential classes (cross-doc).** 14 needs the owner-class merge credential (dedicated App or linked user token) per C25, selected by `github.merge_credential`.
7. **Community posting (remains).** Bazaar.md §5 wants disclosed agent participation; the research says volume draws maintainer blocks (github_community_presence.md §5–6). Caps are doc 15's keys; the allowlist is `github.community_repo_allowlist`; 36 must match.

## Questions for the owner
ANSWERED: fork path. Keep the fork flow as decided; if GitHub refuses it, work only in repos where the owner or a maintainer installs the App (DECISIONS-LOG round 3, C13).
ANSWERED: git engine. `IGitProvider`; LibGit2Sharp unless S23 fails (round 3, C12).
ANSWERED: push location. Host-side push; tokens never enter a VM (round 2, C05).
ANSWERED: ingress. No inbound by default; polling; tunnel optional (round 8, C30).
ANSWERED: merge path. The owner approves in the client; the server squash-merges; the credential is chosen at setup (rounds 8 and 12, C25).

1. **Agent merge rights.** No agent App gets merge rights; the owner's approved merge is the only merge path? Default: yes. Why: merge is Tier 2 in 15. This is a security invariant, so a setting may not loosen it and it stays a question.
2. **Inbox GC.** One per install, named "Inbox", read by the owner, receiving external and non-member events? Default: yes. It is a registry row in 01 (C19). Naming and design intent, so it stays a question.

MOVED TO SETTINGS: github.merge_credential, github.install_scope, github.poll_interval_minutes, github.writes_per_hour, github.reviewer_family_rule, github.pr_gc_archive_days, github.signoff_event, github.uninstalled_repo_issues, github.community_repo_allowlist (DECISIONS-LOG round 13)
