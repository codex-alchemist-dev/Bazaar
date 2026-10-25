# Coding-Agent Harness

Status: DRAFT for owner review (2026-10-25) · Owner questions: 12
Depends on: Bazaar.md (GCs, taint rule), Bazaar.Soul.md (VM pair, mounts), Bazaar.Server.md (gateway, Git, approvals), implementation-plan.md · Used by: agent runtime, review flow, Vault writer, benchmark (none written yet)

## Purpose
The tools and mechanics a coworker agent uses to read, edit, build, test and commit code inside its VM pair. The server checks results rather than trusting agent self-reports, so other agents and the owner can review them.

## Decided inputs
- Each agent has a Windows VM and a Linux VM, headless by default (Bazaar.Soul §2, CHANGED 2026-10-24).
- Soul data stays off the VM disk; one scoped per-agent folder is mounted. No clipboard, no drive passthrough (Soul §2).
- Godot `--headless` and Luau headless checks are the agent engine path. Roblox Studio runs only on the owner's Bazaar.Windows desktop (Soul §2).
- Once a session has seen untrusted input, post, send, spend and push need owner approval (Server §4, item 13).
- Model whitelists are per agent, enforced by Bazaar.Server's gateway; no global `auto/*`; Claude lane uses API keys only (Bazaar.md §3).
- Server-side Git uses LibGit2Sharp, not the git CLI (Server §2). See Conflicts.
- GCs are append-only; every edit version is kept (Bazaar.md §2). Subagents and workers run on Haiku 5.5, and UI is tested with Avalonia.Headless on xUnit v3 (CLAUDE.md).

## Design

### Tool set [PROPOSED]
| Tool | Function | Tier | Runs in |
|---|---|---|---|
| read, list, search | files, ripgrep, repo map | Read | Linux VM worktree |
| edit | whole-file, line-anchored or search/replace | Read (in worktree) | Linux VM |
| shell | argv command, cwd jailed, timeout | Exec (no secrets, egress allowlisted) | Linux VM |
| git (local) | status, diff, branch, commit, worktree | Read / Exec | Linux VM |
| git push, draft PR | push `agent/*`, open draft PR | Post | Brokered GitHub App token |
| browser | Playwright, no Passport, tokens injected per service | Post | Browser VM |
| MCP | Bazaar tools (GC, Kanban, memory); external servers only from Bazaar config | Per tool | Host |
| run_checks | server-owned verification; agent requests, never edits | Read | Server dispatches to VM |

Tiers follow the threat model (Read, Post, Spend, Create-account, Grant-scope, Merge, Deploy), with Exec added. The server enforces tiers in tool dispatch, never in the prompt.

### Edit formats and benchmark
- Evidence is 2024–25 Aider data: whole-file had the highest format compliance for weak models; diff formats gave better pass rates and fewer tokens for strong ones. There is no data for Haiku 5.5 or DeepSeek V4 (research note, verified).
- Default until measured: whole-file for models without data [PROPOSED]. Each model's format goes in its agent whitelist, chosen by the benchmark (Testing).
- Metrics: format compliance, malformed-edit rate, pass rate, tokens per task. Architect/editor splitting is an unmeasured option.

### Context engine [PROPOSED]
- Per-request package, in order: current file and diff; the GC thread that asked; the task's dependency state; a repo map under a token budget (Aider's default is 1k). All else is fetched by tool.
- Compaction clears old tool results first, then summarises. Structured notes live in the task record, not the repo. Plans are Kanban subtasks with blocked-by edges; scratch notes stay private; a todo list grants no authority.
- Code intelligence: Roslyn for exact C# symbols, the likely first choice (not researched). Tree-sitter for Luau and GDScript (TreeSitter.DotNet 1.3.0 is secondary; platform support conflicts). No embeddings until grep plus repo map is shown to be worse.

### Verification (server-side)
Agent self-reports are data. For each PR the server runs, in a fresh clone of the commit:
1. Clean build with no cached outputs; exit code and log recorded.
2. Test count must not drop against the base branch.
3. Test-file changes are flagged and need a named reviewer.
4. Fake-success scan: new Skip or Ignore attributes, `#if false`, removed tests or assertions.
5. UI changes need an Avalonia.Headless test; a screenshot is not evidence.
6. Luau: `luau-analyze` and Selene. Godot: `--check-only` and `--script`, with the binary build checked first.

