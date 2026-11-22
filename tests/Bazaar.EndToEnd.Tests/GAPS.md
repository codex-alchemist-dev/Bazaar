# End-to-end gaps

Found while building the smoke harness (`Bazaar.EndToEnd.Tests`). Each gap is also a skipped test in `GapTests.cs`. The harness
does not work around a gap silently: where it has to stand in for a missing step, the stand-in is named here.

What the harness proves today, on every Linux CI run: the real `Bazaar.Server.Host` process migrates an empty Postgres database,
starts, reports ready, refuses a wrong token, accepts the dev token, lets a headless wire client send a mention in a group chat, has
the agent answer through the real gateway (FreeLLMAPI source pointed at a keyless stub model), writes the reply to the log, and
writes it to the log.

| # | Gap | Stand-in used by the harness | Needed from |
|---|---|---|---|
| 1 | No first-run install path to run. The host starts only from hand-written configuration. The setup wizard (#824, #914, #952 and later), the managed Postgres bundle and service registration are not run end to end. | External Postgres from `BAZAAR_TEST_PG`, `migrate` run by hand, config as command-line keys. | Setup thread, Packaging thread |
| 2 | Nothing creates a company, a group chat or an agent at run time: no wire request, HTTP route or setup step. There is no agent record either; the responder reads its agents from configuration and the policy comes from the dev seed. | Rows for one GC and two members written straight into `gc` and `gc_member`; one agent listed under `Bazaar:AgentResponder:Agents`; policy from `DevAgentPolicySeed`. | Company, Souls and Identity threads. Update 09:16Z: the client thread's #1086 uses `POST /setup/plans` and `/setup/plans/{hash}/apply` with a bootstrap code for company creation; once that merges, the harness should create the company that way. |
| 3 | Sign-in is the dev bearer token (`Bazaar:DevAuth`). Pairing, signed access tokens and refresh (#797, #807, #974) are not exercised, and the dev token is refused outside local development by design. | `Bazaar:DevAuth` with a token from an environment variable. | Remote auth thread. `POST /auth/handoff` (client #1086) is PROPOSED and not built server-side. |
| 4 | The "client" is `WireClient` from `Bazaar.Client.Abstractions` only. No sign-in screen, no create-company screen and no chat view runs headless against a real server. | A scripted `WireClient`. | GUI shell thread (`Avalonia.Headless`) |
| 5 | Mostly done. `BoardTaskTests` runs the board step end to end: the owner assigns a card over the wire, the wake loop runs a turn through the gateway, the agent moves the card to Review with `kanban.transition`, and its reply is posted in the card's GC. Not exercised: the coding harness and a GitHub merge. | The agent row, tool grant, card and its commit link are written by the harness; the agent has no Soul (`Bazaar:Gc:AgentPost:RequireSoulBinding=false`); the model is scripted to call the tool once. | Agents, coding and GitHub threads |
| 6 | ~~Bug: model usage silently dropped~~ **Fixed** in this PR: config-defined providers are written to `gw_provider` at start (`GatewayProviderRowSync`) and the gateway logs a swallowed usage error. The metering test now runs. | None. | Done |
| 7 | The service-host provider reports "no usable provider" on a plain Linux box (systemd not running). Fine for CI, but the log says it at info level, not as a state the operator can act on. | None. | Host thread |
| 8 | `KekConfigured=False` in the start log, yet GC bodies are written (a body key exists some other way). It is unclear whether a server without a configured KEK is meant to start. | None. | Security thread |
| 9 | ~~No `Coding:Harness:Repo` setting.~~ Added by #788 (`ICodingTaskWorkspace`, default repo from the setting). The harness uses a throwaway git repo (`SampleRepo`, one failing `sh` test) as the repo behind a `sample` repo id. The running host does not read the setting for any turn yet. | `SampleRepo` plus the repo source in `TaskWorkspaceFlow`. | Coding thread |
| 10 | Nothing in the running host turns an agent reply into work: nothing calls `ICodingTaskWorkspace.OpenAsync` at turn start. `AcceptanceTests` composes the real flow in process (`TaskWorkspaceFlow`, as `CodingTaskWorkspaceHostTests` does): worktree on `agent/<task>`, commit through `CodingGitService`, doc 17 checks from `Bazaar.Server.Coding`. It applies the diff and runs the tests itself. Push and draft PR are not run (`FakeHostPushPort`). | `TaskWorkspaceFlow`, `AcceptanceTests`. | Agents thread (turn loop), Coding thread (tool runner), GitHub thread (push, PR) |
| 11 | The model is scripted (the stub returns the fix when it sees the task id). No real model has been shown to find the fix. | `StubModelServer.AddScript`. The Claude Code route (#1066) can replace it when merged. | Gateway thread, owner keys |

`AcceptanceTests` proves today: the owner asks over the wire, the agent answers through the real gateway, the diff applies in its own worktree, the repository's test goes from failing to passing, the doc 17 checks raise no flag, one commit lands on `agent/<task>` and main is untouched. A second test shows a "fix" that weakens the test passes the test yet is flagged and not committed.

Open follow-ups for the push lead: gaps 2 and 3 block "create a company" and "sign in" for a real person; gap 4 blocks any claim
that a client works end to end.

The Claude Code provider (#1066) has a fake CLI under `tests/Bazaar.Server.Providers.ClaudeCode.FakeCli`; the harness uses the FreeLLMAPI stub instead, which needs no merged PR. A second run on the `claude-code` route can be added after #1066 merges.
