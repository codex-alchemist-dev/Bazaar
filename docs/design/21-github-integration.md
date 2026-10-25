# GitHub Integration

Status: DRAFT for owner review (2026-10-25) · Owner questions: 13
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 18-vault.md · Used by: 06-agent-runtime.md, 11-reputation-and-peer-review.md, 12-kanban-and-dependencies.md, 17-coding-agent-harness.md, 19-constellation-and-git-solar-system.md, 28-windows-client.md, 36-community-and-external-platforms.md

## Purpose
Lets agents work on GitHub as disclosed bots: one App per agent, webhooks in, branches, PRs and issues out. Every PR is mirrored into a PR GC, so GitHub records Bazaar's work without becoming a coordination system.

## Decided inputs
- Machine accounts are barred by GitHub's terms. Agents use one GitHub App each, owned by org `codex-alchemist-dev`. Agents never create accounts; a human does (CLAUDE.md; Bazaar.Server.md §2; 14).
- Webhooks feed the agent's GC and inbox; Apps have no inbox (Bazaar.Server.md §2).
- Diff UI reuses Monaco's diff-editor mode (Bazaar.Server.md §2).
- Every PR gets a PR GC (owner brief, 2026-10-25; see Conflicts 6).
- Commits carry `Co-Authored-By`; the owner signs off at squash-merge, DCO model (owner brief; DCO vs CLA is OPEN in CLAUDE.md).
- Agents never push to the default branch; PRs open as drafts; agent messages are never merge approvals (CLAUDE.md; 17).
- Post, push and merge from a tainted session need owner approval (15).

## Design

**Identity [PROPOSED].** The owner registers one App per agent (`<agent>-bazaar`) in the org, with its own name, avatar and description. Permissions: contents, pull requests and issues read/write; checks and metadata read. Subscribed events: `pull_request`, `pull_request_review`, `pull_request_review_comment`, `issue_comment`, `issues`, `check_run`, `check_suite`, `push`. The private key lives in the Identity Service (14), never in a VM or a repo. No agent App is granted merge rights (Q6).

**Tokens [PROPOSED].** The Identity Service signs a short-lived App JWT, exchanges it for an installation token narrowed to the repositories and permissions of one lease, and gives the token only to the host-side GitHub adapter. Tokens stay in memory, are never logged, and never enter a VM. Lifetime: one hour per the installation-auth page (summary only; T4).

**Components [PROPOSED].** `GitHubAdapter` (REST and GraphQL, ETags, rate state, retries), `GitHubWebhookIngress`, `PrService` (PR-to-GC mapping, lifecycle, task links) and `GitWorkspace` (host-side worktree, commit, push).

**Fork-and-PR flow [PROPOSED, UNVERIFIED].** Preferred path: fork upstream into the org, push `agent/<task>` to the fork, open a draft PR against upstream. The fork endpoint requires the App on the source account with access to the source repository, which contradicts the decided "no upstream installation" premise (Conflicts 2). Live tests choose the path:

| Test result | Behaviour |
|---|---|
| Fork and PR work without an upstream install (T2, T3) | Default flow for any public repo. |
| Fork or PR needs an upstream install | Only repos where the owner or a maintainer installs the App. Elsewhere the agent drafts, and the owner posts (Tier 2). |
| Issue on an uninstalled repo refused (T1) | No writes to uninstalled repos at all. |

**PR lifecycle [PROPOSED].**
1. Author agent pushes `agent/<task>` and opens a draft PR. The server creates the PR GC once, keyed by PR node id (type `pr`, parent = project GC).
2. Task moves to Review with a `pr` link (12). A reviewer agent on a different model family (Q9) is requested and posts its review under its own App.
3. Checks run on GitHub-hosted runners (CI doc). A failed check wakes the author.
4. The author marks the PR ready when checks pass. Fixes are new commits.
5. The owner approves in the client and squash-merges with the owner's own GitHub credential. The squash message carries the owner's `Signed-off-by`.
6. A `pr_event` records the merge, 11 records the outcome, and the PR GC is archived, never deleted.

Reviewer approval is data for the owner, not merge authority (17).

**Event mapping [PROPOSED].**