Every tool call is traced with its exit code, so a run can be replayed.

### Git workflow [PROPOSED]
- Branch `agent/<task>`; one worktree per writer (git allows one branch in one worktree only; secondary source).
- Commit author is the agent's GitHub App identity. Trailers: `Co-Authored-By` for the model, plus GC and task IDs.
- Agents never push to the default branch and never add `Signed-off-by`; the human certifies (Linux kernel policy, via research note).
- Owner merge sign-off is recorded by the platform under a signed human identity. An agent message is never an approval. PRs open as drafts.
- Worktrees sit on the mounted folder or are pushed before any golden-image rebase, because a rebase wipes child-disk changes (Soul §2).

### Review flow [PROPOSED]
Author opens a draft PR. A reviewer on another model gets read-only tools, no shell, and the diff plus decision context (task card, GC thread, check results). The review is posted to the GC. The owner merges; reviewer approval is not merge authority. Outcomes feed reputation (Bazaar.md §5).

### Permissions [PROPOSED]
- A tainted session cannot call Post, Spend or Merge without a per-action owner approval. Approvals are per action or time-boxed with visible scope; no blanket allow (threat model §5).
- Untrusted sources: repo files, issues, web, email, dependency docs and command output.
- Reader/actor split stays OPEN (Server §4, item 13).

### VM execution (guest agent) [PROPOSED]
- The host talks to an `IWorkspace`. The VM backend sits behind it: Hyper-V on Windows, KVM on Linux.
- Guest agent: start (argv, cwd, env allowlist, timeout), streamed stdout and stderr with sequence numbers, cancel, read and write jailed to the worktree.
- Transport: Hyper-V sockets for the Windows guest; AF_VSOCK for Linux, since PowerShell Direct is Windows-guest only (verified). SSH is an unverified fallback.
- A timeout kills the process tree. CPU and memory use cgroups (Linux) or job objects (Windows). Values are placeholders until the NUC sizing benchmark.

### Subagents [PROPOSED]
Fresh context, tool allowlist, Haiku 5.5. Returns a summary plus a link to its full trace. Default depth 1. Writers never share a worktree; parallel work is only over disjoint files in separate worktrees.

### UI and engine verification
Avalonia.Headless.XUnit with `[AvaloniaFact]` and Skia frame capture for pixel checks (verified, build tooling note). Headless cannot cover windowing, IME, DPI, fold posture or accessibility; those need owner-side testing. Godot: headless import, `--check-only`, `--script`. Roblox Studio has no agent path.

## Data and interfaces (sketches)
```csharp
public enum ToolTier { Read, Exec, Post, Spend, CreateAccount, GrantScope, Merge, Deploy }

public interface IWorkspace : IAsyncDisposable
{
    Task<ProcessHandle> StartAsync(ProcessSpec spec, CancellationToken ct);
    IAsyncEnumerable<OutputChunk> StreamAsync(ProcessHandle h, long fromSeq, CancellationToken ct);
    Task<WorkspaceFile> ReadAsync(WorkspacePath path, CancellationToken ct);
    Task WriteAsync(WorkspacePath path, ReadOnlyMemory<byte> content, CancellationToken ct);
}
```
Tables (outline): `tool_calls` (append-only, host-written: session, tool, tier, taint tags, exit code, args hash); `check_runs` (commit, base, build exit, test counts, flags); `approvals` (signed human ID, scope, expires_at, consumed_at).

## Behaviour and edge cases
- Command output is untrusted; injected text in test output taints the session.
- A timeout returns a timeout code, never success. Partial output is kept.
- VM crash mid-command: the call is recorded as `unknown`; the turn resumes from the trace; only idempotent steps re-run.
- One writer per worktree, by host lock. Merge conflicts go to a GC, not a silent overwrite.
- Path jail rejects `..`, absolute paths and symlinks that leave the worktree.
- Kill switch: stop the VM, revoke brokered tokens, freeze the Post tier.
- Repo-supplied MCP servers and instruction files are never auto-loaded (threat model P1).
- Per-agent rate caps on commands and pushes; a breach freezes the agent and notifies the owner.

