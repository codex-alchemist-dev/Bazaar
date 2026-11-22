# Coding-Agent Harness

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: 01-gc-data-model.md, 04-postgres-schema-and-migrations.md, 12-kanban-and-dependencies.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 18-vault.md, 21-github-integration.md · Used by: 06-agent-runtime.md, 11-reputation-and-peer-review.md, 28-windows-client.md, 34-testing-strategy.md, 35-spikes-plan.md

## Purpose
The tools and mechanics a coworker agent uses to read, edit, build, test and commit code inside its VM pair. The server checks results rather than trusting agent self-reports, so other agents and the owner can review them.

## Decided inputs
- Each agent has a Windows VM and a Linux VM, headless by default (Bazaar.Soul §2; C03, round 1).
- Soul data stays off the VM disk. The Passport mount is a per-agent folder holding metadata and a session-cookie profile, detached on hard taint (C02, round 2). No clipboard, no drive passthrough.
- Tokens never enter a VM. Pushes and PR calls run host-side; keys sit in the server's `ISecretStore` (C05, round 2; doc 14).
- Git sits behind `IGitProvider`. LibGit2Sharp stays the engine unless spike S23 fails; the git CLI is an alternative implementation (C12, round 3).
- Taint: external content taints hard; agent text taints softly and propagates only from a tainted source session; Tier 2 actions are gated on hard taint (C06, round 2).
- Reader/actor split: reader sessions read external input and hand over summaries. Routine internal work needs no human approval; approval gates outward high-impact actions (posting, sending, spending, repo push or merge) (C18, round 6; Claude's reading, to confirm).
- Approval tiers are doc 15's ladder (C21, round 7). The model seam is `ModelEvent` with an `IChatClient` adapter (C29, round 8).
- The server verifies. Clean builds and tests run server-side; agent self-reports never count (CLAUDE.md; C37, round 10).
- PRs are reviewed and merged in the Bazaar clients. A card tied to a PR is Done when the PR is merged (C25, C28, round 8).
- Claude lane: Anthropic API key and FreeLLMAPI, by availability; subscription logins on no route (C14, round 4). Model whitelists are per agent (doc 05).
- Notes and plans are Vault documents (C38, round 11). Agent terminals run in the VM and are streamed by the server (C31, round 4).

## Design

### Tool set [PROPOSED]
| Tool | Function | CapabilityClass | Runs in |
|---|---|---|---|
| read, list, search | files, ripgrep, repo map | Read | Linux VM worktree |
| edit | whole-file, line-anchored or search/replace | Read (in worktree) | Linux VM |
| shell | argv command, cwd jailed, timeout; no secrets, egress allowlisted | Exec | Linux VM |
| git (local) | status, diff, branch, commit, worktree | Read / Exec | Through `IGitProvider` |
| push, draft PR | push `agent/*`, open draft PR | Post | Host-side, via doc 21 |
| browser | Chromium in the agent's VM, two profiles: anonymous (reader side, no credentials) and account (Passport, allowlisted sites) | Post for account profile | Agent VM profile (C22) |
| MCP | Bazaar tools (GC, Kanban, memory); external servers only from Bazaar config | Per tool | Host |
| run_checks | server-owned verification; agent requests, never edits | Read | Server dispatches to VM |

Each tool declares a `CapabilityClass` (renamed from `ToolTier`; the approval ladder itself is C21). Doc 15's policy maps a class to an approval tier. The server enforces it in tool dispatch, never in the prompt.

### Edit formats and benchmark [PROPOSED]
- Evidence is 2024–25 Aider data: whole-file had the highest format compliance for weak models; diff formats gave better pass rates and fewer tokens for strong ones. There is no data for Haiku 5.5 or DeepSeek V4 (research note, verified).
- Default until measured: whole-file for models without data. Each model's format goes in its agent whitelist, chosen by the benchmark (Testing).
- Metrics: format compliance, malformed-edit rate, pass rate, tokens per task.

### Context engine [PROPOSED]
- Per-request package, in order: current file and diff; the GC thread that asked; the task's dependency state; a repo map under a token budget (Aider's default is 1k). All else is fetched by tool. Compaction clears old tool results first, then summarises.
- Plans and notes are Vault documents (C38). The Kanban task holds state and blocked-by edges and changes only through the task service (C28, doc 12). Private scratch notes sit in the agent's private Vault area (doc 18). A todo list grants no authority.
- Code intelligence: Roslyn for exact C# symbols, the likely first choice (not researched). Tree-sitter for Luau and GDScript (TreeSitter.DotNet 1.3.0 is secondary; platform support conflicts). No embeddings until grep plus repo map is shown to be worse.

### Verification (server-side) [DECIDED: CLAUDE.md]
For each PR, the server runs in a fresh clone of the commit:
1. Clean build with no cached outputs; exit code and log recorded.
2. Test count must not drop against the base branch.
3. Test-file changes are flagged and need a named reviewer.
4. Fake-success scan: new Skip or Ignore attributes, `#if false`, removed tests or assertions.
5. UI changes need an Avalonia.Headless test; a screenshot is not evidence.
6. Luau: `luau-analyze` and Selene. Godot: `--check-only` and `--script`, with the binary build checked first.

Results go to `check_runs`. Agent reports are data. Every tool call is traced with its exit code, so a run can be replayed.

