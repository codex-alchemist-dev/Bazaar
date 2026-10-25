# GitHub Integration

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 2
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 18-vault.md · Used by: 06-agent-runtime.md, 11-reputation-and-peer-review.md, 12-kanban-and-dependencies.md, 17-coding-agent-harness.md, 19-constellation-and-git-solar-system.md, 28-windows-client.md, 29-android-client.md, 36-community-and-external-platforms.md

## Purpose
Lets agents work on GitHub as disclosed bots: one App per agent, webhooks or polling in, branches, PRs and issues out. Every PR is mirrored into a PR GC, so GitHub records Bazaar's work without becoming a coordination system.

## Decided inputs
- Machine accounts are barred by GitHub's terms. Agents use one GitHub App each, owned by org `codex-alchemist-dev`. Agents never create accounts; a human does (CLAUDE.md; Bazaar.Server.md §2; 14).
- Secrets: the Identity Service holds GitHub App keys and tokens are minted there. One server-side `ISecretStore` (owned by 14). Git pushes run host-side, so tokens never enter a VM (DECISIONS-LOG round 2, C05).
- Taint: external content taints hard; agent text taints softly and only from a tainted source session (round 2, C06). Reader sessions read external input and hand over summaries; actor sessions never read it directly (round 6, C18; Claude's reading, to confirm). Approval gates outward high-impact actions: posting, push, merge (C18, C06).
- Approval tiers use 15's ladder, with 36's stricter community defaults (round 7, C21).
- Git engine: one `IGitProvider`. LibGit2Sharp stays decided unless spike S23 shows it cannot do worktrees, merges or concurrency; the git CLI is an alternative implementation (round 3, C12).
- Fork-and-PR flow is built as decided; the owner accepts the risk that GitHub's docs contradict it (round 3, C13).
- Merge: the owner approves in Bazaar.Windows or Bazaar.Android, which replace GitHub's PR UI. The server performs the squash merge on the owner's signed approval. The credential is a dedicated owner GitHub App with merge rights or the owner's linked GitHub credential, chosen in setup and changeable in settings (rounds 8 and 12, C25).
- Done for a card tied to a PR means the PR is merged; reviewer approval is a flag (round 8, C28).
- Home host: no inbound connections by default. It polls GitHub; a tunnel or overlay is optional for real-time webhooks (round 8, C30).
- Every PR gets a PR GC (owner brief, 2026-10-25). GC types come from the registry in 01, and this doc registers `pr` and `inbox` (round 6, C19).
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
| Issue or comment from a non-member | `inbox` GC (question 2); provenance `external`, hard taint | Never; digest only |

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

**Client review and merge [PROPOSED].** Bazaar.Windows and Bazaar.Android show server-proxied PR lists, file hunks keyed by head SHA, checks and the in-app review. The diff view uses Monaco's diff-editor mode only after the Monaco hosting gate (S12, round 5, C26). Android has no Monaco, so it needs a native diff surface (29). Clients never call GitHub.

## Data and interfaces

```sql
github_app(agent_id pk, app_id bigint, installation_id bigint, account_login text,
           status text,                      -- active|suspended|revoked
           key_ref text)                     -- reference only, never the key
github_pr(pr_node_id text pk, repo_node_id text, number int, gc_id uuid unique,
          task_id uuid null, head_sha char(40), base_ref text, state text, draft bool,
          updated_at timestamptz)
github_delivery(source text, delivery_id text, event text, received_at timestamptz,
                processed_at timestamptz null, primary key (source, delivery_id))
github_rate_state(installation_id bigint, bucket text, remaining int, reset_at timestamptz)
```

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