| Webhook (after verification) | Target | Wakes |
|---|---|---|
| `review_requested` for an agent | PR GC; assignee inbox, kind `assignment` | Yes |
| Review submitted, changes requested | PR GC; author inbox | Yes (author) |
| Comment that @-mentions an agent | PR or issue GC; inbox, kind `mention` | Yes |
| `check_run` or `check_suite` failed | PR GC; author inbox | Yes (author) |
| `push` to an agent branch | PR GC `pr_event` | No |
| `pull_request` closed or merged | PR GC; owner notice; 11 outcome | Owner only |
| Issue or comment from a non-member | Inbox GC [name PROPOSED], `external_untrusted` | Never; digest only |

**Git operations [PROPOSED].** Worktrees sit on the host under the agent's mount. Mutating operations (worktree add, commit, push, merge) run through the git CLI behind a per-repo lock. Read-only work (diff, log, commit graph for 19) may use LibGit2Sharp. This is pending a spike (Conflicts 1). Pushes run host-side with a one-shot credential, so the token never reaches the VM. Commit author: the App's bot identity. Committer: `GitWorkspace`. Trailers: `Co-Authored-By` (model), `Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`. Agents never add `Signed-off-by`.

**Branch protection [PROPOSED].** On owner-controlled repos: the default branch requires a PR, one human approval and passing checks; no force-push or direct push. Agent Apps are on no bypass list. `agent/*` branches accept pushes; force-push only with lease.

**Rate limits [PROPOSED].**

| Limit | Source | Bazaar default |
|---|---|---|
| 5,000 requests/hour per installation | Secondary summary | Warn at 70%; defer reads at 90% |
| 80 content-creating requests/min; 500/hour | Secondary summary | 30/min per agent; 300/hour per installation (Q11) |
| 900 REST / 2,000 GraphQL points/min | Secondary summary | ETag reads; no polling while webhooks are healthy |

On 403 or 429, honour `retry-after` and reset headers; repeated breaches freeze the agent's GitHub writes and notify the owner (15).

**Webhook delivery [PROPOSED].** One `POST /hooks/github/{opaque}` route, reached through an outbound-only tunnel (Q10). Before any parsing: HMAC-SHA256 over the raw body, compared with `X-Hub-Signature-256` in constant time; reject other methods, bad signatures and oversize bodies. Dedupe on `X-GitHub-Delivery` as `dedupe_key = github:<delivery>` (11). Persist, return 2xx, then process from the table. Events missed during an outage are recovered by an Events-API sweep every 10 minutes.

**Community presence [PROPOSED].** Every post carries the bot label and names the human sponsor. Agents file only where a maintainer asked or agreed (36). Each new public issue or PR is Tier 2. Caps are in Q11 and Q12.

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

```csharp
public interface IGitHubAdapter {
    Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest r, CancellationToken ct); // search by head before create
    Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken ct);
    Task PostReviewAsync(PrRef pr, ReviewRequest r, CancellationToken ct);
}
public interface IGitWorkspace {
    Task PushAsync(WorkspaceRef ws, string branch, CancellationToken ct); // host-side; token from Identity Service
}
```

Clients never call GitHub; they read server-proxied PR lists and file hunks keyed by head SHA.

## Behaviour and edge cases
- Out-of-order webhooks: apply by `head_sha` and `updated_at`; an older event is stored, never applied over newer state.
- A PR GC is created once; a replayed delivery creates nothing. Repos are keyed by node id, so renames do not break mappings.
- Suspended or uninstalled App: new leases denied, open PR GCs notified. Writes queue while GitHub is unreachable; each retry first checks whether the write already landed.
- A force-push changes `head_sha`; reviews on the old SHA are marked stale. Stranger comments are `external_untrusted` and taint any reading session (15).
- A minted installation token stays valid up to one hour after revocation (14).

## Testing
- Fake GitHub HTTP endpoint with recorded fixtures: 201, 202 (async fork), 403 and 429 with rate headers.
- Webhook fixtures: valid, bad signature, duplicate delivery, out of order, oversize.
- Real Postgres: dedupe and PR-GC uniqueness under concurrent deliveries.
- Git: temporary repos and a local bare remote for worktree, push and lock tests. Avalonia.Headless: PR list and diff view from fixtures.
- Live tests (manual, against a human-created throwaway org, never in CI): T1 issue on an uninstalled repo; T2 fork into the org; T3 draft PR from the fork, with and without an upstream install; T4 token expiry, used at 55 and 61 minutes; T5 content-write burst, recording limit headers; T6 bot commit identity; T7 30-minute webhook outage, then redelivery and sweep.