### Git workflow [PROPOSED]
- Engine: `IGitProvider` (C12). Worktrees, merges and concurrency are tested by S23, including where the engine runs for a VM worktree.
- Branch `agent/<task>`; one worktree per writer.
- Worktrees live on the agent's separate work share, never on the Passport folder, so a shell that cannot read secrets cannot read cookies. The separate share is required by C02 (round 2); doc 16 carries it as the second inter-VM share.
- Commit author is the agent's GitHub App identity. Trailers: `Co-Authored-By` for the model, plus GC and task IDs.
- Push and draft PR run host-side through doc 21. No token enters a VM (C05).
- Agents never push to the default branch and never add `Signed-off-by`.
- Merge: the owner approves in a Bazaar client; the server squash-merges and writes the platform's `Signed-off-by` line (C25). Credential choice follows doc 21. PRs open as drafts.

### Review flow and subagents [PROPOSED]
- The author opens a draft PR. A reviewer on another model family, where one exists, gets read-only tools, no shell, and the diff plus decision context (task card, GC thread, check results). The review posts to the PR's GC. Reviewer approval sets a flag on the card and is not merge authority (C28). Outcomes feed doc 11.
- Subagents: fresh context, tool allowlist, model from the parent's whitelist (doc 05). Depth 1 by default, inside doc 06's runtime delegation limit (depth 2, 3 concurrent). Writers never share a worktree; parallel work only over disjoint files in separate worktrees (`harness.subagent_disjoint_writers`). The Haiku 5.5 rule in CLAUDE.md governs build agents, not runtime coworkers.

### Permissions [PROPOSED, on DECIDED C06 and C18]
- Routine internal work (edit, shell, commit, check runs) needs no human approval.
- Hard taint comes from external content: strangers' issues, web pages, email, third-party docs. Coding actors do not read these directly; a reader session reads them and hands over a summary (doc 15).
- Classification: a session that reads stranger repository files (repos the owner does not control, or third-party code read as input) is a reader session and hard-tainted. Its output reaches actors only through a hand-over. The owner's own repo files and command output are Question 3.
- A hard-tainted session cannot run actions that doc 15 gates at Tier 2 without a per-action approval recorded in doc 15's `approval`. A fresh session with an approved summary is the alternative (C06).
- Pushes to `agent/*`: no approval while untainted; per-push approval while hard-tainted (Question 1).
- Approvals are per action or time-boxed with visible scope. No blanket allow (threat model §5).

### VM execution (guest agent) [PROPOSED]
- `IWorkspace` is a facade over doc 16's `IGuestChannel`, the only guest wire. `IVmBackend` is doc 16's (C22).
- Guest agent: start (argv, cwd, env allowlist, timeout), streamed output with sequence numbers, cancel, read and write jailed to the worktree. Transport and cgroup or job-object limits follow doc 16.
- Per-call command timeout defaults to doc 16's exec default (15 min); doc 16's exec maximum (2 h) caps it. The task time limit (`harness.task_time_limit`, default 2 h) is this doc's setting and cannot exceed that maximum. A timeout kills the process tree.

### UI and engine verification
Avalonia.Headless.XUnit with `[AvaloniaFact]` and Skia frame capture for pixel checks. Headless cannot cover windowing, IME, DPI, fold posture or accessibility; those need owner-side testing. Godot: headless import, `--check-only`, `--script`. Roblox Studio has no agent path.