## Testing
- Fake workspace and scripted gateway for deterministic tool sequences; record and replay of model I/O behind `IChatClient`.
- Real Postgres for trace and approval tables; temporary git repos for worktrees.
- Negative tests (must refuse or flag): test-file edit, Skip added, test count drop, timeout reported as success, path escape, repo MCP config loaded, push while tainted without approval, two writers on one worktree.
- Live periodic eval, Bazaar-specific: about 40 tasks from Bazaar's history (bug fix, feature with test, refactor, Avalonia.Headless UI change, Godot script check, Luau lint fix), plus traps: a wrong test (must flag, not edit), an impossible task (must report failure), an injected issue body. Run per model and per edit format, 3 seeds. Measure held-out pass rate, format compliance, fake-success rate, tokens, turns, cost.

## Work packages
1. Contracts: `ToolTier`, tool-call, check-run and approval shapes (Wave 1 WP1).
2. Trace and approval tables and migrations. Depends on 1.
3. `IWorkspace`, in-memory fake, guest protocol. Depends on 1.
4. Linux guest agent (AF_VSOCK): streaming, timeout, cgroups. Depends on 3.
5. Windows guest agent (Hyper-V sockets). Depends on 3.
6. Edit tools in three formats, with path jail. Depends on 3.
7. Search and repo map. Depends on 3.
8. Permission gate: tiers, taint, approvals. Depends on 1 and 2.
9. Check runner: clean build, test count, test-file and fake-success scans. Depends on 2 and 3.
10. Git service: worktrees, branches, draft PRs, trailers. Depends on 3 and Question 1.
11. Review context package and reviewer tool set. Depends on 8 and 10.
12. Subagent spawner. Depends on 8.
13. Benchmark task set and runner. Depends on 6 and 9.
14. Avalonia.Headless tests for the review-diff view. Depends on 11 and the Wave 4 client shell.

## Conflicts
1. **Git engine.** Server §2 says no git CLI. This design runs the git CLI inside the agent VM and LibGit2Sharp on the server. LibGit2Sharp worktree and merge coverage is unverified (Vault note).
2. **Parallel subagents.** Cognition (June 2025, a vendor opinion) calls parallel subagents fragile. Claude Code docs describe parallel subagents, default cap 20. Anthropic (June 2025) calls most coding tasks a poor fit for fan-out. The Claude Code docs are the more current source, so the Cognition description may be outdated. Not resolved; this design defaults to one writer and measures.
3. **Taint scope.** Server §4 names email, strangers' issues and web. The threat model also taints on other agents' GC messages, which are append-only and so poison permanently. Applied fully, nearly every coding session is tainted.
4. **Approval fatigue.** Per-push approval while tainted is the main usability risk; the threat model reports "always allow" as a documented failure mode.
5. **Haiku-only subagents** are fixed by policy, but no evidence covers Haiku 5.5 edit quality. The benchmark may change the edit format, not the model.
6. **Roblox.** The official Studio MCP needs an open Studio, so agents cannot call it headlessly. Its tools can modify open places, so any future path needs approval.

## Questions for the owner
1. Git in the agent VM: git CLI in the worktree (recommended), LibGit2Sharp server-side only? Or LibGit2Sharp everywhere after a worktree spike?
2. Taint propagation: does another agent's message taint the reader? Recommended: only when the source session was tainted.
3. Pushes to `agent/*`: allowed untainted, owner approval per push when tainted. Confirm?
4. Merge sign-off: who signs (you, by default), and how is it recorded? DCO vs CLA is still open.
5. Edit format for models without data: whole-file (recommended) or line-anchored?
6. Benchmark spend: approve about 40 tasks × 3 formats × 3 seeds per model, with a cap per run?
7. Coding VM: Linux for all coding, Windows only for Windows-only checks? Recommended: yes.
8. Subagents: depth 1, read-only parallel, writers serialised. Or allow writers on disjoint files?
9. Command timeout 10 minutes, task limit 2 hours; VM limits are placeholders until the NUC benchmark. Confirm?
10. Test-count drop: zero tolerance, no override (recommended). Test-file edits need a named reviewer flag. Confirm?
11. Code intelligence: Roslyn for C# first, tree-sitter deferred until Luau or GDScript work starts?
12. May agents ever load repo-level instruction files? Recommended: no, Soul config only.