## Work packages
1. Schema and migrations for the four `github_*` tables (needs 04).
2. Webhook ingress: verify, dedupe, persist, acknowledge (needs 1; fixtures).
3. Token minting adapter against the Identity Service, with a fake endpoint (needs 14).
4. `GitHubAdapter`: REST, ETag, rate state, retries (needs 3).
5. Event mapper to GC entries and inbox items (needs 2 and 01).
6. `PrService`: PR-GC creation, lifecycle, task links (needs 4 and 5).
7. `GitWorkspace`: worktree, commit, host-side push, per-repo lock (needs 18).
8. Client PR list and Monaco diff view in `Bazaar.Client.Core` (needs 6 and 28).
9. Live throwaway-repo harness, manual and gated (needs 3 and 4).

Packages 1, 3 and 7 can run in parallel.

## Conflicts
1. **Git engine.** Bazaar.Server.md §2 (DECIDED) says LibGit2Sharp. 17 and 18 use the git CLI for worktrees and merges. The research could not verify libgit2 worktree or merge coverage (github_community_presence.md §4). Blocks package 7.
2. **Fork-and-PR premise.** Bazaar.Server.md §2 (DECIDED) says no upstream installation is needed. The forks endpoint requires the App on the source account with access to the source repository, and tokens cannot reach repositories outside the grant. The owner must re-decide or accept the T2 and T3 result.
3. **Token lifetime.** 14 marks "one hour (verified)". The research took it from a summarizer, and the threat model lists it as unverified. Treated as unverified until T4.
4. **Install scope.** Fork writes need the destination installation with all repositories. 15 wants each token narrowed to one repo per lease. Narrowing works per token, but the grant itself stays broad.
5. **Push location.** 17 has the VM push with a brokered token; 14 says no secret leaves the host. This doc moves pushes host-side. 17 needs amending.
6. **PR GC rule.** The owner brief says every PR gets a PR GC. Bazaar.md §2 says PR GCs "grow organically". Record the rule.
7. **DCO vs CLA.** Still OPEN (CLAUDE.md; Bazaar.Server.md §8). This doc assumes owner sign-off without choosing the model.
8. **Community posting.** Bazaar.md §5 wants disclosed agent participation; the research says volume draws maintainer blocks (github_community_presence.md §5–6).

## Questions for the owner
1. **Fork path.** If T2 and T3 show an upstream install is needed, operate only where the owner or a maintainer installs the App; elsewhere the owner posts drafts? Default: yes. Why: the only documented path.
2. **Issues on uninstalled repos.** If T1 succeeds, allow with per-issue Tier 2 approval? Default: yes; if refused, no writes there. Why: volume draws bans.
3. **Install scope.** One org install with all repositories (needed for forks), tokens narrowed per lease? Default: yes. Alternative: per-repo installs, more setup.
4. **Git engine.** Git CLI for mutations, LibGit2Sharp for read-only work, and amend Bazaar.Server.md §2? Default: yes. Why: libgit2 coverage is unverified.
5. **Push location.** Host-side push, token never in a VM? Default: yes (resolves 14 vs 17).
6. **Merge authority.** No agent App gets merge rights; the owner merges with their own credential? Default: yes (merge is Tier 2 in 15).
7. **Sign-off record.** Owner `Signed-off-by` at squash-merge, logged as a platform event? Default: yes; DCO vs CLA stays open.
8. **PR GC lifecycle.** Created at draft open, kept after merge, archived 7 days after close, never deleted? Default: yes.
9. **Reviewer model.** Must the reviewer use a different model family from the author? Default: yes (17 says "another model").
10. **Webhook ingress.** Outbound tunnel (operator choice, see 32) plus a 10-minute Events sweep? Default: yes; polling-only is the fallback for hosts with no tunnel.
11. **Write caps.** 30 content writes/min per agent, 300/hour per installation, 2 new public issues and 2 new PRs per agent per day? Default: yes.
12. **Community allowlist.** Public repos outside owner-controlled orgs stay off until the owner adds each one? Default: yes, until 36 exists.
13. **Triage GC.** Name and existence of the GC receiving `external_untrusted` events? Default: one per install, named "Inbox", read by the owner.