## Data and interfaces (sketches)
```csharp
public enum CapabilityClass { Read, Exec, Post, Spend, CreateAccount, GrantScope, Merge, Deploy }

public interface IGitProvider
{
    Task<WorktreeHandle> AddWorktreeAsync(RepoRef repo, BranchName branch, CancellationToken ct);
    Task<CommitId> CommitAsync(WorktreeHandle wt, CommitRequest req, CancellationToken ct);
}

public interface IWorkspace : IAsyncDisposable
{
    Task<ProcessHandle> StartAsync(ProcessSpec spec, CancellationToken ct);
    IAsyncEnumerable<OutputChunk> StreamAsync(ProcessHandle h, long fromSeq, CancellationToken ct);
}
```
Tables owned here: `tool_calls` (append-only, host-written: session, tool, CapabilityClass, source_trust per doc 15's enum, tainted_at_write, exit code, args hash) and `check_runs` (commit, base, build exit, test counts, flags). Approvals use doc 15's `approval`; security events go to doc 15's `audit_log` (C16). This doc adds no approval table.

`check_runs` additions (PROPOSED, 2026-11-18, for engine checks such as Luau, doc 20): `verdict` (server-computed `pass`, `fail` or `error`; `error` means the run could not finish and is never a pass; null on older rows) and `tool_exit_codes` (a JSON object from a short tool name such as `build`, `test`, `luau-analyze`, `selene` to its exit code, null when the tool did not run). Both are written by the server from measured results; an agent report never sets them. Migration `coding/0003`. The table stays insert-only.

### Settings [PROPOSED]
Registered in doc 03's settings registry (DECISIONS-LOG round 13). Defaults are safe; every change is audit-logged. Settings may tighten the taint, verification and quota floors, never loosen them.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `harness.command_timeout` | duration | 15 min | install | operator; max 2 h (doc 16) | no |
| `harness.task_time_limit` | duration | 2 h | project | operator; max 2 h (doc 16) | no |
| `harness.edit_format` | enum: whole-file, line-anchored, search-replace | whole-file | agent | operator, via the agent's whitelist (doc 05) | no; logged |
| `harness.benchmark_max_runs` | int | 360 per model per run (40 tasks × 3 formats × 3 seeds) | install | operator | yes, before the first run; a run quota, not a money budget (Resource Economy is REJECTED) |
| `harness.coding_vm_usage` | enum: linux-only, linux-plus-windows-checks | linux-plus-windows-checks | install | operator | no |
| `harness.subagent_depth` | int | 1 | project | operator; max is doc 06's runtime limit | no |
| `harness.subagent_disjoint_writers` | bool | off (one writer per worktree) | project | operator | no |
| `harness.test_count_drop_tolerance` | int | 0, no per-PR override | install | operator | yes, for any value above 0 |
| `harness.test_file_review_flag` | bool | on (test-file edits need a named reviewer) | install | operator | yes, to turn off |
| `harness.code_intel.tree_sitter` | bool | off until Luau or GDScript work starts | project | operator | no |

Roslyn for C# code intelligence is the default and needs no setting.

## Module layout [PROPOSED]
The harness is built as module pairs (`Bazaar.Server.<Name>` plus `.Abstractions`), none of which reference `Bazaar.Server.csproj`. Where a module needs something Server owns (the capability policy, taint state, approvals, `IGitProvider`, the doc 21 push path), it declares a small port in its own Abstractions project; a host adapter (a later PR) binds the port. Each module ships an in-memory fake of its ports and tests against the fakes. Module version, manifest and SQL: none of these modules owns tables yet (`tool_calls` and `check_runs` stay WP2).

| Module pair | Work package | Holds | Depends on |
|---|---|---|---|
| `Bazaar.Server.CodingPermissions` | WP8 | `ToolCapabilityClass` (mirror of Server's `CapabilityClass`, kept equal by a test), the permission gate (class-to-tier lookup, taint, approvals, rate caps), the `tool_calls` trace port | none (Contracts only) |
| `Bazaar.Server.CodingEdit` | WP6 | `IWorktreeFiles` port, path jail (rejects `..`, absolute paths, escaping symlinks), whole-file, line-anchored and search/replace edit tools, malformed-edit reporting | `Bazaar.Server.Coding` (`GuestPath`, `IGuestChannel`), CodingPermissions.Abstractions |
| `Bazaar.Server.RepoMap` | WP7 | search tool (ripgrep through `IWorkspace`), repo map under a token budget, symbol extractor port (Roslyn for C#; tree-sitter stays off, setting `harness.code_intel.tree_sitter`) | `Bazaar.Server.Coding`, CodingPermissions.Abstractions |
| `Bazaar.Server.CodingGit` | WP10 | worktree and push service: branch rule `agent/<task>`, one writer per worktree by host lock, commit trailers (`Co-Authored-By`, GC and task ids, never `Signed-off-by`), work-share placement rule, host-side push and draft PR through a doc 21 port | CodingPermissions.Abstractions |
| `Bazaar.Server.CodingReview` | WP11 | review context package (diff, task card, GC thread, check results), reviewer tool set (read-only, no shell), reviewer model-family choice, review-result port (flag on card, never merge authority) | `Bazaar.Server.Coding` (check types), CodingPermissions.Abstractions |
| `Bazaar.Server.Subagents` | WP12 | subagent spawner: fresh context, tool allowlist that is a subset of the parent's, model from the parent's whitelist, depth and concurrency limits, disjoint-writer rule | CodingPermissions.Abstractions |
| `Bazaar.Server.CodingRunner` | WP15 (PROPOSED, 2026-11-20) | the coding tool runner: a thin dispatch layer between the Agents turn loop's tool call and the guarded tool. Looks the tool up in a closed registry of `ICodingToolHandler`s, builds the `ToolCallContext`, calls `IToolCallRecorder.BeginAsync` (the gate), runs the handler only when the scope may run, serialises writers per worktree through `IWorktreeLocks`, and always completes the trace row with an `ExecOutcome`. Unknown tool, denied gate, approval needed, busy worktree and handler failure return a structured error result to the model and never throw. Ships an edit handler over `EditToolRouter` and a delegate handler for git, review, search and subagent adapters | CodingPermissions.Abstractions, CodingGit.Abstractions (`IWorktreeLocks`), CodingEdit |

Gap decisions (PROPOSED, most robust option, owner may overrule): (1) push to `agent/*` is gated only by hard taint, the recommendation of Question 1, behind the setting `harness.push_always_approve` (default off, may only tighten); (2) repo-level instruction files and repo MCP servers are never loaded, with no setting, per Question 2; (3) the owner's own repo files and command output count as soft taint, per the recommendation of Question 3, and the gate treats any source marked external as hard. These are enforced in code but stay PROPOSED until the owner answers the three questions.

Host-side sources (PROPOSED, 2026-11-18): the harness binds these ports in `Bazaar.Server.Host/Coding/` once the owning module exposes them. `IWorkShareLayout` and `ISubagentWorktreeAllocator` come from the VM mounts module (doc 16). Agent taint comes from `IAgentStateReader` (Identity); session id and role are composed from Agents and Security in an `IPushSessionDirectory` that this doc owns. `ICodingRepoDirectory` comes from the Git/GitHub module. `IAgentGitIdentity` is PROPOSED (coordinator, 2026-11-18): docs 14 and 21 do not say how a commit author is derived from the GitHub App, so the default is author name = the agent's display name, author email = the App's noreply address, and the agent id in the `Bazaar-Agent` trailer. The port stays unregistered (commits and pushes fail closed) until the Identity side implements it. Running-turn lookup for subagents (`IRuntimeSubagentParentDirectory`) is owned by the Agents runtime (doc 06).

Host sources for the identity and state ports (PROPOSED, 2026-11-20). All three are thin adapters in `Bazaar.Server.Host/Coding/` because the types they read (`IAgentStateReader`, `ISessionTaintStore`, `IQuarantineStore`) live in Server and no module may reference it. They fail closed.
- `IAgentGitIdentity`: author name = the agent's display name (from `IAgentDisplayNames`; an agent with no name is an error, never a default), author email = the operator-set GitHub App noreply address `Coding:Harness:Git:AppNoreplyEmail` (required; no domain is built in; a missing or malformed value stops the host start naming the key). Control characters and angle brackets are rejected so a name cannot forge a header. The agent id goes in the `Bazaar-Agent` trailer, which the engine already writes.
- `IAgentStateReader`: quarantined when the agent has an active `Agent` quarantine record or is frozen (`IAgentFreeze`); a store that fails or takes longer than `Coding:Harness:StateReadTimeoutSeconds` (default 2) counts as quarantined. Taint is the highest taint among the agent's running turns' sessions (read through `ISessionTaintStore`); an agent with no running turn is Clean (sessions are per task, doc 15); an unreadable session or an agent id that is not a GUID is Hard. The running turns come from a new read-only `IRunningTurnIndex` next to the running-turn registry (doc 06).
- `IPushSessionRecords`: the session is the running turn of the pushing agent whose trigger GC equals the push's GC (a top-level turn before a subagent turn, one holding the `git.push` tool before one that does not). Role is Actor only when that turn's allowlist holds `git.push`, otherwise Reader, which the gate denies. Source trust is the least trusted label among the session's taint sources (`quarantined` > `external` > `agent` > `member-human` > `system` > `owner`), `agent` when there are none, `external` when the taint cannot be read. No turn in that GC means no record, so no push. The approval grant comes from `IPushApprovalGrants` (below); with no approval store bound it is absent and a hard-tainted push stays at "needs approval".
- **Approval grant lookup (PROPOSED, 2026-11-22, most robust option, owner may overrule).** `IPushApprovalGrants` (Host, `Coding/PushApprovalGrants.cs`) finds the owner approval for a push in Server's `IApprovalStore`, through a small candidate source `IApprovedPushApprovalIds` that lists the ids of approved, unexpired rows for the agent, session, tool and hash (the shipped one is one read-only SQL query over the `approval` table through the host's `NpgsqlDataSource`; Server's `IApprovalStore` has no query method and is not changed), then loading each row with `IApprovalStore.GetAsync`. The hash is the doc 15 dispatch hash of exactly what the gate evaluates: tool `git.push`, target = the branch, args `{repo, branch, gc, task}`, no taint sources, so an approval can never cover another agent, session, repo, branch, GC or task. A row counts only when ALL hold, and any doubt is no grant: status `Approved` and not consumed; `expires_at` after now; agent, session, tool, hash equal (re-checked in code, not only in SQL); it carries the owner's decision (`DecidedBy`, `DeviceId`, `DecidedAt`, 64-byte `Signature`) and that signature verifies against the owner's device key, which must be registered and not revoked (an approval row an agent wrote itself has no valid owner signature, so it never counts). Several qualifying rows (the owner approved the same call twice) give the earliest-expiring one. A store or key-registry failure, a timeout (`Coding:Harness:StateReadTimeoutSeconds`), a malformed row or a non-GUID agent id gives no grant. Consumption is single-use and atomic with the push decision: `PermissionGatePushGate` consumes the grant (`Approved` to `Consumed`, one compare-and-set) at the moment it would return Allow for a session that carries a grant; only the caller whose compare-and-set wins is allowed, so two concurrent pushes on one grant cannot both pass, and a lost race, a consume error or an expired grant denies. The push runs after the Allow; if it then fails, the approval stays consumed (fail safe, the owner approves again). Consume-on-allow is chosen over two-phase reserve and commit because a crash between the phases would otherwise leave a grant that could be spent twice or never released. Open: the code that files the push approval request (the card the owner signs) must build the same hash; it is not part of this change.
**Workspace sources: `Bazaar.Server.CodingWorkspace` (WP16, PROPOSED, 2026-11-20).** One module pair holds the production sources of three unbound ports, coded against contracts in its own Abstractions project so it does not wait for the Git or VM modules. The owner of each area can replace a source by binding a different implementation of the small port.
- `IAgentWorkShareSource` (port) gives one agent's work share root and Passport folder. The shipped source is operator-configured: `Coding:Workspace:Root` (absolute, no default, no hard-coded path) holds `agents/<agentId>/work` and `agents/<agentId>/passport`, which are siblings, so a worktree is never on or around the Passport folder. A paths-resolved-by-link check (`WorkSharePaths.Canonicalize`) keeps every result under the root. The VM mounts module (doc 16, WP8) replaces it when its share layout exists. `WorkShareLayout` adapts the port to `IWorkShareLayout`; an agent id outside `[A-Za-z0-9_][A-Za-z0-9._-]{0,99}`, one ending in a dot, or a Windows device name is refused.
- `ICodingRepoSource` (port) mirrors the Git module's repository directory (doc 52, `IGitRepositoryDirectory`: id, default branch, host-side clone path, installation and provider ids). The shipped source reads the operator's list `Coding:Workspace:Repos`. Each entry is checked at start (id pattern, absolute clone path, no duplicate id, clone path not under the work-share root, numeric installation and repository ids). An unknown or no-longer-valid clone gives null, which refuses the call. `Bazaar.Server.Host` adapts it to `ICodingRepoDirectory` (`CodingRepoDirectoryAdapter`); when the Git module merges, a second adapter over its directory replaces this source.
- `ISubagentParentWorkSource` (port) resolves a parent session id to its agent, repository, task and worktree path. No source exists yet (the running-turn registry carries the session, not the repository), so the host must bind it; until then the allocator refuses (fails closed). `GitSubagentWorktreeAllocator` implements `ISubagentWorktreeAllocator` over `CodingGitService`: a writer gets its own worktree on branch `agent/<parentTaskSlug>-<subagentSlug>` under the parent's work share, with the two host locks the service takes (path and branch), so two writers never share one; a read-only lease is the parent's own worktree path, marked read-only, with no new git state. A failed allocation leaves no lease; a double or unknown release is a no-op. Release frees the locks and keeps the directory and branch (the child's commits may be needed for review); pruning is a later job.
- **Git-directory repo source (PROPOSED, 2026-11-22, most robust option, owner may overrule).** `Bazaar.Server.Host/Coding/GitDirectoryCodingRepoSource` implements `ICodingRepoSource` over the Git module's `IGitRepositoryDirectory` (doc 52). The host binds it instead of `ConfiguredCodingRepoSource` whenever `IGitRepositoryDirectory` is resolvable, so `Coding:Workspace:Repos` is only the fallback for a host with no Git directory (no merging of the two lists: one source of truth). It fails closed: null for an unsafe id, an unknown id, a provider other than `github`, a full name that is not `owner/name`, a missing or non-positive installation id or provider repository id (the coding port needs numeric GitHub ids), a clone path that is not absolute, that is gone, or that lies on or around the work-share root, and for a directory that throws (not cancellation). The directory carries no default branch, so it is the operator setting `Coding:Workspace:DefaultBranch` (default `main`, checked like a configured entry).
- **Host sources for worktree files and tool context (PROPOSED, 2026-11-22, owner may overrule).** `Bazaar.Server.Host/Coding/` binds the two ports the read, list and git handlers need (decisions 2 and 7 below); both fail closed.
  - `IWorktreeFilesSource` (`WorktreeFilesSource`): returns a read-only, path-confined store over the worktree directory, or null when the worktree is no longer active, its path is not under the agent's `IWorkShareLayout` worktree root (links resolved), it is on or around the Passport folder, or the directory is gone. The store refuses absolute paths (rooted, drive letters, a leading slash or backslash), `..` and `.` segments, empty segments, backslashes, colons (alternate data streams), control characters, segments with a trailing dot or space, Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1` to `COM9`, `LPT1` to `LPT9`, with or without an extension, on every OS so a repo behaves the same everywhere) and any `.git` segment (the worktree's `.git` file names a host path). Every component is resolved by hand; a symlink whose target leaves the real worktree root makes the path unresolvable (null or a refusal), and listing never follows directory links. Reads open the resolved real path and load at most the read tool's cap plus one byte, so an oversized file is reported as too large without being loaded; a listing stops at 100000 files. Write and delete are not part of this source: they throw, because edits go through the guest channel (doc 16). A link swapped in between the check and the open is a residual race, accepted for a read-only source and closed by the guest-channel store when it exists.
  - `ICodingToolHostContext` (`RunningTurnCodingToolHostContext`): the calling agent and session come from the call and must match a running turn in `IRunningTurnIndex`; no running turn, a non-GUID id, or more than one matching turn (a parent and its subagent share a session, and the call does not say which one is calling) means no context. `FindWorktree` reads a host `ICodingWorktreeDirectory` (an in-memory registry that whoever opens a task worktree registers into) and returns only active worktrees. `ParentFor` builds the parent limits from the turn (session id as parent and chain id, the turn's allowlist and depth) and the model whitelist from `ICodingAgentModels`. The commit model and the agent's model id come from `ICodingAgentModels`, whose shipped source reads `Coding:Harness:Models:<agentId>` (`ModelId`, `Name`, `Email`, `Whitelist`); a missing entry throws, so the commit or review fails instead of crediting a default. No credit email is built in. Open: a gateway-backed `ICodingAgentModels` should replace the configured one.
- **Default repo and per-task worktree flow (PROPOSED, 2026-11-22, owner may overrule).**
  - `Coding:Harness:Repo` is an operator setting: the repository id the harness works on when a task names none. It is required when `Coding:Harness:Enabled` is true and must match the repo id pattern; a missing or malformed value stops the host start with a message naming the key. It is not checked against the repo source at start (the source may need the network); an id the source does not know refuses the open (fail closed).
  - `ICodingTaskWorkspace` (`Bazaar.Server.CodingWorkspace.Abstractions`; implementation `CodingTaskWorkspace` in `Bazaar.Server.CodingWorkspace`) is the one place a task worktree is opened. `OpenAsync(agentId, taskId, repoId?, gcId?)` takes the repo from the argument or the default, finds it through `ICodingRepoSource`, and opens the worktree through `CodingGitService.CreateWorktreeAsync` (so the path `<agent work share>/<repoId>/<taskId>`, branch `agent/<taskId>`, the Passport checks and the two host locks are the existing ones, not a second copy). The task id is used as is, never rewritten: it must be a valid branch slug (`[a-z0-9._-]`, at most 100 characters, no leading `.` or `-`, no `..`, no trailing `.`, no `.lock` suffix) and a safe path segment (no Windows device name). The GC id for the commit trailer defaults to the task id.
  - It refuses (typed error, nothing registered or held) an unknown agent, an unknown or unsafe repo, an unsafe task id, a worktree directory that already exists (even a released one: reuse is a separate decision) and a task that is already open. The engine's add is idempotent, so the directory check is what makes "existing worktree" an error.
  - The opened worktree is registered with a port `ICodingWorktreeRegistrar` under a key the module issues: 32 random bytes, base64url, from `RandomNumberGenerator`, never derived from the agent, task or path, so the model cannot guess or choose it. The host binds the port to `CodingWorktreeRegistry`. The key is returned to the caller (the host code that starts the turn), which hands it to the tool context; it is not a credential and carries no authority beyond what `ICodingToolHostContext` already checks (agent and session of a running turn).
  - `CloseAsync(key)` unregisters, releases the locks and keeps the directory and branch (the commits may be needed for review). `DiscardAsync(key)` does that and then deletes the worktree directory (checked to be strictly under the agent's work-share root and not near the Passport folder; links are removed, never followed). The branch and git's worktree record stay until the pruning job (out of scope above); opening the same task again meanwhile is refused. An unknown key is a no-op for both.
- Out of scope here: creating the share directories (the engine does when it adds a worktree), SMB sharing and guest mounts (doc 16, WP8), removing finished worktrees.

## Behaviour and edge cases
- Command output is untrusted. Output that echoes external text taints the session hard (Question 3).
- A timeout returns a timeout code, never success. Partial output is kept.
- VM crash mid-command: recorded as `unknown`; the turn resumes from the trace; only idempotent steps re-run.
- One writer per worktree, by host lock. Merge conflicts go to a GC, not a silent overwrite.
- Path jail rejects `..`, absolute paths and symlinks that leave the worktree.
- Kill switch: stop the VM, revoke host-held tokens, freeze Tier 2 classes (doc 15).
- Hard taint detaches the Passport folder; the work share is unaffected.
- Repo-supplied MCP servers and instruction files are never auto-loaded (threat model P1).
- Per-agent rate caps on commands and pushes come from doc 15's quota table (C36); a breach freezes the agent and notifies the owner.

## Testing
- Fake workspace and scripted gateway for deterministic tool sequences. Record and replay of model I/O at the `ModelEvent` seam, through its `IChatClient` adapter (C29).
- Real Postgres for trace and approval tables; temporary git repos for worktrees.
- Negative tests (must refuse or flag): test-file edit, Skip added, test count drop, timeout reported as success, path escape, repo MCP config loaded, push while hard-tainted without approval, two writers on one worktree, worktree on the Passport folder, any token path into a VM.
- Live periodic eval: about 40 Bazaar-history tasks (bug fix, feature with test, refactor, Avalonia.Headless UI change, Godot check, Luau lint fix) plus traps: a wrong test (must flag), an impossible task (must report failure), an injected issue body. Per model and edit format, 3 seeds. Measure held-out pass rate, format compliance, fake-success rate, tokens, turns, cost.

## Work packages
1. Contracts: `CapabilityClass`, tool-call and check-run shapes (Wave 1 WP1). The approval shape comes from doc 15.
2. `tool_calls` and `check_runs` tables and migrations, applied by the explicit `migrate` step (doc 04). Depends on 1.
3. `IWorkspace` facade over `IGuestChannel`, with an in-memory fake. Depends on 1 and doc 16.
4. `IGitProvider`: LibGit2Sharp implementation gated by S23, plus the CLI alternative. Depends on 3 and S23.
5. Guest-agent conformance tests against doc 16's agents (streaming, timeout, cancel). Depends on 3.
6. Edit tools in three formats, with path jail. Depends on 3.
7. Search and repo map. Depends on 3.
8. Permission gate: class-to-policy lookup, taint, approvals. Depends on 1 and 2.
9. Check runner: clean build, test count, test-file and fake-success scans. Depends on 2 and 3.
10. Worktree and push service over `IGitProvider`; host-side push and draft PR via doc 21. Depends on 4 and doc 21.
11. Review context package and reviewer tool set. Depends on 8 and 10.
12. Subagent spawner. Depends on 8.
13. Benchmark task set and runner. Depends on 6 and 9.
14. Avalonia.Headless tests for the review-diff view. Depends on 11 and the client shell (doc 28).
15. Coding tool runner (PROPOSED, 2026-11-20). Depends on 6, 8, 10. It is not a second agent loop: the model call, turn loop and tool dispatch stay in `Bazaar.Server.Agents` (doc 06); the runner is the guarded tool body a host adapter registers in that dispatcher, one registration per coding tool. Decisions: (a) an unknown tool is traced as an `Exec` row and completed as failed with exit code 127; (b) a handler exception completes the row as failed with exit code -1 and shows the model only the exception type; (c) a handler cancelled by the caller completes the row as cancelled and the cancellation is rethrown; a handler that times out on its own is a timeout; (d) a writer tool on a worktree waits for `IWorktreeLocks` (polling, default 30 s, then a `busy` result, exit code 75); a writer call without a worktree key fails with exit code 64; (e) the completion write always uses `CancellationToken.None`, so the recorder is called even after cancellation; (f) the args hash is SHA-256 of the raw JSON text until Server's canonical hash is bound by the host adapter.

## Coding tool handlers (WP15 follow-up) [PROPOSED, 2026-11-20]
Implementation: `src/Bazaar.Server.CodingRunner/Handlers/` (one class per tool), registered by `AddBazaarCodingToolHandlers` in `CodingToolHandlerRegistration.cs`. The handlers sit in CodingRunner, not in the owning modules, because none of RepoMap, CodingGit, CodingReview or Subagents references `Bazaar.Server.CodingRunner.Abstractions`. The host binds the services each one wraps and `ICodingToolHostContext`.

| Tool | CapabilityClass | Writes worktree | Required args | Optional args and notes |
|---|---|---|---|---|
| `search` | Read | no | `pattern` (1 to 256 chars) | `glob`, `max_results` (1 to 1000, default 100), `case_insensitive`; ripgrep through `ISearchTool` |
| `repo_map` | Read | no | none | `root` (default `.`), `token_budget` (1 to 16000, default 1000), `focus_files` |
| `git_diff` | Read | no | none | needs a worktree key; patch capped at 200 000 characters, `patch_truncated` says when the cap cut it |
| `git_commit` | Exec | yes | `subject` | `body`; the credited model comes from the host; trailers and the `Signed-off-by` ban are `CommitMessageBuilder`'s rules |
| `git_push_draft_pr` | Post | no | `title` | `body`, `branch` (any value other than the worktree's own branch is refused); the push gate decides; draft only |
| `review` | Post | no | `repository`, `number`, `base_commit`, `head_commit` (hex, 7 to 64 chars) | the author model comes from the host; a `NotCompleted` review is a failure, not a verdict |
| `subagent_spawn` | Exec | no | `task`, `tool_allowlist`, `model_id` | `worktree` (`none` default, `read_only`, `own`), `declared_paths`, `references`; waits for the child |
| `read` | Read | no | `path` (1 to 1024 characters) | `start_line` (1 to 1 000 000, default 1), `max_lines` (1 to 1000, default 200); files over 1 MiB are refused; output capped at 200 000 characters, `truncated` says when the cap or `max_lines` cut it; files are read through `IWorktreeFiles` behind `PathJail` |
| `list` | Read | no | none | `path` (default: the worktree root), `depth` (1 to 8, default 1), `max_entries` (1 to 2000, default 500); lists files and the directories above them, never outside the worktree |

Decisions for the gaps (the most robust option; the owner may overrule):
1. **Argument errors.** Each schema is closed: unknown fields, wrong JSON types, blank required strings and out-of-range numbers are rejected. The result is `malformed_args` with exit code 64. Argument values are never echoed back, and the model never receives a host path or a raw exception.
2. **Host facts come from the host, never from arguments.** The worktree for a key, the commit model, the reviewer's author model and the parent limits all come from `ICodingToolHostContext`. The parent limits must name the calling session, or the call fails with `parent_mismatch`.
3. **Git refusals** surface as `git_<error kind>` (for example `git_push_denied`, `git_push_needs_approval`, `git_nothing_to_commit`), using the service's fixed message. Nothing is pushed or committed in those cases.
4. **Subagents run inline.** `subagent_spawn` waits for the child's completion. A cancelled call cancels the child. The child's summary is model output, returned as data with `summary_is_untrusted: true`. A failed or cancelled child exits 1; a refused spawn returns `subagent_rejected_<reason>` and starts nothing.
5. **Review is Post, not Read.** It posts the report to the PR's group chat and sets the approval flag, which is never merge authority. The review inputs are commit ids the model supplies, so the host's review sink must check them against the PR's current head before it records a verdict. That binding is OPEN and belongs to the host adapter.
6. **Exit codes.** 0 is success; 1 is a failed tool or a refused operation; 64 is a usage error (malformed arguments, or no worktree the host holds). The runner's own codes (127, 75, -1) are unchanged.
7. **Worktree files (read and list) [PROPOSED, 2026-11-22].** The two handlers reach a worktree's files through `IWorktreeFilesSource`, a port in `CodingRunner.Handlers` that the host binds per `CodingWorktree`. It returns null when no store exists for that worktree, and the call fails with `worktree_files_unavailable` (exit 1). No default store is registered, the same rule as `CodingEditModule`.
8. **Path refusals (read and list) [PROPOSED].** A path that fails the lexical rules (empty, or naming a non-file) is `malformed_args` (exit 64). A path that `PathJail.AuthorizeAsync` refuses for escape (`..`, absolute, drive or device marker, a symlink that leaves the worktree or cannot be resolved) is `path_escape` (exit 1) with a fixed message. `list` with no path, an empty path or `.` lists the worktree root; the root is the only path that skips the jail.
9. **Read limits [PROPOSED].** `IWorktreeFiles` has no ranged or stat read, so `read` loads the whole file and then refuses it as `file_too_large` (exit 1) when it is over 1 MiB, before any text is returned. A ranged or size-only port method is OPEN; it would bound memory before the load. A file with a NUL byte is refused as `binary_file` (exit 1). Lines are 1-based; `start_line` past the end returns no lines with `total_lines` set.
10. **List shape [PROPOSED].** `IWorktreeFiles.ListAsync` returns files only, recursively. `list` derives directory entries from the file paths under the requested root, within `depth`. Each candidate entry is resolved through `ResolveRealPathAsync`; an entry that resolves outside the worktree is omitted and counted as `omitted_escaping`, never listed. Empty directories are not shown; a directory-entry port method is OPEN. A path with no files under it, including a directory that does not exist, lists no entries and succeeds (`entries` empty); only a store exception fails the call, as `list_failed` (exit 1), with a fixed message.
11. **Untrusted output [PROPOSED].** Both results carry `content_is_untrusted: true`. File text and paths are repository content and are data only, as with `search`.

## Shell and run_checks handlers (WP15 follow-up) [PROPOSED, 2026-11-22]
Implementation: `Handlers/ShellToolHandler.cs` and `Handlers/RunChecksToolHandler.cs` in `Bazaar.Server.CodingRunner`. Both reach the guest only through doc 16's `IGuestChannel` (`Bazaar.Server.Coding.Workspace`); neither starts a process on the server host, and no command text goes through a host shell. The host binds two ports declared in `Bazaar.Server.CodingRunner.Abstractions`: `IGuestWorktreeSource` (agent id and worktree key to the guest channel and the worktree's guest root) and `ICheckSubjectSource` (the commit, base commit, base test count, changed files and the configured check commands). Neither value ever comes from the model's arguments. A missing binding fails resolution, so the tools never run unguarded. The permission gate is the only policy: the runner asks it before the handler runs, so a denied or approval-needed call never reaches the guest. The handlers add no allow or deny list of commands.

| Tool | CapabilityClass | Writes worktree | Arguments |
|---|---|---|---|
| `shell` | Exec | yes | `argv` (required, 1 to 64 non-blank strings, each at most 4096 characters, no NUL), `cwd` (relative to the worktree, default `.`), `timeout_seconds` (1 to the cap, default `harness.command_timeout`) |
| `run_checks` | Read | yes | none; the schema is closed. The agent requests a run and never chooses what runs |

`run_checks` is serialised per worktree because a build writes into it.

Decisions for the gaps (the most robust option; the owner may overrule):
1. **No shell string.** `shell` takes an argv list and the guest starts it with no shell in between, so metacharacters in an argument stay data. An agent that wants a shell runs one inside the guest by naming it in argv; that is the guest's sandbox, never the host. Arguments with a NUL character are refused.
2. **Path jail.** `cwd` goes through `GuestPath.Resolve` (no absolute paths, drive or device markers, or `..`); a refusal is `path_escape`, exit 64. Symlink resolution stays the guest agent's job (doc 16).
3. **Environment.** The call sends an empty environment, so no host or Passport value reaches the process. Output is also passed through a secret redactor (common token shapes and private-key blocks) before it reaches the model or any row. The redactor is a backstop, not the control.
4. **Timeout and output cap.** The timeout is enforced by the guest and again host-side; either one gives `timed_out: true` and the outcome is `TimedOut`, never a success whatever the exit code. Output is capped at 65 536 characters across both streams, the rest is drained and dropped, and `output_truncated` says so. Partial output is kept on a timeout.
5. **Untrusted output.** The result carries `output_is_untrusted: true`. Output is command output and may echo third-party text, so it is data only (Question 3 governs the taint class; the handler does not change taint itself).
6. **Result shape and exit codes.** `shell` returns `exit_code`, `timed_out`, `stdout`, `stderr`, `output_truncated`. The call exits with the guest's own code; a timeout, a cancel, a guest fault or a missing exit code is never 0. A guest fault returns `guest_unavailable` (exit 1) with no host detail. A call whose worktree the host does not hold is `no_worktree` (exit 64).
7. **Cancellation.** A cancelled shell call stops the guest process, is recorded by the runner as cancelled, and the cancellation is rethrown.
8. **`run_checks` order and verdict.** The configured commands run in order. A build command that exits non-zero stops the run (later tools stay `null`). After the commands, the server runs `FakeSuccessScanner`, `TestFileChangeFlagger` and, when the base count is known, `TestCountGate` on the host-supplied changed files and the parsed test counts, and builds a `CheckRunRecord`. Verdict: `error` when any command timed out, was cancelled, had no exit code or hit a guest fault, when no command is configured, when a test command's counts cannot be parsed while a base count exists, or when the subject cannot be resolved (an `error` is never a pass); otherwise `fail` when any tool exits non-zero, the count gate fires or a flag outside the allowed codes is raised (`test-file-changed` is allowed by default: it asks for a named reviewer and does not fail the run); otherwise `pass`. Test counts are read from the test tool's output (`Passed:`, `Failed:`, `Skipped:` summary), never from the agent.
9. **Recording.** The record is appended through `ICheckRunStore`. A failed append returns `check_run_not_recorded` (exit 1): no verdict is claimed that is not stored. On cancellation an `error` row is still appended, with no token, before the cancel is rethrown. The model receives the verdict, per-tool exit codes, counts and flags; no logs and no host paths.

## Conflicts
1. **Subagent depth.** This doc uses depth 1 inside doc 06's runtime limit (depth 2, 3 concurrent). Doc 06 must still state that its limit covers runtime delegation only.
2. **Parallel subagents.** Cognition (June 2025, a vendor opinion) calls them fragile; Claude Code docs describe them with a default cap of 20; Anthropic (June 2025) calls most coding tasks a poor fit for fan-out. Unresolved; this design defaults to one writer and measures.
3. **Haiku edit quality.** No evidence covers Haiku 5.5 edit quality. The benchmark may change the edit format, not the model.
4. **Taint on routine coding (C06 vs C18).** Stranger repository files are now classed as external and reach coding actors only through reader sessions (this pass). Owner-repo files and command output can still carry third-party text; until Question 3 is answered, a session that reads such text is hard-tainted and its pushes need approval.
5. **CLAUDE.md wording.** "Subagents and workers run on Haiku 5.5" reads as runtime policy; it governs build agents. Fix in the CLAUDE.md pass.
6. **Approval fatigue.** C06 and C18 reduce per-action prompts, but "always allow" remains a documented failure mode (threat model).
7. **Roblox.** The official Studio MCP needs an open Studio, so agents cannot call it headlessly. Any future path that modifies open places needs approval.

## Questions for the owner
ANSWERED: git sits behind `IGitProvider`; LibGit2Sharp stays decided unless S23 fails (DECISIONS-LOG round 3).
ANSWERED: taint propagates only from a tainted source session; external content is hard, agent text soft (round 2).
ANSWERED: the owner approves merges in a Bazaar client; the server squash-merges with the platform's `Signed-off-by` line; DCO vs CLA stays OPEN (rounds 8 and 12).
ANSWERED: worktrees sit on a separate work share, never the Passport folder (C02, round 2; the separate share is required, so no question remains).

1. Pushes to `agent/*`: gated only by hard taint (this draft), or always, because C18 lists repo push among outward actions? Recommended: hard taint only, so routine work keeps moving. Confirm Claude's reading of C18 (a question because it reads the approval gate).
2. May agents ever load repo-level instruction files or repo-supplied MCP servers? Recommended: no; Soul configuration only. Loosening threat model P1 needs the owner.
3. Taint class for the owner's own repo content and command output. Recommended: the owner's own repo files and build output are agent text (soft); third-party docs, issues and web content are external (hard) and reach coding actors only through a reader session (C18). Confirm?

MOVED TO SETTINGS: harness.command_timeout, harness.task_time_limit, harness.edit_format, harness.benchmark_max_runs, harness.coding_vm_usage, harness.subagent_depth, harness.subagent_disjoint_writers, harness.test_count_drop_tolerance, harness.test_file_review_flag, harness.code_intel.tree_sitter (DECISIONS-LOG round 13)
