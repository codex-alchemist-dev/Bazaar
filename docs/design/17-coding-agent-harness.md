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
