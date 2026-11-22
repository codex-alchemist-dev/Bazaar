# 91. Claude Code CLI provider

Status: PROPOSED (2026-11-20). One item is DECIDED: the owner asked on 2026-11-20 for a provider that drives the Claude Code CLI so agents can use the owner's Claude subscription instead of an Anthropic API key. Everything else here is PROPOSED, including the guard rails, which exist because of the terms finding in section 2.
Depends on: 05-model-gateway.md (adapter seam, quota states, policy, usage), 06-agent-runtime.md (turn loop), 15-security-taint-and-approvals.md (`ToolDispatcher` is the only path from model output to side effects), 14-identity-service-and-passport.md (secret references), 40-core-feature-modules-audit.md (module rules), 46-kodo-health-module.md (health contract), 63 (encrypted data folder, open PR #874).
Amends: 05 "Claude lane" (the "subscription-backed routes are excluded" sentence), 05b section 4 (subscription routing), DECISIONS-LOG C14.
Owner questions: 3 (section 15).

## 1. What this is

A provider module that runs the unmodified `claude` command line program in print mode, once per model step, and turns its `stream-json` output into the gateway's `ModelEvent` stream. Agents on this route use the model access of the Claude plan the operator signed in with, instead of an API key.

It is one more upstream behind the gateway. Bazaar keeps its own policy, quota tracking, tool dispatch, taint rules and audit. Claude Code is used as a model endpoint with every one of its own tools switched off.

## 2. What Anthropic's terms allow (read 2026-11-20, primary pages)

Sources, all read directly:
- `code.claude.com/docs/en/legal-and-compliance`, sections "Can customers offer Claude Code in their products?" and "Authentication and credential use".
- `code.claude.com/docs/en/agent-sdk/overview` (notice box).
- `code.claude.com/docs/en/headless` and `authentication` (what bare mode and tokens do).

Facts, stated as the pages state them:

0. **Condition:** running Claude Code inside a product or service "requires agreeing to our Commercial Terms of Service", and the binary must not be modified: customers "may not remove, disable, or restrict any authentication method built into it".
1. **Not allowed:** a third-party developer offering Claude.ai login (the Agent SDK page adds "or rate limits") in their own application, or routing requests through Free, Pro, Max or Ultra plan credentials on behalf of their users. Developers building products on the Agent SDK "should use API key authentication". The Agent SDK page repeats this and adds "unless previously approved".
2. **Not allowed:** collecting, storing or intermediating Claude.ai credentials or session tokens. Sign-in must complete through Anthropic's own flow.
3. **Allowed:** "an end user signing in to the unmodified Claude Code binary with their own Claude subscription, including where a platform hosts Claude Code". The binary must not be modified, and no built-in authentication method may be removed or restricted.
4. **Not allowed:** paying for, reselling or intermediating Claude usage for other people. Each end user authenticates with their own plan, key or cloud credential.
5. **Caution, not a ban:** advertised plan limits "assume ordinary, individual usage of Claude Code and the Agent SDK". Anthropic may enforce its rules without notice.
6. Bare mode (`--bare`) never reads subscription login or the keychain. `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`) is a long-lived subscription token that Bazaar must not hold (fact 2).

What this means, plainly. Reading 3 permits the shape the owner wants: the operator signs in to the real `claude` program themselves, and a Bazaar server hosts that program. It does not permit Bazaar to handle the login, to use the Agent SDK libraries with subscription credentials, to share one person's plan with other people, or to make the account look like a person using the product normally when it is a fleet running all day. The last point is a risk, not a rule: a company of agents working around the clock is not "ordinary, individual usage" by any reasonable reading, and the pages give no number. The account can be limited or suspended. Counsel review is already planned for the first public release (docs 47, 60); this lane goes on that list.

### Rules that follow (T1 to T9, all PROPOSED)

- **T1.** Run only the unmodified `claude` binary, as published. No patching, no wrapper that edits its auth, no `@anthropic-ai/claude-agent-sdk` package.
- **T2.** Bazaar never reads, copies, stores, logs or forwards a subscription credential. The login lives in a Claude configuration directory that belongs to the operating system user running the server (section 11). The module never opens `.credentials.json` or the keychain entry. Backups and exports exclude that directory.
- **T3.** Bazaar refuses a subscription OAuth token everywhere: not as a secret reference value (`sk-ant-oat`, `sk-ant-ort` prefixes already refused by doc 05), and not through `CLAUDE_CODE_OAUTH_TOKEN`. The child process environment is built from an allowlist, and that variable is never on it.
- **T4.** Sign-in is done by the operator in their own terminal with the unmodified program. The admin panel shows the exact command and the directory; it does not wrap the browser flow.
- **T5.** One login serves one human. The lane is bound to one `user` principal (the login owner) and is offered only to agents of an install whose human membership is that one person, unless the operator turns the single-person guard off after a recorded acknowledgement that each human must then use their own login (not supported in v1, section 11).
- **T6.** Off by default. Turning it on needs the operator's acknowledgement of the section 2 summary, stored with a timestamp like the FreeLLMAPI warnings (doc 05, step 2).
- **T7.** Conservative load: a small concurrency cap and an hourly step cap by default (section 9), so use looks closer to one person's than to a fleet's. The operator may change them (audited); an agent never can.
- **T8.** Rate limits and account errors (limit reached, `authentication_failed`, `account_on_hold`, `oauth_org_not_allowed`) stop the lane and raise an admin alert. The module never retries around them, rotates logins or hides the cause.
- **T10.** The operator accepts the Commercial Terms for hosting the program (recorded with the T6 acknowledgement). T3's refusals are Bazaar-side refusals to store or forward a token, not a change to the binary; whether blocking `CLAUDE_CODE_OAUTH_TOKEN` and not using `--bare` for login mode counts as restricting an authentication method goes to counsel with the rest of this lane.
- **T9.** API key mode (an `ANTHROPIC_API_KEY` secret reference, billed to the operator's Console account) is supported through the same module. It is the compliant fallback and is what the module recommends for any install that has more than one human or runs unattended around the clock.

## 3. Changes to DECIDED items

C14 and doc 05 say subscription logins stay off every route. The owner's request of 2026-11-20 adds a route that uses a subscription login held by the CLI. The credential rule behind C14 ("never route subscription credentials through the gateway") still holds in letter: the gateway never sees a credential, because the CLI keeps it (T2, T3). What changes is the sentence "no Claude Code tier or subscription login can be configured". The docs PR for this note edits that sentence in doc 05, adds a pointer in 05b section 4, and logs round 30 in DECISIONS-LOG. FreeLLMAPI's own "Claude Code tiers" stay refused: that is a third party holding the login.

CLAUDE.md said the same in two places; the owner accepted the account risk on 2026-11-20, and both lines now name this provider as the one exception.

The usage `identity` label gains a third value, `subscription`, beside `native` and `pooled`. Cost records from this lane carry the CLI's `total_cost_usd` as a notional figure, marked as not billed.

## 4. Module map

| Project | Role |
|---|---|
| `Bazaar.Server.Gateway.Abstractions` | New, small extraction: `IModelAdapter`, `ProviderEndpoint`, `ModelAdapterRegistration`, `AdapterKind` move out of `Bazaar.Server` so a provider module can implement the adapter seam without referencing `Bazaar.Server`. Namespace stays `Bazaar.Server.Gateway.Adapters`. |
| `Bazaar.Server.Providers.ClaudeCode.Abstractions` (required) | `ClaudeCodeOptions`, `IClaudeCodeProcessRunner` (the seam the fake CLI replaces), `ClaudeCodeStatus`, `IClaudeCodeLaneState` (read by Kodo and the admin panel). No process code. |
| `Bazaar.Server.Providers.ClaudeCode` | Component `claude-code-cli` (`ComponentKind.Provider`, provides `gateway:provider:claude-code`). The adapter registered as wire name `claude_code_cli`, the process runner, the stream parser, the definition-only tool bridge, health and rate-limit mapping. |
| `Bazaar.Server.Providers.ClaudeCode.Tests` | Tests against a fake CLI (section 13). |

Rules kept: no feature code in `Bazaar.Server`; the module references Abstractions projects only; migrations (if the `identity` label needs one) live in the module or in the gateway's own namespace as doc 04 says. A client-side admin pane is out of scope for this doc and rides doc 51 (web admin) sections.

## 5. How one model step runs

One `ModelRequest` is one process. State lives in Bazaar, not in the CLI, so a crash, a restart or a switch to another provider loses nothing.

Command (arguments go through `ProcessStartInfo.ArgumentList`, never a shell string and never through `cmd.exe`):

| Argument | Why |
|---|---|
| `-p` | Print mode, reads the prompt from standard input. The prompt is never on the command line (length limits, process lists). |
| `--output-format stream-json --verbose --include-partial-messages` | Newline-delimited events with text deltas. |
| `--no-session-persistence` | The CLI keeps no transcript on disk. |
| `--model=<id>` | From the route (section 10). The id must match `^[a-z0-9][a-z0-9.\-]*$`; anything else fails the call before a process starts. |
| `--system-prompt-file <temp file>` | Replaces Claude Code's own prompt entirely (never the append flags) with the agent's system messages, byte for byte. File in a private temp folder, deleted after the call. |
| `--tools ""` | All built-in tools off (Bash, file tools, web). |
| `--strict-mcp-config --mcp-config <temp file>` | Only the definition-only bridge (section 7). A private temp file, because the bearer token must not appear in the process list. |
| `--max-turns 1` | Backstop: one model response per process. Not shown in `claude --help` on the version the reviewer checked (2.1.329), so it is unverified; the kill after the tool-use message is the real stop. |
| `--permission-mode dontAsk` with `--permission-prompts none` | Nothing can wait for a human. |
| `--safe-mode` | Defence in depth when the version supports it (checked, section 12). |

Not used: `--bare` (it ignores the subscription login, so it is used only in API key mode, where it is recommended), `--resume` and `--continue` (state is Bazaar's), `--dangerously-skip-permissions`, any Agent SDK package.

**History.** `stream-json` input accepts user messages only, so earlier assistant tool calls and tool results are rendered into the prompt as plain tagged sections (`<assistant>`, `<tool_call id name>`, `<tool_result id>`), in order, by one deterministic renderer. Every delimiter carries a random per-request nonce (`<tool_result nonce=...>`), content is escaped so it cannot close a section, and a renderer test feeds a forged closing tag. The same prefix repeated across steps keeps the CLI's prompt cache warm.

**Working directory and home.** The child runs in a fresh empty directory per call and with the dedicated configuration directory of section 11. Nothing of the operator's own `~/.claude` (hooks, MCP servers, CLAUDE.md, skills) is loaded.

**Environment.** Built from an allowlist: `PATH`, `SystemRoot`/`HOME` equivalents, `CLAUDE_CONFIG_DIR`, proxy variables the operator listed, and `ANTHROPIC_API_KEY` only in API key mode. Everything else is dropped, including any `ANTHROPIC_*`, `CLAUDE_CODE_*` and cloud credential variable not on the allowlist.

## 6. Process lifecycle

- **Start.** A call waits for a free slot (`max_concurrent`) and for the minimum spacing; waiting is not a failure, because reporting it as `rate_limited` would mark the whole lane limited for every agent. The hourly step cap (`max_steps_per_hour`) is a real quota: when it is full the call returns `Failure("rate_limited", retryAfter)` without starting a process, so the gateway's quota state and the Work-Life Simulation see it as capacity.
- **Kill tree.** Cancel and timeouts use `Process.Kill(entireProcessTree: true)`. For a server crash, Windows adds a Job Object with kill-on-close, created before the child can spawn anything (the child starts suspended or is placed by the job-list attribute, so no early grandchild escapes). Linux marks the child with an environment variable carrying a per-server id and a startup sweep removes only processes of the server's own user that carry it; an unknown command-line marker is not used because the unmodified CLI may reject it. The Job Object and sweep are a work package of their own (section 14).
- **Time limits.** First-event timeout (default 60 s), idle gap timeout between events (default 90 s), total timeout (default 15 min). Each ends the call with `Failure("timeout")` and kills the tree.
- **Bounded output.** Standard error is captured up to a small cap and only for diagnostics; it is scrubbed before logging. A single event line over the line cap ends the call with `Failure("provider_error")`.
- **Exit.** A clean exit without a `result` event is `stream_dropped`. A non-zero exit maps through section 9.
- **Version.** `claude --version` runs at start and on a timer. A version below the module's tested minimum (setting `claude_code.min_version`) keeps the lane `Down` with a reason. `system/init` `capabilities` is read to feature-detect, never version strings alone.

## 7. Streaming and tool bridging

### 7.1 Event mapping

| CLI event | `ModelEvent` |
|---|---|
| `system/init` | records served model and capabilities; no event |
| `stream_event` text delta | `TextDelta` |
| `stream_event` thinking delta | `ThinkingDelta` |
| assistant `tool_use` block (content block start / input JSON deltas / stop) | `ToolCallStart`, `ToolCallArgsDelta`, `ToolCallEnd` |
| `system/api_retry` | `Warning("upstream_retry", ...)`; `rate_limit` error also feeds section 9 |
| `message_start` and `message_delta` usage | accumulated; the lane's usage comes from these, so a step ended by a tool call still reports tokens |
| `result` | when it arrives, its `usage` replaces the accumulated counts; then `UsageReported` and `Done(stopReason, servedModel)` |
| `result` with `is_error` or `error_max_turns` after a tool call | mapped as below |
| anything unknown | ignored and counted (the parser is tolerant on purpose: the event set changes between versions) |

Stop reasons: the stream's own `stop_reason` maps `end_turn`, `tool_use`, `max_tokens` to `StopReason`; refusals map to `ContentFilter`.

### 7.2 Tool bridging: definitions only

Doc 15 (DECIDED): `ToolDispatcher` is the only path from model output to side effects, with taint, approvals and audit. If the CLI executed tools it would bypass all of that. So the CLI never executes anything:

1. The module runs a tiny MCP server on a loopback address with an ephemeral port and a per-process random bearer token (never the same token twice, never logged). (The JSON-RPC layer is small; it should reuse the MCP layer of doc 80 section 5 once that PROPOSED doc lands, and until then is a private minimal one.) It answers `initialize` and `tools/list` with the request's `ModelToolDefinition`s under the server name `bazaar`, so the model sees `mcp__bazaar__<tool>` and calls it with native `tool_use`.
2. Its `tools/call` handler never runs a tool. It returns a fixed error result ("deferred to the host") and counts the attempt.
3. When the stream shows a completed assistant message whose stop reason is `tool_use`, the module emits the call events, emits `Done(ToolUse)` and kills the process. The agent runtime then runs the calls through `ToolDispatcher` as it does for every other provider and sends the next `ModelRequest`, with results in `Messages`.
4. The `mcp__bazaar__` prefix is stripped before events leave the module. A tool name the request did not offer is `Failure("malformed_tool_json")`.

This keeps one loop (doc 06), one step cap, one audit trail, one taint tracker. It costs one process start per step, which is acceptable because the process is short lived and the prompt prefix is cached.

A mode where the CLI runs its own tools (a coding agent inside the CLI's loop) is explicitly out of scope: it would need its own approval and taint design and conflicts with doc 17's harness. It is listed in section 15 as a possible later module, not built.

## 8. Cancellation

The gateway's `CancellationToken` ends the call: the module stops reading, kills the process tree, deletes the temp folder, releases the slot and the loopback server, and reports nothing further. Cancellation never counts as a failure for quota or health. A server shutdown cancels every live call the same way; the Job Object (Windows) or the startup sweep (Linux) covers a crash.

## 9. Rate limits, quota and health

- **Detection** (any one is enough): a `system/api_retry` event with error `rate_limit`; a `result` with `is_error` whose text states a usage limit and a reset time; a non-zero exit with the same text; an HTTP-style rate-limit event if the version emits one (unverified, the parser accepts it when present).
- **Mapping.** The failure is `Failure("rate_limited", retryAfterSeconds)`. The reset time is parsed when the message gives one; otherwise the default `claude_code.rate_limit_default_backoff` applies (default 30 minutes, cap 6 hours). The gateway's quota tracker holds `RateLimited(until)` per (provider key, model class); here the provider key is the login, so every agent sees the same state. The gateway then reroutes only to another allowed source of the same agent (C14 order), or emits `AgentCapacityChanged` so the Work-Life Simulation can send the agent on vacation. The module reports facts only.
- **Account errors.** `authentication_failed`, `oauth_org_not_allowed`, `account_on_hold`, `billing_error` set the lane `Down` with the reason, suspend the route (`IRouteSuspender`) and raise an admin alert. No retry. The operator signs in again or switches to API key mode.
- **Own throttle.** `max_concurrent` (default 2), `max_steps_per_hour` (default 120), minimum spacing between starts (default 1 s). These are PROPOSED defaults picked to look like one person's use; Anthropic gives no number (section 2, fact 5), so they are settings, tightened or loosened by the operator and audited.
- **Health.** Kodo contract (doc 46 section 10): `claude --version` check, last successful step time, last error class, current quota state, count of ignored events (a rising count means the CLI's format moved). No paid call is made for a probe. Built: `ClaudeCodeHealth` implements the module health contract (`IModuleHealthContract`, module id `claude-code-cli`, registered only when the provider is on). Liveness does no I/O; readiness reads the lane state (Failing when down, Degraded while rate limited, with last success, last error class and ignored-event count in the reason); the self-test runs `claude --version` only (10 s limit, base environment without `CLAUDE_` or `ANTHROPIC_` names, output cut to one printable line).
- **Usage.** One `gw_usage` row per step with identity `subscription`, token counts from `result.usage`, notional cost, served model.

## 10. Per-agent configuration and policy

- Policy stays the gateway's: deny by default, entries provider-qualified, for example `claude-code-cli:claude-sonnet-5-5`. New models never join a whitelist on their own.
- The module's catalogue is a setting (`claude_code.models`, default Haiku 5.5 and Sonnet 5.5; Opus only if the operator lists it), because the CLI has no cheap model-list probe. A route per listed model, named like the API route but under this provider.
- The served model from `system/init` is checked against the route. A mismatch is the same violation path as a FreeLLMAPI wrong pick: logged, route suspended.
- Per-agent knobs the module honours, all optional and all stored with the agent's route (not in the module): model, `effort` where the CLI offers it, max output tokens as a prompt-level limit, and per-agent `max_steps_per_hour` as a tighter share of the lane. An agent cannot raise its own limits or edit policy.
- Agents are multi-provider: the same agent may list this route and an API key route and the gateway falls back between them by the doc 05 rules (`gateway.claude.order` gains the value `subscription_first`; shipped default stays `native_first`).

## 11. Authentication modes and credentials

| Mode | Setting `claude_code.auth_mode` | Credential | Notes |
|---|---|---|---|
| `login` | default when the lane is on | The CLI's own sign-in, in `claude_code.config_dir` (default `<data root>/claude-code/home`, never the system drive by default, operator-changeable) | The operator runs `claude` once with `CLAUDE_CONFIG_DIR` set to that folder and signs in themselves (T4). Bazaar only passes the folder path. |
| `api_key` | alternative | `ANTHROPIC_API_KEY` from a secret reference (`env:` or `file:`, doc 05) | Uses `--bare`. Billed to the operator's Console account, same as `anthropic-direct`. No single-person guard needed. |

Credential handling summary: the module has no credential field for `login` mode at all. The config folder is excluded from backup, export and the Soul and memory exports, listed as sensitive for the data-folder encryption (doc 63, open PR #874), and given owner-only permissions when the module creates it. If the folder is missing or not signed in, the lane is `Down` with the exact command to run.

One login, one human (T5): `claude_code.login_owner` names the `user` principal. The lane is not offered to agents of a company with more than that one human member. Several humans each with their own login would need a lane instance per person and per-person agents; that is not v1 and is a question in section 15.

## 12. Security review points

- **No tools in the child.** `--tools ""`, definition-only MCP, empty working directory, allowlisted environment, no hooks (dedicated config folder plus `--safe-mode` when supported). The capability check at start fails closed: if `system/init` lists any built-in tool or any MCP server other than `bazaar`, the lane goes `Down` and says why. It also goes `Down` when the `bazaar` server is missing from `system/init` (`--safe-mode` can disable MCP), and any hook that administrator-managed policy settings still apply is reported to the operator, because those cannot be switched off from here.
- **Prompt injection** is the same exposure as any model call: the model's output is data, and tool calls pass `ToolDispatcher`, taint and approvals unchanged. Reader sessions for untrusted input work as in doc 15.
- **Argument and path safety.** No shell, no interpolation of agent text into arguments, prompt only on standard input, temp files in a private directory with owner-only access, cleaned in `finally`.
- **Loopback bridge.** Binds to loopback only, random port, per-process bearer token, refuses any other path and any request when no call is live, closes at call end. The token is not a credential for anything else.
- **Secrets.** Planted-key tests (as doc 05 requires) cover logs, spans, usage rows and error text.
- **Binary trust.** The binary path is a setting (default: resolved from `PATH` once at start and pinned). On Windows the real executable is used, not an `npm` shell shim. The module records the file hash in the audit log when it changes.
- **Privacy.** Prompts go to Anthropic under the operator's own plan terms; setup says so (the same note as API key mode, plus the plan's data settings are the operator's).

## 13. Tests

All with a **fake CLI**: a small .NET console program (`Bazaar.Server.Providers.ClaudeCode.FakeCli`, test support only) that reads a script file and writes `stream-json` lines, so no network or subscription is needed in CI.

- argument vector is exactly the table in section 5, and the prompt is not on it;
- text, thinking, tool call, usage and done events map correctly (golden transcripts, including one with unknown events);
- tool call kills the process after the assistant message and emits `ToolUse`; a `tools/call` on the bridge returns the deferred error and is counted;
- rate limit text, `api_retry`, and non-zero exit map to `rate_limited` with and without a reset time;
- account errors suspend the route and never retry;
- cancellation, total timeout, idle timeout and first-event timeout kill the whole tree (the fake spawns a grandchild);
- concurrency cap and hourly cap return `rate_limited` without starting a process;
- environment allowlist (planted `CLAUDE_CODE_OAUTH_TOKEN` and `ANTHROPIC_API_KEY` in the host never reach the child in `login` mode);
- a subscription token in any secret reference is refused;
- config folder excluded from backup list; single-person guard refuses a second human;
- no `init` tool leak: the capability check fails closed;
- architecture tests: the module references Abstractions only; no feature code lands in `Bazaar.Server`.
- An opt-in test category runs against a real `claude` binary and a signed-in config folder on the owner's machine, never in CI.

## 14. Work packages

1. `Bazaar.Server.Gateway.Abstractions` extraction (move four files, fix references, no behaviour change).
2. `...ClaudeCode.Abstractions` and options with start-time validation.
3. Stream parser and event mapper (golden tests).
4. Process runner (Job Object / process group, timeouts, bounded output) and the fake CLI.
5. Definition-only MCP bridge.
6. Adapter, component, settings, health, rate-limit mapping. `ProviderEndpoint.BaseUrl` is required by the gateway, so provider rows for this adapter use the sentinel `cli://claude-code`; the adapter ignores it and reads its own options.
6b. Gateway persistence: a migration widening `adapter` (adds `claude_code_cli`) and `identity` (adds `subscription`) in `gw_provider`, `gw_model` and `gw_usage` (`0002_gateway.sql` lines 8, 28, 77; `common/` is frozen, so its placement needs the doc 04 rule for a change to a core-owned table), the Contracts `GatewayIdentity.Subscription` value, `AdapterKind.ClaudeCodeCli`, and a directory overlay like `AnthropicDirectWiring`.
6b-1. Directory overlay and the `claude` route (built). The owner's first agent is "Claude" and draws from whichever Claude source is available (owner, 2026-11-22). `ClaudeCodeGatewayDirectory` (host composition) overlays one provider row `claude-code-cli` (kind `local`, auth `none`, no secret, base URL `cli://claude-code`), one model row per configured model (family `claude`, identity `subscription`), the route `claude` and, for each model, `claude/<model>`. The `claude` route lists every Claude source in priority order: the CLI's models, then the owner's Anthropic key models, then any members another directory stores under a route named `claude` (so an operator adds a further Claude source, a model row in the `claude` family, without code). All members share the `claude` family, so doc 05's no-cross-family rule holds, and the agent's policy still decides which members it may use. The seeded agent policy (`DevAgentPolicySeed`, for the agents under `Bazaar:AgentResponder:Agents`) allows `claude`, `claude/<default model>` and every configured CLI model, and makes `claude` the default route when the provider is on; the Anthropic key keeps its Haiku-only seed. FreeLLMAPI stays an allowed second route (`agent-default`); the gateway never falls back across families, so switching to it is a route choice and not an automatic failover. The wizard options (name, purpose, endpoints) are the sign-in thread's.
6c. Job Object (Windows, built in the Windows module) and Linux sweep (built). Every child carries the environment entry `BAZAAR_CLAUDE_CODE_RUN=<marker>`, where the marker is the `RunMarker` option or, by default, a hash of the login folder path, so it is stable across restarts of one server and differs between servers. At host start, on Linux only, a hosted service scans `/proc/<pid>/status` and `/proc/<pid>/environ`, and kills (with its tree) every process of the server's own user that carries the marker and is not the server. Unreadable processes are skipped. No command-line marker is used, because the unmodified CLI may reject unknown flags.
7. Admin wiring: guided setup text and the acknowledgement record (rides doc 51). Built: `ClaudeCodeGuidedSetup` runs as data for the admin panel (terms accepted, program found, login folder exists, login owner named, then the settings to apply), stops at the first failing step, never opens a file inside the login folder, and keeps the acceptance time through `IClaudeCodeAcknowledgementStore` (in memory by default). Durable store, built: the host binds it to the settings key `gateway.claude_code.terms_acknowledged_at` (install scope, operator-changed, audited, ISO 8601 UTC, empty means not accepted; the first acceptance stands). **PROPOSED:** the key is not yet in the doc 51 / doc 05 settings tables.

## 15. Open questions

1. **Terms risk: ACCEPTED by the owner, 2026-11-20.** Section 2: the login path is permitted for a person using their own plan in the unmodified CLI, but a round-the-clock agent fleet is outside "ordinary, individual usage" and the account could be limited. Recommended: build it, ship it off by default with the acknowledgement, keep the API key route as the fallback. Counsel review before public release.
2. **Several humans (owner).** v1 serves one login owner. Recommended: keep it that way. Per-human logins and per-human agents are a later design.
3. **CLI-run tool loops (owner).** A coding agent that runs inside Claude Code's own loop (its file and shell tools, in a VM) is a different module with its own approval and taint design. Recommended: not now; the harness in doc 17 stays the coding path.

Unverified until a real binary is run (the owner's machine): the exact `--safe-mode` interaction with `--mcp-config`, whether a rate-limit event exists in the stream, the exact text of usage-limit errors. The tolerant parser and the capability check are built so these are detected, not assumed. `scripts/dev/claude-code-real-cli-check.ps1` runs one short prompt with the provider's exact arguments and prints the three answers (writes only inside its `-WorkDir`, never off C: temp, never reads the login folder).
